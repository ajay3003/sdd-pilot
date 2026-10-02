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
    /// <summary>Path to the k6 executable; null = look up "k6" on PATH.</summary>
    public string? K6ExecutablePath { get; init; }
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
            K6ExecutablePath = s.GetValue<string?>("K6ExecutablePath"),
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
