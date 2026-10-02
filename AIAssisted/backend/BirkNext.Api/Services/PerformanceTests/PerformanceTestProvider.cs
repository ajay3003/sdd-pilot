using Path = System.IO.Path;
using System.Diagnostics;
using System.Text;
using BirkNext.PerformanceTests;

namespace BirkNext.Api.Services.PerformanceTests;

/// <summary>
/// Backend configuration (<c>PerformanceTests</c> section). The maximums are hard ceilings: a definition can only be stricter. There is
/// deliberately no setting for credentials or extra command-line arguments.
/// </summary>
public sealed record PerformanceTestOptions
{
    public int MaxVirtualUsers { get; init; } = 50;
    public double MaxRequestsPerSecond { get; init; } = 50;
    public int MaxDurationSeconds { get; init; } = 30 * 60;
    public int MaxSoakDurationSeconds { get; init; } = 60 * 60;
    public long MaxTotalRequests { get; init; } = 200_000;
    public bool AllowStressTest { get; init; } = true;
    public bool AllowSoakTest { get; init; } = true;
    /// <summary>Container execution of providers (Podman). k6 is never part of the BirkNext images or repository.</summary>
    public PerformanceContainerOptions Container { get; init; } = new();
    /// <summary>Resource Stability observation: approved targets, sampling bounds and the per-component sample cap.</summary>
    public BirkNext.Api.Services.PerformanceTests.Resources.PerformanceResourceOptions Resources { get; init; } = new();
    /// <summary>Extra time beyond the workload before the provider process is killed.</summary>
    public int ProviderTimeoutGraceSeconds { get; init; } = 120;
    public int MaxProviderOutputBytes { get; init; } = 64 * 1024;
    /// <summary>Hosts that are always refused (e.g. known production hosts), exact or "*.suffix".</summary>
    public IReadOnlyList<string> BlockedHosts { get; init; } = [];
    /// <summary>When non-empty, only these hosts (exact or "*.suffix") may be load-tested. Loopback is always allowed for local targets.</summary>
    public IReadOnlyList<string> AllowedHosts { get; init; } = [];

    public static PerformanceTestOptions From(IConfiguration configuration)
    {
        var s = configuration.GetSection("PerformanceTests");
        var d = new PerformanceTestOptions();
        return new PerformanceTestOptions
        {
            MaxVirtualUsers = Math.Clamp(s.GetValue("MaxVirtualUsers", d.MaxVirtualUsers), 1, 1000),
            MaxRequestsPerSecond = Math.Clamp(s.GetValue("MaxRequestsPerSecond", d.MaxRequestsPerSecond), 0.1, 2000),
            MaxDurationSeconds = Math.Clamp(s.GetValue("MaxDurationSeconds", d.MaxDurationSeconds), 10, 4 * 3600),
            MaxSoakDurationSeconds = Math.Clamp(s.GetValue("MaxSoakDurationSeconds", d.MaxSoakDurationSeconds), 60, 12 * 3600),
            MaxTotalRequests = Math.Clamp(s.GetValue("MaxTotalRequests", d.MaxTotalRequests), 10, 10_000_000),
            AllowStressTest = s.GetValue("AllowStressTest", d.AllowStressTest),
            AllowSoakTest = s.GetValue("AllowSoakTest", d.AllowSoakTest),
            Container = PerformanceContainerOptions.From(s.GetSection("Container")),
            Resources = BirkNext.Api.Services.PerformanceTests.Resources.PerformanceResourceOptions.From(s.GetSection("Resources")),
            ProviderTimeoutGraceSeconds = Math.Clamp(s.GetValue("ProviderTimeoutGraceSeconds", d.ProviderTimeoutGraceSeconds), 10, 1800),
            MaxProviderOutputBytes = Math.Clamp(s.GetValue("MaxProviderOutputBytes", d.MaxProviderOutputBytes), 1024, 1024 * 1024),
            BlockedHosts = s.GetSection("BlockedHosts").GetChildren().Select(c => c.Value ?? "").Where(v => v.Length > 0).ToList(),
            AllowedHosts = s.GetSection("AllowedHosts").GetChildren().Select(c => c.Value ?? "").Where(v => v.Length > 0).ToList(),
        };
    }

