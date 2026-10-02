using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BirkNext.Api.Services.ContainerRuntime;
using BirkNext.PerformanceTests;
using Path = System.IO.Path;

namespace BirkNext.Api.Services.PerformanceTests;

/// <summary>Where the load generator addresses the target from inside its container. Inside a container "localhost" is the container itself.</summary>
public sealed record ContainerTarget(string ExecutionOrigin, string? Network, bool HostGateway, string Explanation);

/// <summary>
/// The first performance-test provider: k6, executed as an on-demand container through <see cref="IContainerExecutionRuntime"/> (Podman first).
/// k6 is external to BirkNext (pinned image, never baked into BirkNext images or the repository). BirkNext generates the script from a validated
/// definition; the container gets only the run's own directories (script read-only, result writable), an allow-listed environment and a
/// network chosen by trusted configuration. Results come from k6's structured end-of-test summary (handleSummary JSON), never console text.
/// </summary>
public sealed partial class K6PerformanceTestProvider(PerformanceTestOptions options, IContainerExecutionRuntime runtime, ILogger<K6PerformanceTestProvider> logger) : IPerformanceTestProvider
{
    /// <summary>handleSummary, k6/execution and ramping-arrival-rate are all available from this version.</summary>
    public static readonly Version MinimumVersion = new(0, 45, 0);
    public const string Component = "performance-test";
    private const string InDir = "/birknext/in", OutDir = "/birknext/out", CaFile = "/birknext/ca/ca.pem";
    private readonly Dictionary<string, string> _versionByDigest = new(StringComparer.Ordinal);

    public string ProviderId => PerformanceProviderIds.K6;
    public string DisplayName => "k6";

    public PerformanceProviderCapabilities Capabilities { get; } = new()
    {
        Http = true, GraphQl = true, Purposes = [.. Enum.GetValues<WorkloadPurpose>()], Modes = [WorkloadMode.VirtualUsers, WorkloadMode.ArrivalRate],
        Cancellation = true, Metrics = [.. Enum.GetValues<PerformanceMetric>()], RequiresExternalExecutable = false,
    };

    private PerformanceContainerOptions C => options.Container;

    public static string ContainerName(Guid runId, string purpose = "run") => $"birknext-k6-{purpose}-{runId:N}";

    /// <summary>Runtime → image → k6 version, each a separate, truthful state. Nothing here is a performance result.</summary>
    public async Task<PerformanceProviderStatus> StatusAsync(CancellationToken ct = default)
    {
        var status = new PerformanceProviderStatus { ProviderId = ProviderId, DisplayName = DisplayName, Capabilities = Capabilities, Image = C.Image, AllowImagePull = C.AllowImagePull };
        var rt = await runtime.StatusAsync(ct);
        status = status with { Runtime = rt };
        if (rt.Availability != ProviderAvailability.Available)
            return status with { Availability = ProviderAvailability.RuntimeUnavailable, Detail = $"{rt.DisplayName}: {rt.Detail}" };
        if (!ContainerArgumentRules.IsImage(C.Image))
            return status with { Availability = ProviderAvailability.Misconfigured, Detail = "PerformanceTests:Container:Image must be a pinned, fully-qualified image reference (no ':latest')." };
        if (C.CaBundlePath is { } ca && (!ContainerArgumentRules.IsHostPath(ca) || !File.Exists(ca)))
            return status with { Availability = ProviderAvailability.Misconfigured, Detail = "The configured CA bundle (PerformanceTests:Container:CaBundlePath) does not exist." };
        var image = await runtime.ImageAsync(C.Image, ct);
        if (!image.Present)
            return status with { Availability = ProviderAvailability.ImageMissing,
                Detail = $"The k6 image {C.Image} is not available locally. {(C.AllowImagePull ? "Pull it from System Settings → Performance Test Engines." : $"Pull it once: podman pull {C.Image}")}" };
        status = status with { ImagePresent = true, ImageDigest = image.Digest };
        var version = await VersionAsync(image.Digest ?? C.Image, ct);
        if (version is null) return status with { Availability = ProviderAvailability.Misconfigured, Detail = "The k6 container started but did not report a recognisable version." };
        if (version < MinimumVersion)
            return status with { Availability = ProviderAvailability.VersionUnsupported, Version = version.ToString(), Detail = $"k6 {version} is older than the supported minimum {MinimumVersion}." };
        return status with { Availability = ProviderAvailability.Available, Version = version.ToString(), Detail = $"k6 {version} in {C.Image} on {rt.DisplayName} {rt.Version}." };
    }

