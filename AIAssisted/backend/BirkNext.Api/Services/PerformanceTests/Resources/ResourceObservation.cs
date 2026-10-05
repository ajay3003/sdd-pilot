using System.Diagnostics;
using BirkNext.Api.Services.ContainerRuntime;
using BirkNext.PerformanceTests;

namespace BirkNext.Api.Services.PerformanceTests.Resources;

/// <summary>An approved observation target as the backend knows it: the public descriptor plus the internal container name (never from a user).</summary>
public sealed record ResourceTargetSpec(ResourceObservationTarget Target, string? ContainerName, ResourceComponentRole Role = ResourceComponentRole.Target);

/// <summary>Resource Stability configuration (<c>PerformanceTests:Resources</c>).</summary>
public sealed record PerformanceResourceOptions
{
    /// <summary>The built-in target observing BirkNext's own API process through in-process .NET runtime counters (no attach).</summary>
    public const string SelfTargetId = "birknext-api";

    public List<ResourceTargetSpec> Targets { get; init; } = [];
    public bool EnableSelfObservation { get; init; } = true;
    public int DefaultSampleIntervalSeconds { get; init; } = 10;
    public int MinSampleIntervalSeconds { get; init; } = 5;
    public int MaxSampleIntervalSeconds { get; init; } = 60;
    public int MaxCooldownSeconds { get; init; } = 600;
    /// <summary>Per component; beyond it the series is downsampled (bucket means), so a multi-hour soak cannot grow storage without bound.</summary>
    public int MaxSamplesPerComponent { get; init; } = 720;

    public static PerformanceResourceOptions From(IConfiguration s)
    {
        var d = new PerformanceResourceOptions();
        var targets = s.GetSection("Targets").GetChildren().Select(c =>
        {
            var id = c["Id"] ?? ""; var container = c["Container"];
            if (!ContainerArgumentRules.IsName(id) || id == SelfTargetId || (container is not null && !ContainerArgumentRules.IsName(container))) return null;
            return new ResourceTargetSpec(new ResourceObservationTarget
            {
                Id = id, DisplayName = string.IsNullOrWhiteSpace(c["DisplayName"]) ? id : c["DisplayName"]!.Trim()[..Math.Min(80, c["DisplayName"]!.Trim().Length)],
                ProviderId = c["Provider"] ?? ResourceProviderIds.Podman, Description = c["Description"],
                Environments = c.GetSection("Environments").GetChildren().Select(e => e.Value ?? "").Where(v => v.Length > 0).ToList(),
            }, container ?? id);
        }).OfType<ResourceTargetSpec>().GroupBy(t => t.Target.Id).Select(g => g.First()).ToList();
        var min = Math.Clamp(s.GetValue("MinSampleIntervalSeconds", d.MinSampleIntervalSeconds), 1, 60);
        var max = Math.Clamp(s.GetValue("MaxSampleIntervalSeconds", d.MaxSampleIntervalSeconds), min, 600);
        return new PerformanceResourceOptions
        {
            Targets = targets, EnableSelfObservation = s.GetValue("EnableSelfObservation", d.EnableSelfObservation),
            MinSampleIntervalSeconds = min, MaxSampleIntervalSeconds = max,
            DefaultSampleIntervalSeconds = Math.Clamp(s.GetValue("DefaultSampleIntervalSeconds", d.DefaultSampleIntervalSeconds), min, max),
            MaxCooldownSeconds = Math.Clamp(s.GetValue("MaxCooldownSeconds", d.MaxCooldownSeconds), 0, 3600),
            MaxSamplesPerComponent = Math.Clamp(s.GetValue("MaxSamplesPerComponent", d.MaxSamplesPerComponent), 60, 5000),
        };
    }
}

/// <summary>One reading (or why there is none). Collection problems are telemetry states, never resource results.</summary>
public sealed record ResourceSampleResult(ResourceSample? Sample, ResourceCollectionState State, string? Detail, string? Image = null, double? MemoryLimitBytes = null, double? CpuLimit = null);

