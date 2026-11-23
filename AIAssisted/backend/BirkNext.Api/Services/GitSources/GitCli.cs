using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using BirkNext.GitSources;
using Path = System.IO.Path;

namespace BirkNext.Api.Services.GitSources;

/// <summary>A classified Git failure (never collapsed into "connection failed"); the message is the plain-language description.</summary>
public sealed class GitProviderException(GitFailureKind kind, string? detail = null) : Exception(GitSourcePolicy.Describe(kind) + (detail is null ? "" : $" ({detail})"))
{
    public GitFailureKind Kind { get; } = kind;
}

/// <summary>The Git for Windows (or any git) installation BirkNext found: never installed or upgraded by BirkNext.</summary>
public sealed record GitInstallation(bool Found, string? Path, string? Version, IReadOnlyList<string> CredentialHelpers, string? Problem);

/// <summary>The outcome of one git process. Output is kept bounded; it is never logged in full (it may name URLs or paths).</summary>
public sealed record GitRunResult(int ExitCode, string StdOut, string StdErr, bool TimedOut, bool Cancelled)
{
    public bool Ok => ExitCode == 0 && !TimedOut && !Cancelled;
}

/// <summary>
/// How git's own configuration is used. <see cref="Isolated"/>: no global or system configuration at all (no filter drivers, hooks or credential
/// helpers a configuration could add) — for everything that only reads BirkNext-owned repositories. <see cref="User"/>: the person's global
/// configuration (credential helper such as Git Credential Manager, safe.directory, proxy, SSH command) — for network operations and for reading
/// a repository the person owns.
/// </summary>
public enum GitConfigMode { Isolated, User }

public interface IGitCli
{
    GitInstallation Detect();
    /// <param name="subcommand">One of <see cref="GitCli.AllowedSubcommands"/>; anything else is refused before a process starts.</param>
    /// <param name="args">Further arguments, each passed as its own argument (never through a shell).</param>
    /// <param name="stdout">When given, standard output is copied there (binary, e.g. an archive) with at most <paramref name="maxOutputBytes"/>.</param>
    Task<GitRunResult> RunAsync(string subcommand, IReadOnlyList<string> args, string? workingDirectory, GitConfigMode mode, TimeSpan timeout, CancellationToken ct,
        IReadOnlyList<string>? configOverrides = null, Stream? stdout = null, long maxOutputBytes = 0);
}

/// <summary>
/// Runs the person's git with an argument list (System.Diagnostics.Process.ArgumentList — no shell, no string interpolation into a command line).
/// Every call: only allowed subcommands; repository hooks disabled (core.hooksPath → an empty BirkNext folder); fsmonitor off; submodules and LFS
/// downloads not followed; ext:: and (unless a local repository is read on purpose) file:// transports refused; no hidden terminal prompt
/// (GIT_TERMINAL_PROMPT=0) — Git Credential Manager still shows its own sign-in window when it needs one; bounded time; cancellation and timeouts
/// kill the whole process tree. Never pushes: there is no write subcommand in the allow-list.
/// </summary>
public sealed class GitCli(ILogger<GitCli>? logger = null, string? stateDirectory = null, string? executable = null) : IGitCli
{
    /// <summary>Read-only or BirkNext-owned-storage subcommands only. No push, no remote branch change, no checkout of a person's working tree.</summary>
    public static readonly IReadOnlySet<string> AllowedSubcommands = new HashSet<string>(StringComparer.Ordinal)
    {
        "version", "config", "init", "fetch", "ls-remote", "rev-parse", "for-each-ref", "symbolic-ref", "archive", "cat-file", "log", "status",
        "remote", "check-ref-format",
        // Only in the BirkNext-owned bare repository (Isolated configuration): removes a branch that is no longer selected (2026-11-23).
        "update-ref",
    };

    private readonly string _state = stateDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BirkNext", "git");
    private GitInstallation? _detected;

    public string HooksDirectory => Path.Combine(_state, "no-hooks");
    public string EmptyConfig => Path.Combine(_state, "empty.gitconfig");

