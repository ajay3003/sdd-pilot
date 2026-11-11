using Path = System.IO.Path;
using System.Diagnostics;
using System.Text;
using Npgsql;

namespace BirkNext.Api.Services.WorkspaceTransfer;

/// <summary>
/// How BirkNext reaches pg_dump / pg_restore (configuration <c>Backup:PostgresTools</c>):
/// <list type="bullet">
/// <item><c>Mode = Host</c> (default): the tools on PATH, or <c>BinDirectory</c>. Host, port, user and database from the BirkNext connection string;
/// the password only in the child's environment (PGPASSWORD), never on the command line.</item>
/// <item><c>Mode = ContainerExec</c>: <c>{Runtime} exec -i {Container} pg_dump …</c> (Runtime defaults to podman). The tools inside the database
/// container use its local socket, so no password is passed at all. The database name and user still come from the connection string.</item>
/// </list>
/// Arguments are always an argument list (no shell). Output is streamed; stderr is captured (bounded) for the error message.
/// </summary>
public sealed class PostgresToolOptions
{
    public string Mode { get; set; } = "Host";
    public string? BinDirectory { get; set; }
    public string Runtime { get; set; } = "podman";
    public string? Container { get; set; }
    public int TimeoutMinutes { get; set; } = 60;
}

public sealed class PostgresTools(PostgresToolOptions options)
{
    public PostgresToolOptions Options => options;

    public bool ContainerMode => string.Equals(options.Mode, "ContainerExec", StringComparison.OrdinalIgnoreCase);

    public string? ConfigurationProblem() =>
        ContainerMode && string.IsNullOrWhiteSpace(options.Container) ? "Backup:PostgresTools:Container is required in ContainerExec mode."
        : !ContainerMode && !string.Equals(options.Mode, "Host", StringComparison.OrdinalIgnoreCase) ? $"Unknown Backup:PostgresTools:Mode \"{options.Mode}\" (Host or ContainerExec)."
        : null;

    /// <summary>"pg_dump (PostgreSQL) 16.13 …", or null when the tool cannot be started.</summary>
    public async Task<string?> VersionAsync(string tool, CancellationToken ct)
    {
        try
        {
            var (exit, stdout, _) = await RunCapturedAsync(tool, ["--version"], connection: null, ct);
            return exit == 0 ? stdout.Trim() : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return null; }
    }

    /// <summary>
    /// Container mode only: runs one read-only query with psql inside the configured container (local socket, the connection string's user and
    /// database) and returns its single value, or null when the container cannot be queried. Used to prove the container is BirkNext's server.
    /// </summary>
    public async Task<string?> QueryInContainerAsync(NpgsqlConnectionStringBuilder connection, string sql, CancellationToken ct)
    {
        if (!ContainerMode) return null;
        try
        {
            var (exit, stdout, _) = await RunCapturedAsync("psql",
                ["--no-psqlrc", "--tuples-only", "--no-align", $"--username={connection.Username}", $"--dbname={connection.Database}", $"--command={sql}"], connection: null, ct);
            return exit == 0 ? stdout.Trim() : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return null; }
    }

    /// <summary>pg_dump -Fc of the whole database at the exported snapshot, streamed into <paramref name="output"/>.</summary>
    public Task DumpAsync(NpgsqlConnectionStringBuilder connection, string snapshotId, Stream output, CancellationToken ct) =>
        RunStreamingAsync("pg_dump", ["--format=custom", "--no-owner", "--no-privileges", $"--snapshot={snapshotId}", .. ConnectionArgs(connection)], connection, stdin: null, stdout: output, ct);

    /// <summary>pg_restore of a custom-format dump from <paramref name="input"/> into an EXISTING, EMPTY database (the caller creates it).</summary>
    public Task RestoreAsync(NpgsqlConnectionStringBuilder target, Stream input, CancellationToken ct) =>
        RunStreamingAsync("pg_restore", ["--no-owner", "--no-privileges", "--exit-on-error", "--single-transaction", .. ConnectionArgs(target)], target, stdin: input, stdout: null, ct);