/// <summary>A resource observation provider (Podman container stats, .NET runtime counters, …). Read-only; observes approved targets only.</summary>
public interface IResourceObservationProvider
{
    string ProviderId { get; }
    string DisplayName { get; }
    Task<ResourceProviderCapability> StatusAsync(CancellationToken ct = default);
    /// <summary>Whether this provider can observe the target at all, and the target's current instance (no sampling).</summary>
    Task<ResourceSampleResult> ProbeAsync(ResourceTargetSpec target, CancellationToken ct = default);
    Task<ResourceSampleResult> SampleAsync(ResourceTargetSpec target, CancellationToken ct = default);
}

/// <summary>
/// <c>resource.podman</c>: container memory, memory limit and CPU of approved containers (and the run's own k6 container) through the container
/// runtime. Never lists or touches other containers, never reads a container's environment. Where the runtime cannot account memory (rootless Podman
/// without a delegated memory controller) memory is Unavailable — not 0.
/// </summary>
public sealed class PodmanResourceObservationProvider(IContainerExecutionRuntime runtime) : IResourceObservationProvider
{
    public string ProviderId => ResourceProviderIds.Podman;
    public string DisplayName => "Podman container resources";
    private const string Scope = "Approved Podman containers";
    private static readonly List<ResourceMetric> All = [ResourceMetric.ContainerMemoryBytes, ResourceMetric.CpuPercent];

    public async Task<ResourceProviderCapability> StatusAsync(CancellationToken ct = default)
    {
        var status = await runtime.StatusAsync(ct);
        if (status.Availability != ProviderAvailability.Available)
            return new(ProviderId, DisplayName, "Unavailable", $"{runtime.DisplayName}: {status.Detail}", [])
                { Scope = Scope, Summary = $"{runtime.DisplayName} is not available.", UnavailableMetrics = All };
        var controllers = await runtime.ControllersAsync(ct);
        return controllers.Contains("memory")
            ? new(ProviderId, DisplayName, "Available", $"{runtime.DisplayName} {status.Version}: container memory and CPU.", All)
                { Scope = Scope, Summary = "Container memory and CPU." }
            : new(ProviderId, DisplayName, "Partial", $"{runtime.DisplayName} {status.Version}: CPU only — container memory accounting is not available (no memory cgroup controller delegated, typical for rootless Podman). Memory is reported as unavailable, never 0.",
                [ResourceMetric.CpuPercent])
                { Scope = Scope, Summary = "CPU only; container memory accounting is unavailable on this host.", UnavailableMetrics = [ResourceMetric.ContainerMemoryBytes] };
    }

    public async Task<ResourceSampleResult> ProbeAsync(ResourceTargetSpec target, CancellationToken ct = default)
    {
        if (target.ContainerName is not { } name || !ContainerArgumentRules.IsName(name)) return new(null, ResourceCollectionState.TargetNotFound, "No container is configured for this target.");
        var info = await runtime.InspectAsync(name, ct);
        if (info is null) return new(null, ResourceCollectionState.TargetNotFound, $"The approved container for {target.Target.DisplayName} was not found.");
        if (!info.Running) return new(null, ResourceCollectionState.TargetNotFound, $"The approved container for {target.Target.DisplayName} is not running.");
        return new(null, ResourceCollectionState.Collected, "Container found.", info.Image, info.MemoryLimitBytes, info.NanoCpus is { } n ? n / 1e9 : null);
    }

    public async Task<ResourceSampleResult> SampleAsync(ResourceTargetSpec target, CancellationToken ct = default)
    {
        if (target.ContainerName is not { } name || !ContainerArgumentRules.IsName(name)) return new(null, ResourceCollectionState.TargetNotFound, "No container is configured for this target.");
        var info = await runtime.InspectAsync(name, ct);
        if (info is null || !info.Running) return new(null, ResourceCollectionState.TargetNotFound, "The container is not running.");
        var stats = await runtime.StatsAsync(name, ct);
        if (stats.CpuPercent is null && stats.MemoryBytes is null)
            return new(null, ResourceCollectionState.CollectionFailed, stats.Error ?? "No statistics were returned.", info.Image, info.MemoryLimitBytes);
        var sample = new ResourceSample
        {
            At = DateTimeOffset.UtcNow, ProviderId = ProviderId, TargetId = target.Target.Id, InstanceId = info.InstanceKey,
            Memory = new MemoryResourceSample { ContainerMemoryBytes = stats.MemoryBytes, ContainerMemoryLimitBytes = stats.MemoryLimitBytes },
            Cpu = new CpuResourceSample { CpuPercent = stats.CpuPercent },
        };
        return new(sample, stats.MemoryBytes is null ? ResourceCollectionState.PartialEvidence : ResourceCollectionState.Collected, stats.Error,
            info.Image, stats.MemoryLimitBytes ?? info.MemoryLimitBytes, info.NanoCpus is { } n ? n / 1e9 : null);
    }
}

