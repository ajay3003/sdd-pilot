using System.Diagnostics;
using BirkNext.Api.Services.WorkspaceTransfer;

namespace BirkNext.Api.Tests.TestInfrastructure;

/// <summary>
/// pg_dump / pg_restore for backup tests, matching the server <see cref="TestPostgres"/> uses:
/// <list type="number">
/// <item>the tools on PATH (CI images, Linux);</item>
/// <item>else the container named by BIRKNEXT_PG_CONTAINER;</item>
/// <item>else the local development container <c>birknext-studio-local_postgres_1</c> through podman.</item>
/// </list>
/// When none answers, the backup tests are skipped with this reason; they never pass without running.
/// </summary>
public static class PostgresToolsTestGate
{
    private static readonly Lazy<PostgresToolOptions?> Resolved = new(Resolve);

    public static PostgresToolOptions? Options => Resolved.Value;
    public static string? SkipReason => Options is null ? "pg_dump/pg_restore not available (PATH, BIRKNEXT_PG_CONTAINER or the local podman container)." : null;

    private static PostgresToolOptions? Resolve()
    {
        if (Answers("pg_dump", ["--version"])) return new PostgresToolOptions { Mode = "Host" };
        var container = Environment.GetEnvironmentVariable("BIRKNEXT_PG_CONTAINER") is { Length: > 0 } c ? c : "birknext-studio-local_postgres_1";
        return Answers("podman", ["exec", container, "pg_dump", "--version"]) ? new PostgresToolOptions { Mode = "ContainerExec", Container = container, Runtime = "podman" } : null;
    }

    public static PostgresTools Tools() => new(Options ?? throw new InvalidOperationException(SkipReason));

    private static bool Answers(string file, string[] args)
    {
        try
        {
            var info = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in args) info.ArgumentList.Add(a);
            using var p = Process.Start(info);
            if (p is null) return false;
            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            return p.WaitForExit(30_000) && p.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return false; }
    }
}

/// <summary>A fact that needs pg_dump/pg_restore; skipped with the reason when <see cref="PostgresToolsTestGate"/> finds none.</summary>
public sealed class PostgresToolsFactAttribute : FactAttribute
{
    public PostgresToolsFactAttribute()
    {
        if (PostgresToolsTestGate.SkipReason is { } reason) Skip = reason;
    }
}