    /// <summary>The settings every call carries (before the subcommand).</summary>
    public IReadOnlyList<string> HardenedConfig() =>
    [
        "-c", $"core.hooksPath={HooksDirectory}", "-c", "core.fsmonitor=false", "-c", "submodule.recurse=false", "-c", "fetch.recurseSubmodules=false",
        "-c", "protocol.ext.allow=never", "-c", "credential.interactive=auto",
    ];

    public GitInstallation Detect()
    {
        if (_detected is { } known) return known;
        var path = executable ?? Locate();
        if (path is null)
            return _detected = new(false, null, null, [], "Git was not found. Install Git for Windows (https://git-scm.com/download/win), then restart BirkNext.");
        try
        {
            var version = RunSync(path, ["--version"], TimeSpan.FromSeconds(15)).Trim();
            var helpers = RunSync(path, ["config", "--get-all", "credential.helper"], TimeSpan.FromSeconds(15))
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                // The helper's name only (e.g. "manager"): a helper line could carry arguments, which are not shown.
                .Select(h => h.Split(' ', 2)[0]).Where(h => h.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return _detected = new(true, path, version.StartsWith("git version ", StringComparison.Ordinal) ? version["git version ".Length..] : version, helpers, null);
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or IOException)
        {
            return _detected = new(false, path, null, [], "Git was found but could not be started.");
        }
    }

    private static string RunSync(string path, string[] args, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo(path) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("git did not start");
        var output = p.StandardOutput.ReadToEndAsync();
        if (!p.WaitForExit((int)timeout.TotalMilliseconds)) { try { p.Kill(true); } catch (InvalidOperationException) { } return ""; }
        return output.Result;
    }

    /// <summary>git on PATH, else the default Git for Windows locations. Never downloads anything.</summary>
    private static string? Locate()
    {
        var name = OperatingSystem.IsWindows() ? "git.exe" : "git";
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try { var candidate = Path.Combine(dir.Trim('"'), name); if (File.Exists(candidate)) return candidate; }
            catch (ArgumentException) { }
        }
        if (!OperatingSystem.IsWindows()) return null;
        foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs") })
        {
            var candidate = Path.Combine(root, "Git", "cmd", "git.exe");
            if (root.Length > 0 && File.Exists(candidate)) return candidate;
        }
        return null;
    }

    public async Task<GitRunResult> RunAsync(string subcommand, IReadOnlyList<string> args, string? workingDirectory, GitConfigMode mode, TimeSpan timeout, CancellationToken ct,
        IReadOnlyList<string>? configOverrides = null, Stream? stdout = null, long maxOutputBytes = 0)
    {
        if (!AllowedSubcommands.Contains(subcommand)) throw new InvalidOperationException($"git {subcommand} is not allowed.");
        var install = Detect();
        if (!install.Found || install.Path is null) throw new GitProviderException(GitFailureKind.GitMissing);
        Directory.CreateDirectory(HooksDirectory);
        // A tiny local file, written without the caller's token: an already cancelled call ends below as Cancelled instead of throwing here.
        if (!File.Exists(EmptyConfig)) await File.WriteAllTextAsync(EmptyConfig, "", CancellationToken.None);

        var psi = new ProcessStartInfo(install.Path)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = workingDirectory ?? _state,
        };
        foreach (var a in HardenedConfig()) psi.ArgumentList.Add(a);
        foreach (var c in configOverrides ?? []) { psi.ArgumentList.Add("-c"); psi.ArgumentList.Add(c); }
        psi.ArgumentList.Add(subcommand);
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";        // never a hidden terminal prompt; GCM's own window still appears when needed
        psi.Environment["GCM_INTERACTIVE"] = "auto";
        psi.Environment["GIT_LFS_SKIP_SMUDGE"] = "1";          // LFS pointers stay pointers: no implicit LFS download
        psi.Environment["GIT_OPTIONAL_LOCKS"] = "0";           // reading a person's repository never rewrites its index
        psi.Environment.Remove("GIT_DIR");
        psi.Environment.Remove("GIT_WORK_TREE");
        if (mode == GitConfigMode.Isolated)
        {
            psi.Environment["GIT_CONFIG_GLOBAL"] = EmptyConfig;
            psi.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        }
        // SSH: never wait for a hidden host-key or passphrase prompt. Known-host verification stays exactly as configured (BatchMode refuses an
        // unknown host instead of asking; it never accepts one). A person's own core.sshCommand / GIT_SSH_COMMAND is respected.
        if (Environment.GetEnvironmentVariable("GIT_SSH_COMMAND") is null) psi.Environment["GIT_SSH_COMMAND"] = "ssh -o BatchMode=yes";