/// <summary>
/// <c>resource.dotnet.runtime</c> for BirkNext's own API process only: in-process runtime counters (GC heap, post-GC floor, LOH, allocation rate,
/// collections, pause time, working set, private bytes, CPU, threads, handles). No process attachment and no elevated access exist, so other .NET
/// targets are Unsupported here — they need exported runtime telemetry (OpenTelemetry/App Insights), which is not implemented.
/// </summary>
public sealed class DotNetRuntimeSelfObservationProvider : IResourceObservationProvider
{
    public string ProviderId => ResourceProviderIds.DotNetRuntime;
    public string DisplayName => "BirkNext API runtime";
    private readonly object _gate = new();
    private (DateTimeOffset At, TimeSpan Cpu, long Allocated, int GcIndexGen)? _previous;
    private long _lastGcIndex = -1;
    private static readonly string Instance = $"pid{Environment.ProcessId}@{Process.GetCurrentProcess().StartTime.ToUniversalTime():O}";

    public static readonly List<ResourceMetric> Supported =
    [
        ResourceMetric.WorkingSetBytes, ResourceMetric.PrivateBytes, ResourceMetric.ManagedHeapBytes, ResourceMetric.GcHeapAfterGcBytes, ResourceMetric.LohBytes,
        ResourceMetric.AllocationRateBytesPerSecond, ResourceMetric.Gen2CollectionsPerMinute, ResourceMetric.GcPauseMsPerMinute, ResourceMetric.CpuPercent,
        ResourceMetric.ThreadCount, ResourceMetric.HandleCount,
    ];

    public Task<ResourceProviderCapability> StatusAsync(CancellationToken ct = default) => Task.FromResult(new ResourceProviderCapability(ProviderId, DisplayName, "Available",
        "In-process runtime counters of the BirkNext API itself. Other .NET applications are Unsupported (no attach; exported runtime telemetry is not implemented).", Supported)
    {
        Scope = "BirkNext API process only", Summary = "Runtime counters from the BirkNext API process.",
        Limits = [new("External .NET targets", "Unsupported", "No process attachment or exported runtime telemetry provider is configured.")],
    });

    public Task<ResourceSampleResult> ProbeAsync(ResourceTargetSpec target, CancellationToken ct = default) => Task.FromResult(target.Target.Id == PerformanceResourceOptions.SelfTargetId
        ? new ResourceSampleResult(null, ResourceCollectionState.Collected, "BirkNext API process (this process).")
        : new ResourceSampleResult(null, ResourceCollectionState.ProviderUnavailable, ".NET runtime metrics are only available for the BirkNext process itself; this target needs exported runtime telemetry, which is not implemented."));