    private string[] ConnectionArgs(NpgsqlConnectionStringBuilder c) => ContainerMode
        ? [$"--username={c.Username}", $"--dbname={c.Database}"]
        : [$"--host={c.Host}", $"--port={c.Port}", $"--username={c.Username}", $"--dbname={c.Database}", "--no-password"];

    private ProcessStartInfo Start(string tool, IEnumerable<string> args, NpgsqlConnectionStringBuilder? connection)
    {
        ProcessStartInfo info;
        if (ContainerMode)
        {
            info = new ProcessStartInfo(options.Runtime);
            foreach (var a in new[] { "exec", "-i", options.Container!, tool }) info.ArgumentList.Add(a);
        }
        else
        {
            info = new ProcessStartInfo(string.IsNullOrWhiteSpace(options.BinDirectory) ? tool : Path.Combine(options.BinDirectory, tool));
            if (connection?.Password is { Length: > 0 } password) info.Environment["PGPASSWORD"] = password;
        }
        foreach (var a in args) info.ArgumentList.Add(a);
        info.UseShellExecute = false;
        info.RedirectStandardError = true;
        info.RedirectStandardOutput = true;
        info.RedirectStandardInput = true;
        info.CreateNoWindow = true;
        return info;
    }

    private async Task<(int Exit, string Stdout, string Stderr)> RunCapturedAsync(string tool, IEnumerable<string> args, NpgsqlConnectionStringBuilder? connection, CancellationToken ct)
    {
        using var output = new MemoryStream();
        var (exit, stderr) = await ExecuteAsync(Start(tool, args, connection), stdin: null, output, ct);
        return (exit, Encoding.UTF8.GetString(output.ToArray()), stderr);
    }

    private async Task RunStreamingAsync(string tool, IEnumerable<string> args, NpgsqlConnectionStringBuilder connection, Stream? stdin, Stream? stdout, CancellationToken ct)
    {
        var (exit, stderr) = await ExecuteAsync(Start(tool, args, connection), stdin, stdout, ct);
        if (exit != 0) throw new PostgresToolException(tool, exit, Sanitize(stderr, connection));
    }

    private async Task<(int Exit, string Stderr)> ExecuteAsync(ProcessStartInfo info, Stream? stdin, Stream? stdout, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(Math.Clamp(options.TimeoutMinutes, 1, 24 * 60)));
        using var process = Process.Start(info) ?? throw new InvalidOperationException($"{info.FileName} could not be started.");
        var errors = new StringBuilder();
        var stderrTask = Task.Run(async () =>
        {
            var buffer = new char[4096];
            int n;
            while ((n = await process.StandardError.ReadAsync(buffer, timeout.Token)) > 0)
                if (errors.Length < 16_384) errors.Append(buffer, 0, Math.Min(n, 16_384 - errors.Length));
        }, timeout.Token);
        var stdoutTask = stdout is null
            ? process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, timeout.Token)
            : process.StandardOutput.BaseStream.CopyToAsync(stdout, 81920, timeout.Token);
        try
        {
            if (stdin is not null) await stdin.CopyToAsync(process.StandardInput.BaseStream, 81920, timeout.Token);
            process.StandardInput.Close();
            await Task.WhenAll(stdoutTask, stderrTask);
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }
        catch (IOException) when (process.HasExited)
        {
            // The tool exited early (e.g. pg_restore refused the input): report its exit code and stderr, not the broken pipe.
            await process.WaitForExitAsync(CancellationToken.None);
        }
        return (process.ExitCode, errors.ToString());
    }

    private static string Sanitize(string stderr, NpgsqlConnectionStringBuilder connection) =>
        connection.Password is { Length: > 0 } p ? stderr.Replace(p, "***", StringComparison.Ordinal) : stderr;
}

public sealed class PostgresToolException(string tool, int exitCode, string stderr)
    : Exception($"{tool} failed (exit {exitCode}): {(stderr.Length > 600 ? stderr[..600] + "…" : stderr).Trim()}")
{
    public string Tool { get; } = tool;
    public int ExitCode { get; } = exitCode;
}