        using var process = new Process { StartInfo = psi };
        try { process.Start(); }
        catch (Win32Exception) { throw new GitProviderException(GitFailureKind.GitMissing); }

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        var errTask = ReadBoundedAsync(process.StandardError.BaseStream, 64 * 1024, null, CancellationToken.None);
        var outTask = ReadBoundedAsync(process.StandardOutput.BaseStream, stdout is null ? 4 * 1024 * 1024 : maxOutputBytes, stdout, CancellationToken.None);
        var (timedOut, cancelled) = (false, false);
        try { await process.WaitForExitAsync(linked.Token); }
        catch (OperationCanceledException)
        {
            timedOut = timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested;
            cancelled = ct.IsCancellationRequested;
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            try { await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)); } catch (TimeoutException) { }
        }
        var (outText, outTooLarge) = await outTask;
        var (errText, _) = await errTask;
        if (outTooLarge) { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } throw new GitProviderException(GitFailureKind.TooLarge); }
        var exit = timedOut || cancelled ? -1 : process.ExitCode;
        if (exit != 0) logger?.LogInformation("git {Subcommand} ended with {Exit}{Timeout}{Cancelled}", subcommand, exit, timedOut ? " (timed out)" : "", cancelled ? " (cancelled)" : "");
        return new(exit, outText, errText, timedOut, cancelled);
    }

    private static async Task<(string Text, bool TooLarge)> ReadBoundedAsync(Stream source, long max, Stream? copyTo, CancellationToken ct)
    {
        var buffer = new byte[81920];
        var text = copyTo is null ? new MemoryStream() : null;
        long total = 0;
        var tooLarge = false;
        int read;
        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            total += read;
            if (max > 0 && total > max) { tooLarge = true; continue; } // keep draining so the process can exit
            if (copyTo is not null) await copyTo.WriteAsync(buffer.AsMemory(0, read), ct);
            else text!.Write(buffer, 0, read);
        }
        return (text is null ? "" : Encoding.UTF8.GetString(text.ToArray()), tooLarge);
    }

    /// <summary>
    /// What a failed git call means, from git's and the credential helper's messages (the text itself is never shown or logged: it may contain a
    /// URL). Authentication required, cancelled and denied stay apart from "not found", "network unavailable" and "timed out".
    /// </summary>
    public static GitFailureKind Classify(GitRunResult r)
    {
        if (r.TimedOut) return GitFailureKind.TimedOut;
        if (r.Cancelled) return GitFailureKind.Cancelled;
        var e = r.StdErr;
        bool Has(string s) => e.Contains(s, StringComparison.OrdinalIgnoreCase);
        if (Has("user canceled") || Has("user cancelled") || Has("operation was canceled") || Has("authentication was cancelled") || Has("authentication canceled"))
            return GitFailureKind.AuthenticationCancelled;
        if (Has("terminal prompts disabled") || Has("could not read username") || Has("could not read password") || Has("authentication failed")
            || Has("invalid credentials") || Has("no credentials"))
            return GitFailureKind.SignInRequired;
        if (Has("403") || Has("permission denied") || Has("access denied") || Has("not authorized") || Has("unauthorized"))
            return GitFailureKind.AccessDenied;
        if (Has("host key verification failed")) return GitFailureKind.HostKeyNotTrusted;
        if (Has("repository not found") || Has("not found") || Has("does not appear to be a git repository") || Has("not a git repository"))
            return GitFailureKind.RepositoryNotFound;
        if (Has("could not resolve host") || Has("failed to connect") || Has("unable to access") || Has("connection timed out") || Has("network is unreachable")
            || Has("connection refused"))
            return GitFailureKind.Unreachable;
        if (Has("dubious ownership")) return GitFailureKind.UntrustedRepository;
        return GitFailureKind.Unexpected;
    }
}