    public Task<ResourceSampleResult> SampleAsync(ResourceTargetSpec target, CancellationToken ct = default)
    {
        if (target.Target.Id != PerformanceResourceOptions.SelfTargetId)
            return Task.FromResult(new ResourceSampleResult(null, ResourceCollectionState.ProviderUnavailable, "Unsupported target for in-process runtime counters."));
        lock (_gate)
        {
            using var process = Process.GetCurrentProcess();
            var now = DateTimeOffset.UtcNow;
            var cpu = process.TotalProcessorTime;
            var allocated = GC.GetTotalAllocatedBytes(false);
            var info = GC.GetGCMemoryInfo(GCKind.Any);
            double? cpuPercent = null, allocationRate = null;
            if (_previous is { } p && (now - p.At).TotalSeconds > 0)
            {
                var seconds = (now - p.At).TotalSeconds;
                cpuPercent = Math.Round((cpu - p.Cpu).TotalSeconds / (seconds * Environment.ProcessorCount) * 100, 2);
                allocationRate = Math.Round((allocated - p.Allocated) / seconds, 0);
            }
            // The post-GC floor: the heap size recorded at the end of a GC, taken only once per new GC (index advanced).
            double? floor = null;
            if (info.Index > 0 && info.Index != _lastGcIndex) { floor = info.HeapSizeBytes; _lastGcIndex = info.Index; }
            _previous = (now, cpu, allocated, 0);
            var generations = info.GenerationInfo;
            var sample = new ResourceSample
            {
                At = now, ProviderId = ProviderId, TargetId = target.Target.Id, InstanceId = Instance,
                Memory = new MemoryResourceSample
                {
                    WorkingSetBytes = process.WorkingSet64, PrivateBytes = process.PrivateMemorySize64 > 0 ? process.PrivateMemorySize64 : null,
                    ManagedHeapBytes = GC.GetTotalMemory(false), GcHeapAfterGcBytes = floor, LohBytes = generations.Length > 3 ? generations[3].SizeAfterBytes : null,
                    AllocationRateBytesPerSecond = allocationRate, Gen0Collections = GC.CollectionCount(0), Gen1Collections = GC.CollectionCount(1), Gen2Collections = GC.CollectionCount(2),
                    GcPauseMs = GC.GetTotalPauseDuration().TotalMilliseconds,
                },
                Cpu = new CpuResourceSample { CpuPercent = cpuPercent, ThreadCount = process.Threads.Count, HandleCount = process.HandleCount > 0 ? process.HandleCount : null },
            };
            return Task.FromResult(new ResourceSampleResult(sample, ResourceCollectionState.Collected, null));
        }
    }
}

/// <summary>Approved targets per environment and the providers that observe them. The API exposes only these — no container enumeration.</summary>
public sealed class ResourceObservationRegistry(PerformanceTestOptions options, IEnumerable<IResourceObservationProvider> providers)
{
    private readonly Dictionary<string, IResourceObservationProvider> _providers = providers.ToDictionary(p => p.ProviderId, StringComparer.Ordinal);
    public PerformanceResourceOptions Options => options.Resources;
    public IReadOnlyCollection<IResourceObservationProvider> Providers => _providers.Values;
    public IResourceObservationProvider? Provider(string id) => _providers.GetValueOrDefault(id);

    public static readonly ResourceObservationTarget Self = new()
    {
        Id = PerformanceResourceOptions.SelfTargetId, DisplayName = "BirkNext API (this process)", ProviderId = ResourceProviderIds.DotNetRuntime,
        Description = "BirkNext's own API process — for BirkNext self-regression soak runs (e.g. load against BirkNext endpoints).",
    };

    public IReadOnlyList<ResourceTargetSpec> Approved(string environmentId, string environmentType) => ApprovedTargets(options.Resources, environmentId, environmentType);

    public static IReadOnlyList<ResourceTargetSpec> ApprovedTargets(PerformanceResourceOptions o, string environmentId, string environmentType) =>
        (o.EnableSelfObservation ? [new ResourceTargetSpec(Self, null)] : Array.Empty<ResourceTargetSpec>())
        .Concat(o.Targets.Where(t => t.Target.Environments.Count == 0 || t.Target.Environments.Any(e =>
            e.Equals(environmentId, StringComparison.OrdinalIgnoreCase) || e.Equals(environmentType, StringComparison.OrdinalIgnoreCase)))).ToList();