    public PerformanceSafetyLimits Limits(PerformanceTestSafetyPolicy? policy) => new(
        Math.Min(MaxVirtualUsers, policy?.MaxVirtualUsers ?? int.MaxValue),
        Math.Min(MaxRequestsPerSecond, policy?.MaxRequestsPerSecond ?? double.MaxValue),
        Math.Min(MaxDurationSeconds, policy?.MaxDurationSeconds ?? int.MaxValue),
        Math.Min(MaxSoakDurationSeconds, policy?.MaxDurationSeconds ?? int.MaxValue),
        Math.Min(MaxTotalRequests, policy?.MaxTotalRequests ?? long.MaxValue),
        AllowStressTest, AllowSoakTest);
}

/// <summary>
/// <c>PerformanceTests:Container</c>: the pinned k6 image (never ":latest"), whether BirkNext may pull it on an explicit action (default no —
/// as for the pinned ZAP image, nothing is downloaded at run time), networks, an optional CA bundle and allow-listed proxy settings.
/// The host environment is never passed through.
/// </summary>
public sealed record PerformanceContainerOptions
{
    public const string DefaultImage = "docker.io/grafana/k6:1.0.0";
    public string? CliPath { get; init; }
    public string Image { get; init; } = DefaultImage;
    public bool AllowImagePull { get; init; }
    /// <summary>Network for external targets; null = Podman's default bridge (ordinary outbound access).</summary>
    public string? Network { get; init; }
    /// <summary>Target host → network, for targets that are containers on a BirkNext Podman network (reached by container DNS name).</summary>
    public IReadOnlyDictionary<string, string> TargetNetworks { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public int MemoryMegabytes { get; init; } = 1024;
    public double Cpus { get; init; } = 2;
    /// <summary>PEM bundle (corporate + public roots) mounted read-only and used via SSL_CERT_FILE. TLS verification is never disabled.</summary>
    public string? CaBundlePath { get; init; }
    public string? HttpProxy { get; init; }
    public string? HttpsProxy { get; init; }
    public string? NoProxy { get; init; }
    /// <summary>How long a successful container-network check stays valid for readiness.</summary>
    public int NetworkCheckValidMinutes { get; init; } = 15;

    public static PerformanceContainerOptions From(IConfigurationSection s)
    {
        var d = new PerformanceContainerOptions();
        return new PerformanceContainerOptions
        {
            CliPath = s.GetValue<string?>("CliPath"), Image = s.GetValue<string?>("Image") is { Length: > 0 } image ? image : d.Image,
            AllowImagePull = s.GetValue("AllowImagePull", false), Network = s.GetValue<string?>("Network"),
            TargetNetworks = s.GetSection("TargetNetworks").GetChildren().Where(c => !string.IsNullOrWhiteSpace(c.Value))
                .ToDictionary(c => c.Key, c => c.Value!, StringComparer.OrdinalIgnoreCase),
            MemoryMegabytes = Math.Clamp(s.GetValue("MemoryMegabytes", d.MemoryMegabytes), 128, 8192), Cpus = Math.Clamp(s.GetValue("Cpus", d.Cpus), 0.25, 16),
            CaBundlePath = s.GetValue<string?>("CaBundlePath"), HttpProxy = s.GetValue<string?>("HttpProxy"), HttpsProxy = s.GetValue<string?>("HttpsProxy"),
            NoProxy = s.GetValue<string?>("NoProxy"), NetworkCheckValidMinutes = Math.Clamp(s.GetValue("NetworkCheckValidMinutes", d.NetworkCheckValidMinutes), 1, 1440),
        };
    }
}

/// <summary>Everything a provider needs for one run. Values are validated; nothing here is a credential.</summary>
public sealed record PerformanceProviderInput(Guid RunId, PerformanceTestDefinition Definition, PerformanceTestDataProfile? TestData, string WorkingDirectory, TimeSpan Timeout);

public sealed record PerformanceProviderResult
{
    public PerformanceRunState State { get; init; }
    public string? Reason { get; init; }
    public PerformanceMetrics? Metrics { get; init; }
    public bool MetricsPartial { get; init; }
    public string MetricsSource { get; init; } = "";
    public string? ProviderVersion { get; init; }
    public string? RuntimeId { get; init; }
    public string? RuntimeVersion { get; init; }
    public string? ContainerImage { get; init; }
    public string? ImageDigest { get; init; }
    public string? Diagnostics { get; init; }
    public List<string> Limitations { get; init; } = [];
}

/// <summary>
/// A load-generation engine. Generic BirkNext concepts (scenario, workload, thresholds) go in; normalized metrics come out. A provider
/// never decides pass/fail — BirkNext evaluates thresholds. A provider that is not installed is a tool limitation, never a performance failure.
/// </summary>
public interface IPerformanceTestProvider
{
    /// <summary>Stable id persisted with definitions and runs (e.g. <c>performance.k6</c>) — never a CLR type name.</summary>
    string ProviderId { get; }
    string DisplayName { get; }
    PerformanceProviderCapabilities Capabilities { get; }
    Task<PerformanceProviderStatus> StatusAsync(CancellationToken ct = default);
    /// <summary>Provider-specific validation of an already safety-checked definition (e.g. unsupported workload mode). Empty = valid.</summary>
    IReadOnlyList<string> Validate(PerformanceTestDefinition definition);
    /// <summary>Runs the workload; honours <paramref name="ct"/> as cancellation (the process is terminated) and the input timeout.</summary>
    Task<PerformanceProviderResult> ExecuteAsync(PerformanceProviderInput input, IProgress<string>? progress, CancellationToken ct);
    /// <summary>One request from the provider's own execution environment (container network, not the BirkNext host) to the target origin.</summary>
    Task<PerformanceReachability> CheckReachabilityAsync(PerformanceTestDefinition definition, CancellationToken ct = default);
    /// <summary>Pulls the provider image when policy allows it (explicit action only). Null error = done.</summary>
    Task<string?> PrepareAsync(CancellationToken ct = default);
    /// <summary>Removes provider-managed execution resources left behind (e.g. containers of runs that are no longer active). Returns what was removed.</summary>
    Task<IReadOnlyList<string>> CleanupOrphansAsync(IReadOnlySet<Guid> activeRunIds, CancellationToken ct = default);
}

// ── Process abstraction (no shell, bounded output, process-tree kill) ─────────────────────────────────────────────────

public sealed record ProcessSpec(string FileName, IReadOnlyList<string> Arguments, string WorkingDirectory, IReadOnlyDictionary<string, string>? Environment = null);

public sealed record ProcessOutcome(int? ExitCode, string StandardOutput, string StandardError, bool TimedOut, bool Cancelled, string? StartError);

public interface IProcessRunner
{
    /// <summary>Runs to completion, timeout (process tree killed → TimedOut) or cancellation (process tree killed → Cancelled).</summary>
    Task<ProcessOutcome> RunAsync(ProcessSpec spec, TimeSpan timeout, int maxOutputBytes, CancellationToken ct);
}

public sealed class SystemProcessRunner : IProcessRunner
{
    public async Task<ProcessOutcome> RunAsync(ProcessSpec spec, TimeSpan timeout, int maxOutputBytes, CancellationToken ct)
    {
        // ArgumentList: every argument is passed verbatim to the executable — no shell, no interpolation, no quoting tricks.
        var info = new ProcessStartInfo { FileName = spec.FileName, WorkingDirectory = spec.WorkingDirectory, UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true, RedirectStandardInput = false, CreateNoWindow = true };
        foreach (var a in spec.Arguments) info.ArgumentList.Add(a);
        if (spec.Environment is not null) foreach (var (k, v) in spec.Environment) info.Environment[k] = v;
        using var process = new Process { StartInfo = info };
        var stdout = new BoundedBuffer(maxOutputBytes);
        var stderr = new BoundedBuffer(maxOutputBytes);
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.Append(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.Append(e.Data); };
        try { if (!process.Start()) return new ProcessOutcome(null, "", "", false, false, "The process could not be started."); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        { return new ProcessOutcome(null, "", "", false, false, $"The executable could not be started ({ex.GetType().Name})."); }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token);
            process.WaitForExit(); // flush redirected streams
            return new ProcessOutcome(process.ExitCode, stdout.ToString(), stderr.ToString(), false, false, null);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            return new ProcessOutcome(null, stdout.ToString(), stderr.ToString(), timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested, ct.IsCancellationRequested, null);
        }
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        try { process.WaitForExit(10_000); } catch (InvalidOperationException) { }
    }

    private sealed class BoundedBuffer(int max)
    {
        private readonly StringBuilder _sb = new();
        private bool _truncated;
        public void Append(string line)
        {
            lock (_sb)
            {
                if (_sb.Length + line.Length + 1 > max) { _truncated = true; return; }
                _sb.AppendLine(line);
            }
        }
        public override string ToString() { lock (_sb) return _truncated ? _sb + "[output truncated]" : _sb.ToString(); }
    }
}