    private async Task<Version?> VersionAsync(string key, CancellationToken ct)
    {
        lock (_versionByDigest) if (_versionByDigest.TryGetValue(key, out var cached)) return Version.Parse(cached);
        var outcome = await runtime.RunAsync(new ContainerRunSpec
        {
            Name = ContainerName(Guid.NewGuid(), "version"), Image = C.Image, Command = ["version"], Network = "none", Labels = Labels(null), Timeout = TimeSpan.FromSeconds(60),
            MaxOutputBytes = 4096, MemoryMegabytes = 256, Cpus = 0.5,
        }, ct);
        var match = VersionPattern().Match(outcome.StandardOutput + outcome.StandardError);
        if (outcome.ExitCode != 0 || !match.Success || !Version.TryParse(match.Groups[1].Value, out var version)) return null;
        lock (_versionByDigest) _versionByDigest[key] = version.ToString();
        return version;
    }

    public IReadOnlyList<string> Validate(PerformanceTestDefinition definition) =>
        Capabilities.Modes.Contains(definition.Workload.Mode) ? [] : [$"k6 does not support the {definition.Workload.Mode} workload mode."];

    public async Task<string?> PrepareAsync(CancellationToken ct = default)
    {
        if (!C.AllowImagePull) return $"Image pulls are disabled (PerformanceTests:Container:AllowImagePull). Pull it once: podman pull {C.Image}";
        var pulled = await runtime.PullAsync(C.Image, ct);
        return pulled.Present ? null : pulled.Detail;
    }

    /// <summary>
    /// How the container reaches the target: a loopback target (the BirkNext host) via the runtime's host-gateway alias; a host mapped in
    /// <c>Container:TargetNetworks</c> by its container DNS name on that network; anything else (DEV/QA) via ordinary outbound networking.
    /// </summary>
    public ContainerTarget ResolveTarget(string targetOrigin)
    {
        var uri = new Uri(targetOrigin);
        if (uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            var rewritten = new UriBuilder(uri) { Host = runtime.HostGatewayAlias }.Uri.GetLeftPart(UriPartial.Authority);
            return new ContainerTarget(rewritten, Valid(C.Network), true,
                $"The target runs on the BirkNext host; the container reaches it as {runtime.HostGatewayAlias} (inside a container, localhost is the container itself).");
        }
        if (C.TargetNetworks.TryGetValue(uri.Host, out var network) && ContainerArgumentRules.IsNetwork(network))
            return new ContainerTarget(uri.GetLeftPart(UriPartial.Authority), network, false, $"The target is a container on network {network}; reached by its DNS name {uri.Host}.");
        return new ContainerTarget(uri.GetLeftPart(UriPartial.Authority), Valid(C.Network), false,
            C.Network is null ? "External target, reached through the runtime's default outbound network." : $"External target, reached through network {C.Network}.");
    }

    private static string? Valid(string? network) => network is { } n && ContainerArgumentRules.IsNetwork(n) ? n : null;

    private static Dictionary<string, string> Labels(Guid? runId)
    {
        var labels = new Dictionary<string, string> { ["birknext.managed"] = "true", ["birknext.component"] = Component, ["birknext.provider"] = "performance.k6" };
        if (runId is { } id) labels["birknext.run-id"] = id.ToString("N");
        return labels;
    }

