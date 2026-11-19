using System.Diagnostics;
using System.Text.Json;
using BirkNext.Launcher;

namespace BirkNext.Updater;

/// <summary>
/// The BirkNext instance of one data folder, seen from the updater. Running = a launcher holds the data folder's instance lock (the same named
/// mutex Start-BirkNext.exe takes); a port or a session file alone proves nothing. Stopping uses the launcher's own stop request — the running
/// launcher stops its frontend, backend and database in order — and is confirmed only when the lock is released. The updater never terminates a
/// process: if BirkNext does not stop, the update stops and nothing is changed.
/// </summary>
public sealed class InstanceControl(string dataRoot)
{
    public string DataRoot { get; } = Path.GetFullPath(dataRoot);
    private string RuntimeState => Path.Combine(DataRoot, "runtime-state");
    public string SessionFile => Path.Combine(RuntimeState, "session.json");
    public string StopRequestFile => Path.Combine(RuntimeState, "stop-request.json");
    public string LastRunFile => Path.Combine(RuntimeState, "last-run.json");
    public string LastErrorFile => Path.Combine(RuntimeState, "last-startup-error.json");
    public string BackupsFolder => Path.Combine(DataRoot, "backups");

    public static string DefaultDataRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BirkNext", "TesterNative");

    public bool IsRunning => SingleInstance.IsHeld(DataRoot);

    /// <summary>The build that last opened this data folder's database (the launcher's record), or null for a data folder never used.</summary>
    public PackageVersion? LastUsedBuild()
    {
        try
        {
            if (!File.Exists(LastRunFile)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(LastRunFile));
            return doc.RootElement.TryGetProperty("build", out var b) ? PackageVersion.FromInformational(b.GetString()) : null;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>The running BirkNext's address, from its session record (null when not running or not ready yet).</summary>
    public string? RunningUrl()
    {
        try
        {
            if (!File.Exists(SessionFile)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(SessionFile));
            return doc.RootElement.TryGetProperty("frontendUrl", out var u) ? u.GetString() : null;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Asks the running BirkNext to stop and waits until its instance lock is released (true) or the time is up (false).</summary>
    public async Task<bool> StopAsync(TimeSpan timeout, CancellationToken ct)
    {
        if (!IsRunning) return true;
        Directory.CreateDirectory(RuntimeState);
        var temp = StopRequestFile + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(new { requestedAt = DateTimeOffset.UtcNow, by = Environment.ProcessId, reason = "update" }), ct);
        File.Move(temp, StopRequestFile, overwrite: true);
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < timeout)
        {
            if (!IsRunning) return true;
            await Task.Delay(500, ct);
        }
        // Not stopped: the request is withdrawn so it cannot stop BirkNext later, at a moment the tester does not expect.
        try { File.Delete(StopRequestFile); } catch (IOException) { }
        return !IsRunning;
    }

    /// <summary>
    /// Starts the installed Start-BirkNext.exe in its own window (the tester's normal BirkNext window) and waits until it is ready (its session
    /// record appears), fails (it exits), or the time is up. The first start of a new version takes the verified pre-upgrade backup and the backend
    /// applies the database updates before it is ready, so the wait is generous.
    /// </summary>
    public async Task<(bool Ready, string? Url, string? Problem)> StartAsync(string installation, bool customDataRoot, bool noBrowser, TimeSpan timeout, CancellationToken ct)
    {
        var exe = Path.Combine(installation, "Start-BirkNext.exe");
        var psi = new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = installation };
        if (customDataRoot) { psi.ArgumentList.Add("--data-dir"); psi.ArgumentList.Add(DataRoot); }
        if (noBrowser) psi.ArgumentList.Add("--no-browser");
        var before = File.Exists(LastErrorFile) ? File.GetLastWriteTimeUtc(LastErrorFile) : DateTime.MinValue;
        using var process = Process.Start(psi);
        if (process is null) return (false, null, "Start-BirkNext.exe did not start.");
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < timeout)
        {
            if (RunningUrl() is { } url && IsRunning) return (true, url, null);
            if (process.HasExited)
            {
                var problem = File.Exists(LastErrorFile) && File.GetLastWriteTimeUtc(LastErrorFile) > before ? ReadLastError() : null;
                return (false, null, problem ?? $"Start-BirkNext.exe ended (exit code {process.ExitCode}) before BirkNext was ready.");
            }
            await Task.Delay(1000, ct);
        }
        return (false, null, "BirkNext did not report ready in time. Its window shows what it is doing.");
    }

    private string? ReadLastError()
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(LastErrorFile));
            var r = doc.RootElement;
            return r.TryGetProperty("message", out var m) ? m.GetString() + (r.TryGetProperty("action", out var a) ? $" {a.GetString()}" : "") : null;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }
}