    /// <summary>Approved targets with their provider availability and whether they are currently found (probe only, no sampling).</summary>
    public async Task<IReadOnlyList<ResourceTargetStatus>> TargetStatusAsync(string environmentId, string environmentType, CancellationToken ct = default)
    {
        var list = new List<ResourceTargetStatus>();
        foreach (var spec in Approved(environmentId, environmentType))
        {
            if (Provider(spec.Target.ProviderId) is not { } provider) { list.Add(new(spec.Target, true, "Unavailable", $"No provider '{spec.Target.ProviderId}' is registered.", null)); continue; }
            try
            {
                var capability = await provider.StatusAsync(ct);
                if (capability.Availability == "Unavailable") { list.Add(new(spec.Target, true, "Unavailable", capability.Detail, null)); continue; }
                var probe = await provider.ProbeAsync(spec, ct);
                list.Add(new(spec.Target, true, probe.State == ResourceCollectionState.Collected ? capability.Availability : probe.State.ToString(),
                    probe.State == ResourceCollectionState.Collected ? $"{probe.Detail} {capability.Detail}" : probe.Detail ?? "", probe.Image));
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { list.Add(new(spec.Target, true, "Unavailable", $"Status could not be determined ({ex.GetType().Name}).", null)); }
        }
        return list;
    }

    public ResourceTargetSpec? Resolve(string targetId, string environmentId, string environmentType) => Approved(environmentId, environmentType).FirstOrDefault(t => t.Target.Id == targetId);

    /// <summary>The run's own k6 container as load-generator health (a BirkNext-managed container, never an arbitrary one).</summary>
    public static ResourceTargetSpec LoadGenerator(Guid runId) => new(new ResourceObservationTarget
    {
        Id = "load-generator", DisplayName = "k6 load generator", ProviderId = ResourceProviderIds.Podman, Description = "The run's own k6 container (generator health).",
    }, K6PerformanceTestProvider.ContainerName(runId), ResourceComponentRole.LoadGenerator);

    public async Task<IReadOnlyList<ResourceProviderCapability>> CapabilitiesAsync(CancellationToken ct = default)
    {
        var list = new List<ResourceProviderCapability>();
        foreach (var p in _providers.Values.OrderBy(p => p.ProviderId, StringComparer.Ordinal))
        {
            try { list.Add(await p.StatusAsync(ct)); }
            catch (Exception ex) when (ex is not OperationCanceledException) { list.Add(new(p.ProviderId, p.DisplayName, "Unavailable", $"Status could not be determined ({ex.GetType().Name}).", [])); }
        }
        list.Add(new(ResourceProviderIds.Browser, "Browser memory", "Unsupported",
            "Not a resource provider: the JavaScript heap does not represent Blazor/.NET WASM managed memory, and GC timing makes browser leak verdicts untrustworthy.", [])
            { Scope = "Browser", Summary = "Browser JavaScript heap is not a reliable measure of Blazor/.NET WASM managed memory." });
        list.Add(new(ResourceProviderIds.OpenTelemetry, "OpenTelemetry runtime metrics", "Not implemented", "Future provider for exported runtime metrics of other services.", [])
            { Scope = "External services", Summary = "Future provider for exported runtime metrics from target services." });
        list.Add(new(ResourceProviderIds.AppInsights, "Application Insights metrics", "Not implemented", "Future observability adapter; Resource Stability does not depend on Azure.", [])
            { Scope = "External services", Summary = "Future observability adapter." });
        return list;
    }
}

/// <summary>
/// Collects samples for one run on a low-frequency timer (default 10 s) until stopped. Bounded: past the per-component cap the series is halved by
/// averaging neighbouring samples of the same instance (deterministic), and the raw peak per metric is kept separately. Never throws into the run.
/// </summary>
public sealed class ResourceObservationSession : IAsyncDisposable
{
    private readonly IReadOnlyList<(ResourceTargetSpec Spec, IResourceObservationProvider? Provider)> _components;
    private readonly TimeSpan _interval;
    private readonly int _cap;
    private readonly ILogger _logger;
    private readonly Guid _runId;
    private readonly object _gate = new();
    private readonly Dictionary<string, State> _state = new(StringComparer.Ordinal);
    private CancellationTokenSource? _cts;
    private Task? _loop;
    public DateTimeOffset StartedAt { get; private set; }
    public bool Running => _loop is { IsCompleted: false };
    /// <summary>Raised after each sampling round (for periodic persistence of partial evidence).</summary>
    public Func<ResourceStabilityAssessment, Task>? OnRound { get; init; }

    private sealed class State
    {
        public required ResourceTargetSpec Spec;
        public required string ProviderId;
        public List<ResourceSample> Samples = [];
        public int Raw, Failed;
        public bool Downsampled;
        public string? LastInstance, Image, Detail;
        public double? MemoryLimit, CpuLimit;
        public ResourceCollectionState LastState = ResourceCollectionState.TargetNotFound;
        public bool EverCollected, EverPartial;
        public List<ResourceDiscontinuity> Discontinuities = [];
    }