    /// <summary>Allow-listed container environment only: never the BirkNext host/API environment.</summary>
    internal Dictionary<string, string> Environment()
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal) { ["K6_NO_USAGE_REPORT"] = "true" };
        if (!string.IsNullOrWhiteSpace(C.HttpProxy)) env["HTTP_PROXY"] = C.HttpProxy!;
        if (!string.IsNullOrWhiteSpace(C.HttpsProxy)) env["HTTPS_PROXY"] = C.HttpsProxy!;
        if (!string.IsNullOrWhiteSpace(C.NoProxy)) env["NO_PROXY"] = C.NoProxy!;
        if (C.CaBundlePath is not null) env["SSL_CERT_FILE"] = CaFile;
        return env;
    }

    private List<ContainerMount> Mounts(string inDir, string outDir)
    {
        var mounts = new List<ContainerMount> { new(inDir, InDir, true), new(outDir, OutDir, false) };
        if (C.CaBundlePath is { } ca) mounts.Add(new ContainerMount(Path.GetDirectoryName(ca)!, "/birknext/ca", true));
        return mounts;
    }

    public async Task<PerformanceProviderResult> ExecuteAsync(PerformanceProviderInput input, IProgress<string>? progress, CancellationToken ct)
    {
        var status = await StatusAsync(ct);
        var provenance = new PerformanceProviderResult { RuntimeId = runtime.RuntimeId, RuntimeVersion = status.Runtime?.Version, ContainerImage = C.Image, ImageDigest = status.ImageDigest,
            ProviderVersion = status.Version, MetricsSource = $"k6 {status.Version} end-of-test summary (handleSummary JSON) in {C.Image}" };
        if (status.Availability != ProviderAvailability.Available)
            return provenance with { State = PerformanceRunState.ExecutionFailed, Reason = status.Detail, MetricsSource = "k6 (not run)" };
        var inDir = Path.Combine(input.WorkingDirectory, "in");
        var outDir = Path.Combine(input.WorkingDirectory, "out");
        var name = ContainerName(input.RunId);
        try
        {
            Directory.CreateDirectory(inDir);
            Directory.CreateDirectory(outDir);
            var target = ResolveTarget(input.Definition.TargetOrigin);
            var executable = input.Definition with { TargetOrigin = target.ExecutionOrigin };
            await File.WriteAllTextAsync(Path.Combine(inDir, "test.js"), K6ScriptGenerator.Generate(executable, input.TestData, $"{OutDir}/summary.json"), new UTF8Encoding(false), ct);
            progress?.Report("Running");
            var outcome = await runtime.RunAsync(new ContainerRunSpec
            {
                Name = name, Image = C.Image, Command = ["run", "--no-color", "--quiet", $"{InDir}/test.js"], Mounts = Mounts(inDir, outDir), Environment = Environment(),
                Labels = Labels(input.RunId), Network = target.Network, HostGateway = target.HostGateway, Timeout = input.Timeout, MaxOutputBytes = options.MaxProviderOutputBytes,
                MemoryMegabytes = C.MemoryMegabytes, Cpus = C.Cpus,
            }, ct);
            var diagnostics = Redact(outcome.StandardError.Length > 0 ? outcome.StandardError : outcome.StandardOutput, options.MaxProviderOutputBytes);
            var notes = new List<string>();
            if (!outcome.Removed) notes.Add($"The container {name} could not be confirmed removed; cleanup runs again on the next start.");
            if (outcome.UnappliedLimits.Count > 0)
                notes.Add($"The container ran without {string.Join("/", outcome.UnappliedLimits)} limits: the {runtime.DisplayName} host delegates no such cgroup controller (common for rootless machines). BirkNext's load limits still applied.");
            var result = provenance with { Diagnostics = diagnostics, Limitations = notes };
            if (outcome.Cancelled)
                return result with { State = PerformanceRunState.Cancelled, Reason = "Cancelled; the k6 container was stopped and removed.",
                    Limitations = [.. result.Limitations, "A cancelled run has no end-of-test summary: no metrics are reported and nothing is assessed."] };
            if (outcome.TimedOut)
                return result with { State = PerformanceRunState.TimedOut, Reason = $"The provider exceeded its {input.Timeout.TotalSeconds:0} s timeout; the k6 container was stopped and removed." };
            if (outcome.StartError is not null) return result with { State = PerformanceRunState.ExecutionFailed, Reason = outcome.StartError };
            // 0 = finished; 99 = a k6 threshold crossed (BirkNext declares only always-true thresholds, to expose per-step metrics). Others = errors.
            if (outcome.ExitCode is not (0 or 99))
                return result with { State = PerformanceRunState.ExecutionFailed, Reason = $"The k6 container exited with code {outcome.ExitCode}." };
            var summaryPath = Path.Combine(outDir, "summary.json");
            if (!File.Exists(summaryPath)) return result with { State = PerformanceRunState.ExecutionFailed, Reason = "k6 finished without writing its structured summary." };
            var (metrics, error) = K6SummaryParser.Parse(await File.ReadAllTextAsync(summaryPath, ct), input.Definition.Scenario.Steps.Select(s => s.Name).ToList());
            if (metrics is null) return result with { State = PerformanceRunState.ExecutionFailed, Reason = error };
            var limitations = new List<string>(result.Limitations);
            if (metrics.DroppedIterations is > 0)
                limitations.Add($"The load generator dropped {metrics.DroppedIterations} iteration(s): it lacked capacity to sustain the configured rate. Measured throughput may reflect the generator, not the target.");
            if (target.HostGateway) limitations.Add(target.Explanation);
            return result with { State = PerformanceRunState.Completed, Metrics = metrics, Limitations = limitations };
        }
        finally
        {
            Cleanup(input.WorkingDirectory);
        }
    }

    /// <summary>One GET of the origin from a k6 container on the same network/proxy/CA as a real run. Any HTTP status = reachable.</summary>
    public async Task<PerformanceReachability> CheckReachabilityAsync(PerformanceTestDefinition definition, CancellationToken ct = default)
    {
        var target = ResolveTarget(definition.TargetOrigin);
        var envelope = new PerformanceReachability { TargetOrigin = definition.TargetOrigin, ExecutionOrigin = target.ExecutionOrigin, Network = target.Network, CheckedAt = DateTimeOffset.UtcNow };
        var status = await StatusAsync(ct);
        if (status.Availability is ProviderAvailability.RuntimeUnavailable) return envelope with { State = "RuntimeUnavailable", Detail = status.Detail };
        if (status.Availability is ProviderAvailability.ImageMissing) return envelope with { State = "ImageMissing", Detail = status.Detail };
        if (status.Availability != ProviderAvailability.Available) return envelope with { State = "Unknown", Detail = status.Detail };
        var id = Guid.NewGuid();
        var work = Path.Combine(Path.GetTempPath(), "birknext-performance", "probe-" + id.ToString("N"));
        var inDir = Path.Combine(work, "in"); var outDir = Path.Combine(work, "out");
        try
        {
            Directory.CreateDirectory(inDir); Directory.CreateDirectory(outDir);
            await File.WriteAllTextAsync(Path.Combine(inDir, "probe.js"), K6ScriptGenerator.Probe(target.ExecutionOrigin, $"{OutDir}/probe.json"), new UTF8Encoding(false), ct);
            var outcome = await runtime.RunAsync(new ContainerRunSpec
            {
                Name = ContainerName(id, "probe"), Image = C.Image, Command = ["run", "--no-color", "--quiet", $"{InDir}/probe.js"], Mounts = Mounts(inDir, outDir), Environment = Environment(),
                Labels = Labels(id), Network = target.Network, HostGateway = target.HostGateway, Timeout = TimeSpan.FromSeconds(90), MaxOutputBytes = 16 * 1024, MemoryMegabytes = 256, Cpus = 0.5,
            }, ct);
            var probe = Path.Combine(outDir, "probe.json");
            if (outcome.TimedOut) return envelope with { State = "Timeout", Detail = "The network check did not finish in time." };
            if (!File.Exists(probe)) return envelope with { State = "Unknown", Detail = "The network check container produced no result." };
            return Interpret(envelope, await File.ReadAllTextAsync(probe, ct));
        }
        finally { Cleanup(work); }
    }

    /// <summary>Maps the probe's HTTP status / k6 error code: 11xx DNS, 12xx TCP, 13xx TLS, 1050/1211 timeout.</summary>
    internal static PerformanceReachability Interpret(PerformanceReachability envelope, string json)
    {
        var (status, code) = K6SummaryParser.Probe(json);
        if (status is > 0) return envelope with { State = "Reachable", Reachable = true, HttpStatus = status, Detail = $"The k6 container reached {envelope.ExecutionOrigin} (HTTP {status})." };
        return code switch
        {
            1050 or 1211 => envelope with { State = "Timeout", Detail = $"The request from the k6 container to {envelope.ExecutionOrigin} timed out (VPN/private route not available to containers?)." },
            >= 1100 and < 1200 => envelope with { State = "DnsFailure", Detail = $"The k6 container could not resolve {new Uri(envelope.ExecutionOrigin).Host} (corporate DNS/VPN may only be available on the host)." },
            >= 1200 and < 1300 => envelope with { State = "ConnectionFailed", Detail = $"The k6 container could not connect to {envelope.ExecutionOrigin} (code {code})." },
            >= 1300 and < 1400 => envelope with { State = "TlsFailure", Detail = $"TLS to {envelope.ExecutionOrigin} failed from the k6 container (code {code}); configure PerformanceTests:Container:CaBundlePath for a corporate CA. Verification is never disabled." },
            _ => envelope with { State = "Unknown", Detail = $"The k6 container got no HTTP response from {envelope.ExecutionOrigin} (code {code?.ToString() ?? "none"})." },
        };
    }

    /// <summary>Removes only containers labelled birknext.managed=true + birknext.component=performance-test whose run is not active.</summary>
    public async Task<IReadOnlyList<string>> CleanupOrphansAsync(IReadOnlySet<Guid> activeRunIds, CancellationToken ct = default)
    {
        if ((await runtime.StatusAsync(ct)).Availability != ProviderAvailability.Available) return [];
        var active = activeRunIds.Select(id => ContainerName(id)).ToHashSet(StringComparer.Ordinal);
        var removed = new List<string>();
        // Another BirkNext instance may share this Podman: a k6 container younger than the longest possible run can be its live run, so only
        // containers older than that (or no longer inspectable) are stale. k6 containers end with their workload and use --rm, so real orphans are rare.
        var maxAge = TimeSpan.FromSeconds(Math.Max(options.MaxDurationSeconds, options.MaxSoakDurationSeconds) + options.ProviderTimeoutGraceSeconds + 60);
        foreach (var name in await runtime.ListManagedAsync(Component, ct))
        {
            if (!name.StartsWith("birknext-k6-", StringComparison.Ordinal) || active.Contains(name)) continue;
            var info = await runtime.InspectAsync(name, ct);
            if (info is not null && ParseStartedAt(info.StartedAt) is { } started && DateTimeOffset.UtcNow - started < maxAge)
            {
                logger.LogInformation("Kept BirkNext k6 container {Name}: started {Age} ago, which can still be a live run (possibly of another BirkNext instance).", name, DateTimeOffset.UtcNow - started);
                continue;
            }
            if (await runtime.RemoveAsync(name, ct)) removed.Add(name);
        }
        if (removed.Count > 0) logger.LogInformation("Removed {Count} stale BirkNext performance-test container(s).", removed.Count);
        return removed;
    }

    /// <summary>Podman's StartedAt ("2026-10-02 10:00:00.123456789 +0000 UTC" or RFC 3339). Null when unparseable (treated as stale).</summary>
    public static DateTimeOffset? ParseStartedAt(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        // Podman prints local time with a zone abbreviation ("+0200 CEST"); the abbreviation is redundant with the numeric offset and not parseable.
        var text = System.Text.RegularExpressions.Regex.Replace(System.Text.RegularExpressions.Regex.Replace(value.Trim(), @"\s+[A-Z]{2,5}$", ""), @"(\.\d{7})\d+", "$1");
        return DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var t) && t.Year > 2000 ? t : null;
    }

    private void Cleanup(string directory)
    {
        try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { logger.LogWarning("Performance test temp directory could not be removed: {Type}", ex.GetType().Name); }
    }

    internal static string Redact(string text, int max)
    {
        var safe = LocalHttpsProxy.SensitiveDataRedactor.RedactText(text);
        safe = BearerPattern().Replace(safe, "Bearer [redacted]");
        return safe.Length > max ? safe[..max] + "[truncated]" : safe;
    }

    [GeneratedRegex(@"v?(\d+\.\d+\.\d+)")] private static partial Regex VersionPattern();
    [GeneratedRegex(@"(?i)bearer\s+[A-Za-z0-9\-_.~+/]+=*")] private static partial Regex BearerPattern();
}
