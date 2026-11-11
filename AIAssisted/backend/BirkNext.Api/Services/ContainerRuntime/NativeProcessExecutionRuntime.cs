using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using BirkNext.Api.Services.PerformanceTests;
using BirkNext.PerformanceTests;
using Path = System.IO.Path;

namespace BirkNext.Api.Services.ContainerRuntime;

/// <summary>
/// The native Windows runner for the Tester Package (<c>PerformanceTests:Runner = Native</c>): the package's own pinned k6 executable runs as a
/// child process of the backend, through the same <see cref="IContainerExecutionRuntime"/> contract as Podman, so the k6 provider, the generated
/// script, authorization, safety limits, result parsing and evidence stay one implementation.
/// <para>
/// What a process cannot do that a container does is reported, never assumed: there is no filesystem or network isolation (the run directories are
/// passed as host paths and the target is reached from the host itself), memory/CPU/process limits are not applied, a graceful stop is not available
/// (cancellation terminates the process tree, so no partial summary is written) and a CA bundle is refused because k6 on Windows validates
/// certificates with the Windows certificate store and ignores <c>SSL_CERT_FILE</c>.
/// </para>
/// The process gets an allow-listed environment only (never the backend's own environment), arguments as a structured list (no shell), bounded
/// output, and the executable is accepted only when it is the configured, fully-qualified file whose SHA-256 matches the pinned value.
/// </summary>
public sealed class NativeProcessExecutionRuntime(PerformanceTestOptions options, ILogger<NativeProcessExecutionRuntime> logger) : IContainerExecutionRuntime
{
    public string RuntimeId => PerformanceProviderIds.NativeRuntime;
    public string DisplayName => "Bundled k6 (native Windows process)";
    /// <summary>Not used: the process shares the host network, so a loopback target stays loopback.</summary>
    public string HostGatewayAlias => "localhost";
    public bool SharesHostNetwork => true;
    public bool SharesHostFilesystem => true;

    private PerformanceNativeOptions N => options.Native;
    private readonly ConcurrentDictionary<string, Tracked> _running = new(StringComparer.Ordinal);
    private (string Path, DateTime Written, long Length, string Hash)? _hashCache;

    private sealed record Tracked(Process Process, DateTimeOffset StartedAt, string? Component);