    public ResourceObservationSession(Guid runId, IReadOnlyList<(ResourceTargetSpec Spec, IResourceObservationProvider? Provider)> components, TimeSpan interval, int cap, ILogger logger)
    {
        _runId = runId; _components = components; _interval = interval; _cap = cap; _logger = logger;
        foreach (var (spec, provider) in components)
            _state[spec.Target.Id] = new State { Spec = spec, ProviderId = provider?.ProviderId ?? spec.Target.ProviderId,
                LastState = provider is null ? ResourceCollectionState.ProviderUnavailable : ResourceCollectionState.TargetNotFound,
                Detail = provider is null ? $"No provider '{spec.Target.ProviderId}' is registered." : null };
    }

    public void Start()
    {
        StartedAt = DateTimeOffset.UtcNow;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => LoopAsync(_cts.Token));
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_interval);
        try
        {
            do
            {
                await SampleRoundAsync(ct);
                if (OnRound is { } round) { try { await round(Snapshot(inProgress: true)); } catch (Exception ex) when (ex is not OperationCanceledException) { _logger.LogWarning("Resource evidence for run {RunId} could not be persisted: {Type}", _runId, ex.GetType().Name); } }
            }
            while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    internal async Task SampleRoundAsync(CancellationToken ct)
    {
        foreach (var (spec, provider) in _components)
        {
            if (provider is null) continue;
            var state = _state[spec.Target.Id];
            ResourceSampleResult result;
            try
            {
                using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
                bounded.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, _interval.TotalSeconds)));
                result = await provider.SampleAsync(spec, bounded.Token);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is not OutOfMemoryException) { result = new(null, ResourceCollectionState.CollectionFailed, $"Sampling failed ({ex.GetType().Name})."); }
            lock (_gate) Record(state, result);
        }
    }

    private void Record(State s, ResourceSampleResult r)
    {
        s.Image = r.Image ?? s.Image; s.MemoryLimit = r.MemoryLimitBytes ?? s.MemoryLimit; s.CpuLimit = r.CpuLimit ?? s.CpuLimit;
        if (r.Sample is not { } sample)
        {
            // The load generator does not exist before k6 starts or after it is removed: absence there is expected, not a failure.
            if (!(s.Spec.Role == ResourceComponentRole.LoadGenerator && r.State == ResourceCollectionState.TargetNotFound)) s.Failed++;
            s.LastState = s.EverCollected ? s.LastState : r.State;
            s.Detail = r.Detail ?? s.Detail;
            return;
        }
        if (s.LastInstance is { } last && sample.InstanceId != last)
            s.Discontinuities.Add(new ResourceDiscontinuity(sample.At, "instance changed (restart or replacement)", last, sample.InstanceId));
        s.LastInstance = sample.InstanceId;
        s.EverCollected = true;
        if (r.State == ResourceCollectionState.PartialEvidence) { s.EverPartial = true; s.Detail = r.Detail ?? s.Detail; }
        s.LastState = s.EverPartial ? ResourceCollectionState.PartialEvidence : ResourceCollectionState.Collected;
        s.Raw++;
        s.Samples.Add(sample);
        if (s.Samples.Count > _cap) { s.Samples = Downsample(s.Samples); s.Downsampled = true; }
    }

    /// <summary>Halves a series by averaging neighbouring samples of the same instance (an instance change is never merged across).</summary>
    public static List<ResourceSample> Downsample(IReadOnlyList<ResourceSample> samples)
    {
        var result = new List<ResourceSample>(samples.Count / 2 + 1);
        for (var i = 0; i < samples.Count; i++)
        {
            if (i + 1 < samples.Count && samples[i].InstanceId == samples[i + 1].InstanceId) { result.Add(Merge(samples[i], samples[i + 1])); i++; }
            else result.Add(samples[i]);
        }
        return result;
    }

    private static ResourceSample Merge(ResourceSample a, ResourceSample b)
    {
        static double? M(double? x, double? y) => x is { } p && y is { } q ? (p + q) / 2 : x ?? y;
        static long? Last(long? x, long? y) => y ?? x;
        static int? I(int? x, int? y) => x is { } p && y is { } q ? (p + q) / 2 : x ?? y;
        var w = a.Aggregated + b.Aggregated;
        return a with
        {
            At = a.At + (b.At - a.At) / 2, Aggregated = w,
            Memory = a.Memory is null && b.Memory is null ? null : new MemoryResourceSample
            {
                ContainerMemoryBytes = M(a.Memory?.ContainerMemoryBytes, b.Memory?.ContainerMemoryBytes), ContainerMemoryLimitBytes = b.Memory?.ContainerMemoryLimitBytes ?? a.Memory?.ContainerMemoryLimitBytes,
                WorkingSetBytes = M(a.Memory?.WorkingSetBytes, b.Memory?.WorkingSetBytes), PrivateBytes = M(a.Memory?.PrivateBytes, b.Memory?.PrivateBytes),
                ManagedHeapBytes = M(a.Memory?.ManagedHeapBytes, b.Memory?.ManagedHeapBytes), GcHeapAfterGcBytes = M(a.Memory?.GcHeapAfterGcBytes, b.Memory?.GcHeapAfterGcBytes),
                LohBytes = M(a.Memory?.LohBytes, b.Memory?.LohBytes), AllocationRateBytesPerSecond = M(a.Memory?.AllocationRateBytesPerSecond, b.Memory?.AllocationRateBytesPerSecond),
                Gen0Collections = Last(a.Memory?.Gen0Collections, b.Memory?.Gen0Collections), Gen1Collections = Last(a.Memory?.Gen1Collections, b.Memory?.Gen1Collections),
                Gen2Collections = Last(a.Memory?.Gen2Collections, b.Memory?.Gen2Collections), GcPauseMs = b.Memory?.GcPauseMs ?? a.Memory?.GcPauseMs,
            },
            Cpu = a.Cpu is null && b.Cpu is null ? null : new CpuResourceSample
            { CpuPercent = M(a.Cpu?.CpuPercent, b.Cpu?.CpuPercent), ThreadCount = I(a.Cpu?.ThreadCount, b.Cpu?.ThreadCount), HandleCount = I(a.Cpu?.HandleCount, b.Cpu?.HandleCount) },
        };
    }

    /// <summary>Current collected evidence (unassessed). Safe to call at any time.</summary>
    public ResourceStabilityAssessment Snapshot(bool inProgress)
    {
        lock (_gate)
        {
            var components = _state.Values.Select(s => new ResourceComponentObservation
            {
                TargetId = s.Spec.Target.Id, DisplayName = s.Spec.Target.DisplayName, Role = s.Spec.Role, ProviderId = s.ProviderId,
                CollectionState = s.EverCollected ? (s.Failed > 0 && s.LastState == ResourceCollectionState.Collected ? ResourceCollectionState.PartialEvidence : s.LastState) : s.LastState,
                CollectionDetail = s.Detail, Samples = s.Samples.ToList(), RawSampleCount = s.Raw, FailedSampleCount = s.Failed, Downsampled = s.Downsampled,
                Discontinuities = s.Discontinuities.ToList(), Image = s.Image, MemoryLimitBytes = s.MemoryLimit, CpuLimit = s.CpuLimit,
                Limitations = s.Failed > 0 && s.EverCollected ? [$"{s.Failed} sampling attempt(s) failed; the series has gaps."] : [],
            }).ToList();
            return new ResourceStabilityAssessment
            {
                Configured = true, ObservationStart = StartedAt, ObservationEnd = DateTimeOffset.UtcNow, SampleIntervalSeconds = (int)_interval.TotalSeconds,
                Components = components, InProgress = inProgress,
                EvidenceState = components.Where(c => c.Role == ResourceComponentRole.Target).Select(c => c.CollectionState).DefaultIfEmpty(ResourceCollectionState.NotConfigured).First(),
            };
        }
    }

    /// <summary>Stops the timer and waits for the loop (idempotent). Called on completion, cancellation, timeout, failure and shutdown.</summary>
    public async Task StopAsync()
    {
        if (_cts is null) return;
        if (!_cts.IsCancellationRequested) _cts.Cancel();
        if (_loop is { } loop) { try { await loop.WaitAsync(TimeSpan.FromSeconds(30)); } catch (Exception ex) when (ex is TimeoutException or OperationCanceledException) { } }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _cts?.Dispose();
    }
}

/// <summary>At API shutdown: cancel active performance runs so their k6 containers are removed and resource collectors stop (no orphans).</summary>
public sealed class PerformanceTestShutdownService(PerformanceTestExecutionService execution) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => execution.CancelAllAsync(TimeSpan.FromSeconds(25));
}