    /// <summary>The verified executable: configured, fully qualified, named k6(.exe), present, and matching the pinned SHA-256.</summary>
    public (string? Path, string? Sha256, string? Problem, ProviderAvailability Availability) Executable()
    {
        if (string.IsNullOrWhiteSpace(N.K6Path))
            return (null, null, "No k6 executable is configured (PerformanceTests:Native:K6Path). The Tester Package launcher sets it to the bundled runtime/k6/k6.exe.", ProviderAvailability.Unavailable);
        var path = N.K6Path!;
        if (!Path.IsPathFullyQualified(path) || Path.GetFileName(path) is not ("k6.exe" or "k6"))
            return (null, null, "PerformanceTests:Native:K6Path must be the full path of the bundled k6.exe.", ProviderAvailability.Misconfigured);
        if (!File.Exists(path))
            return (null, null, "Performance testing is unavailable because the bundled k6 executable is missing. Download the Tester Package again.", ProviderAvailability.Unavailable);
        if (string.IsNullOrWhiteSpace(N.K6Sha256) || N.K6Sha256!.Length != 64)
            return (null, null, "The bundled k6 executable has no pinned SHA-256 (PerformanceTests:Native:K6Sha256), so its integrity cannot be verified.", ProviderAvailability.Misconfigured);
        string hash;
        try { hash = Hash(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return (null, null, $"The bundled k6 executable could not be read ({ex.GetType().Name}).", ProviderAvailability.Unavailable); }
        if (!string.Equals(hash, N.K6Sha256, StringComparison.OrdinalIgnoreCase))
            return (null, hash, "The bundled k6 executable does not match its pinned SHA-256: it was changed after packaging and is not used.", ProviderAvailability.Misconfigured);
        return (path, hash, null, ProviderAvailability.Available);
    }

    private string Hash(string path)
    {
        var info = new FileInfo(path);
        if (_hashCache is { } c && c.Path == path && c.Written == info.LastWriteTimeUtc && c.Length == info.Length) return c.Hash;
        using var stream = File.OpenRead(path);
        var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        _hashCache = (path, info.LastWriteTimeUtc, info.Length, hash);
        return hash;
    }

    public Task<PerformanceRuntimeStatus> StatusAsync(CancellationToken ct = default)
    {
        var status = new PerformanceRuntimeStatus { RuntimeId = RuntimeId, DisplayName = DisplayName };
        if (!OperatingSystem.IsWindows())
            return Task.FromResult(status with { Availability = ProviderAvailability.Unavailable, Detail = "The native runner is supported on Windows only (use the container runner elsewhere)." });
        var exe = Executable();
        return Task.FromResult(exe.Problem is { } problem
            ? status with { Availability = exe.Availability, Detail = problem }
            : status with { Availability = ProviderAvailability.Available, Version = "native", Detail = $"Bundled k6 executable verified (sha256 {exe.Sha256![..12]}…)." });
    }

    /// <summary>The "image" of a native run is the verified executable; its digest is the executable's SHA-256.</summary>
    public Task<ContainerImageStatus> ImageAsync(string image, CancellationToken ct = default)
    {
        var exe = Executable();
        return Task.FromResult(exe.Problem is { } problem
            ? new ContainerImageStatus(image, false, null, problem)
            : new ContainerImageStatus(image, true, "sha256:" + exe.Sha256, "The bundled k6 executable is present and verified."));
    }

    public Task<ContainerImageStatus> PullAsync(string image, CancellationToken ct = default) =>
        Task.FromResult(new ContainerImageStatus(image, false, null, "The native runner uses the k6 executable bundled with the Tester Package; nothing is downloaded."));

    /// <summary>
    /// The process environment: an allow-list only. Windows needs SystemRoot/SystemDrive/windir for networking; the profile and temp variables point
    /// at a per-run directory so k6 reads no user configuration. Everything else comes from the spec (already allow-listed by the provider).
    /// </summary>
    internal static Dictionary<string, string> ProcessEnvironment(ContainerRunSpec spec, string home)
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in new[] { "SystemRoot", "SystemDrive", "windir" })
            if (System.Environment.GetEnvironmentVariable(key) is { Length: > 0 } value) env[key] = value;
        foreach (var key in new[] { "TEMP", "TMP", "USERPROFILE", "APPDATA", "LOCALAPPDATA", "HOME" }) env[key] = home;
        foreach (var (k, v) in spec.Environment) env[k] = v;
        foreach (var (k, v) in spec.SecretEnvironment) env[k] = v;
        return env;
    }

    /// <summary>Why a spec cannot run as a native process, or null. Checked before anything starts.</summary>
    internal static string? Refusal(ContainerRunSpec spec)
    {
        if (!ContainerArgumentRules.IsName(spec.Name)) return "Invalid run name.";
        if (spec.Entrypoint is not null) return "The native runner runs only the bundled k6 executable (no other entrypoint).";
        if (spec.Environment.ContainsKey("SSL_CERT_FILE"))
            return "A CA bundle is configured, but k6 on Windows validates certificates with the Windows certificate store and ignores SSL_CERT_FILE. "
                + "Trust the corporate CA in Windows (or remove the bundle setting); TLS verification is never disabled.";
        foreach (var (k, v) in spec.Environment) if (!ContainerArgumentRules.IsEnvironment(k, v)) return $"Invalid environment variable {k}.";
        foreach (var (k, v) in spec.SecretEnvironment) if (!ContainerArgumentRules.IsSecretEnvironment(k, v)) return $"Invalid secret variable {k}.";
        foreach (var m in spec.Mounts)
            if (!ContainerArgumentRules.IsHostPath(m.HostPath) || !(Directory.Exists(m.HostPath) || File.Exists(m.HostPath))) return "A run directory is not an existing absolute path.";
        // A container path left in the arguments means the caller built a container command: it would silently read or write the wrong place.
        if (spec.Command.Any(a => a.Contains("/birknext/", StringComparison.Ordinal))) return "The command refers to container paths; the native runner needs host paths.";
        return null;
    }

    public async Task<ContainerRunOutcome> RunAsync(ContainerRunSpec spec, CancellationToken ct)
    {
        var exe = Executable();
        if (exe.Path is null) return new ContainerRunOutcome(null, "", "", false, false, exe.Problem, true);
        if (Refusal(spec) is { } refusal) return new ContainerRunOutcome(null, "", "", false, false, refusal, true);
        var home = Path.Combine(Path.GetTempPath(), "birknext-k6-native", spec.Name);
        Directory.CreateDirectory(home);
        var workingDirectory = spec.Mounts.FirstOrDefault(m => m.ReadOnly && Directory.Exists(m.HostPath))?.HostPath ?? home;
        var info = new ProcessStartInfo { FileName = exe.Path, WorkingDirectory = workingDirectory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = false };
        foreach (var a in spec.Command) info.ArgumentList.Add(a);
        info.Environment.Clear();
        foreach (var (k, v) in ProcessEnvironment(spec, home)) info.Environment[k] = v;
        using var process = new Process { StartInfo = info };
        var stdout = new Bounded(spec.MaxOutputBytes);
        var stderr = new Bounded(spec.MaxOutputBytes);
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.Append(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.Append(e.Data); };
        IReadOnlyList<string> unapplied = ["memory", "cpu", "pids"];
        try
        {
            try { if (!process.Start()) return new ContainerRunOutcome(null, "", "", false, false, "The k6 process could not be started.", true) { UnappliedLimits = unapplied }; }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
            {
                // Typical on managed PCs: an application-control policy (AppLocker/WDAC) or antivirus blocked the executable. Reported, never bypassed.
                return new ContainerRunOutcome(null, "", "", false, false, $"Windows did not allow the bundled k6 executable to start ({ex.GetType().Name}). An application-control or antivirus policy may block it.", true)
                    { UnappliedLimits = unapplied };
            }
            _running[spec.Name] = new Tracked(process, DateTimeOffset.UtcNow, spec.Labels.TryGetValue("birknext.component", out var component) ? component : null);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            using var timeout = new CancellationTokenSource(spec.Timeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            try
            {
                await process.WaitForExitAsync(linked.Token);
                process.WaitForExit();
                return new ContainerRunOutcome(process.ExitCode, stdout.ToString(), stderr.ToString(), false, false, null, true) { UnappliedLimits = unapplied };
            }
            catch (OperationCanceledException)
            {
                var gone = Kill(process);
                return new ContainerRunOutcome(null, stdout.ToString(), stderr.ToString(), timeout.IsCancellationRequested && !ct.IsCancellationRequested, ct.IsCancellationRequested, null, gone)
                    { UnappliedLimits = unapplied };
            }
        }
        finally
        {
            _running.TryRemove(spec.Name, out _);
            try { Directory.Delete(home, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { logger.LogDebug("Native k6 home could not be removed: {Type}", ex.GetType().Name); }
        }
    }

    /// <summary>Terminates only a process this runtime started (by its own handle), never any other k6.</summary>
    private static bool Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        try { return process.WaitForExit(10_000); } catch (InvalidOperationException) { return true; }
    }

    public Task<bool> RemoveAsync(string name, CancellationToken ct = default) =>
        Task.FromResult(_running.TryGetValue(name, out var t) ? Kill(t.Process) : true);

    /// <summary>Windows has no SIGTERM for a console child: a graceful stop is not available, so the caller terminates the run (no partial summary).</summary>
    public Task<bool> StopAsync(string name, int graceSeconds, CancellationToken ct = default) => Task.FromResult(false);

    public Task<IReadOnlyList<string>> ListManagedAsync(string component, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<string>>(_running.Where(kv => kv.Value.Component == component).Select(kv => kv.Key).ToList());

    /// <summary>No cgroup controllers: process resource limits are not applied by the native runner.</summary>
    public Task<IReadOnlySet<string>> ControllersAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlySet<string>>(new HashSet<string>());

    public Task<ContainerStatsSnapshot> StatsAsync(string name, CancellationToken ct = default)
    {
        if (!_running.TryGetValue(name, out var t)) return Task.FromResult(new ContainerStatsSnapshot(null, null, null, null, "Not a running BirkNext k6 process (the native runner observes only its own processes)."));
        try { t.Process.Refresh(); return Task.FromResult(new ContainerStatsSnapshot(null, t.Process.WorkingSet64, null, null, null)); }
        catch (InvalidOperationException) { return Task.FromResult(new ContainerStatsSnapshot(null, null, null, null, "The process has exited.")); }
    }

    public Task<ContainerInstanceInfo?> InspectAsync(string name, CancellationToken ct = default)
    {
        if (!_running.TryGetValue(name, out var t)) return Task.FromResult<ContainerInstanceInfo?>(null);
        bool running;
        try { running = !t.Process.HasExited; } catch (InvalidOperationException) { running = false; }
        return Task.FromResult<ContainerInstanceInfo?>(new ContainerInstanceInfo("pid-" + t.Process.Id, t.StartedAt.ToString("o"), 0, running, "k6.exe (bundled)", null, null));
    }

    private sealed class Bounded(int max)
    {
        private readonly StringBuilder _sb = new();
        private bool _truncated;
        public void Append(string line) { lock (_sb) { if (_sb.Length + line.Length + 1 > max) { _truncated = true; return; } _sb.AppendLine(line); } }
        public override string ToString() { lock (_sb) return _truncated ? _sb + "[output truncated]" : _sb.ToString(); }
    }
}
