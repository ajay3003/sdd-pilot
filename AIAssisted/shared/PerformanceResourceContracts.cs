using System.Globalization;
using System.Text.Json.Serialization;

namespace BirkNext.PerformanceTests;

// Resource Stability (part of Performance Test Review). Evidence about whether a component's resources stay bounded under a controlled workload —
// never a generic memory-leak verdict. Memory increased ≠ leak; heap increased ≠ leak; container memory ≠ managed heap; browser JS heap ≠ Blazor
// managed heap; threshold exceeded ≠ root cause; observability unavailable ≠ performance failure.

public static class ResourceProviderIds
{
    /// <summary>Container memory/CPU of approved Podman containers (podman stats/inspect).</summary>
    public const string Podman = "resource.podman";
    /// <summary>.NET runtime counters (GC heap, allocation rate, collections, working set) — only where genuinely accessible without attaching.</summary>
    public const string DotNetRuntime = "resource.dotnet.runtime";
    public const string Browser = "resource.browser";
    public const string OpenTelemetry = "resource.opentelemetry";
    public const string AppInsights = "resource.appinsights";
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ResourceMetric
{
    ContainerMemoryBytes, WorkingSetBytes, PrivateBytes, ManagedHeapBytes, GcHeapAfterGcBytes, LohBytes, AllocationRateBytesPerSecond,
    Gen2CollectionsPerMinute, GcPauseMsPerMinute, CpuPercent, ThreadCount, HandleCount,
}

/// <summary>Target = the application component under test; LoadGenerator = the k6 container (generator health, never application stability).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ResourceComponentRole { Target, LoadGenerator }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ResourceAssessmentState { StableWithinPolicy, IncreasingWithinTolerance, PotentialRegression, Regression, InsufficientEvidence, NotComparable, NotAssessed, Unavailable }

/// <summary>How collection went — a telemetry state, never a resource result.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ResourceCollectionState { NotConfigured, Collected, PartialEvidence, MetricsUnavailable, ProviderUnavailable, CollectionFailed, TargetNotFound }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ResourceStatistic { LateSteady, Peak, Growth, SlopePerMinute }

// ── Configuration (on the definition) ─────────────────────────────────────────────────────────────────────────────────

/// <summary>An approved component a run may observe. Comes from backend configuration (or is built in) — never a free-form container id.</summary>
public sealed record ResourceObservationTarget
{
    public string Id { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string ProviderId { get; init; } = ResourceProviderIds.Podman;
    /// <summary>Environments it may be observed in (empty = any non-production environment).</summary>
    public List<string> Environments { get; init; } = [];
    public string? Description { get; init; }
}

/// <summary>A stability policy for one metric (optionally one component). No universal defaults: absent a policy, evidence is descriptive only.</summary>
public sealed record ResourceStabilityPolicy
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..12];
    /// <summary>Null = every observed target component that reports the metric.</summary>
    public string? TargetComponentId { get; init; }
    public ResourceMetric Metric { get; init; } = ResourceMetric.ContainerMemoryBytes;
    /// <summary>Accepted growth from early to late steady state, in the metric's unit (bytes, %, count).</summary>
    public double? AllowedAbsoluteGrowth { get; init; }
    public double? AllowedRelativeGrowthPercent { get; init; }
    /// <summary>Accepted steady-state trend (least-squares slope) per minute, in the metric's unit.</summary>
    public double? AllowedSlopePerMinute { get; init; }
    /// <summary>Absolute ceiling for the steady-state 95th percentile (a resource threshold, distinct from drift).</summary>
    public double? MaxValue { get; init; }
    public int? MinimumObservationSeconds { get; init; }
    public int? MinimumSampleCount { get; init; }
    public ThresholdSeverity Severity { get; init; } = ThresholdSeverity.Required;
}

/// <summary>Accepted change of a resource statistic against the baseline run (relative/absolute worsening). Without one, drift is descriptive.</summary>
public sealed record ResourceDriftPolicy
{
    public string? TargetComponentId { get; init; }
    public ResourceMetric Metric { get; init; } = ResourceMetric.ContainerMemoryBytes;
    public ResourceStatistic Statistic { get; init; } = ResourceStatistic.LateSteady;
    public double? AllowedRelativeChangePercent { get; init; }
    public double? AllowedAbsoluteChange { get; init; }
    public ThresholdSeverity Severity { get; init; } = ThresholdSeverity.Required;
}

public sealed record ResourceObservationConfiguration
{
    public bool Enabled { get; init; }
    /// <summary>Approved target ids (see <see cref="ResourceObservationTarget"/>). Each is assessed separately; nothing is summed.</summary>
    public List<string> TargetComponentIds { get; init; } = [];
    /// <summary>Also observe the run's own k6 container as load-generator health.</summary>
    public bool ObserveLoadGenerator { get; init; } = true;
    public int SampleIntervalSeconds { get; init; } = 10;
    /// <summary>Excluded from the steady-state trend (startup, JIT, caches, pools). Null = the workload's warm-up + ramp-up.</summary>
    public int? WarmupExclusionSeconds { get; init; }
    /// <summary>Observation continues this long after the workload ends (post-load recovery; descriptive unless a policy exists).</summary>
    public int CooldownSeconds { get; init; }
    public List<ResourceStabilityPolicy> Policies { get; init; } = [];
    public List<ResourceDriftPolicy> DriftPolicies { get; init; } = [];
    /// <summary>When true, readiness blocks a run whose resource providers are unavailable. Default false: telemetry never gates the load test.</summary>
    public bool RequireResourceEvidence { get; init; }
}

// ── Samples ───────────────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>Only what the provider actually measured. Unavailable values are null — never a synthesized zero.</summary>
public sealed record MemoryResourceSample
{
    public double? ContainerMemoryBytes { get; init; }
    public double? ContainerMemoryLimitBytes { get; init; }
    public double? WorkingSetBytes { get; init; }
    public double? PrivateBytes { get; init; }
    public double? ManagedHeapBytes { get; init; }
    /// <summary>Heap size at the end of the most recent GC (the post-GC floor); set only on the first sample after a new GC.</summary>
    public double? GcHeapAfterGcBytes { get; init; }
    public double? LohBytes { get; init; }
    public double? AllocationRateBytesPerSecond { get; init; }
    public long? Gen0Collections { get; init; }
    public long? Gen1Collections { get; init; }
    public long? Gen2Collections { get; init; }
    public double? GcPauseMs { get; init; }
}

public sealed record CpuResourceSample
{
    public double? CpuPercent { get; init; }
    public int? ThreadCount { get; init; }
    public int? HandleCount { get; init; }
}

public sealed record ResourceSample
{
    public DateTimeOffset At { get; init; }
    public string ProviderId { get; init; } = "";
    public string TargetId { get; init; } = "";
    /// <summary>Process/container instance (container id + start time, or process id + start time). A change is a discontinuity.</summary>
    public string? InstanceId { get; init; }
    public MemoryResourceSample? Memory { get; init; }
    public CpuResourceSample? Cpu { get; init; }
    /// <summary>Number of raw samples this point represents (&gt; 1 after deterministic downsampling).</summary>
    public int Aggregated { get; init; } = 1;
}

public sealed record ResourceDiscontinuity(DateTimeOffset At, string Reason, string? PreviousInstance, string? NewInstance);

// ── Summary, assessment, findings ──────────────────────────────────────────────────────────────────────────────────────

public sealed record ResourcePolicyResult
{
    public string PolicyId { get; init; } = "";
    public ResourceMetric Metric { get; init; }
    public string Rule { get; init; } = "";
    public double? Observed { get; init; }
    public double? Allowed { get; init; }
    /// <summary>Pass / Fail / Warning / NotAssessed (shared CheckOutcome semantics; NotAssessed when the evidence cannot evaluate it).</summary>
    public BirkNext.Applicability.CheckOutcome Outcome { get; init; } = BirkNext.Applicability.CheckOutcome.NotAssessed;
    public ThresholdSeverity Severity { get; init; }
    public string Explanation { get; init; } = "";
}

/// <summary>Per component + metric. Medians of steady-state windows, not single samples; the warm-up is excluded from the trend.</summary>
public sealed record ResourceStabilitySummary
{
    public string TargetId { get; init; } = "";
    public ResourceMetric Metric { get; init; }
    public DateTimeOffset? ObservationStart { get; init; }
    public DateTimeOffset? ObservationEnd { get; init; }
    public int WarmupExcludedSeconds { get; init; }
    public int SampleCount { get; init; }
    public int SteadySampleCount { get; init; }
    public double SteadyObservationSeconds { get; init; }
    public double? StartValue { get; init; }
    /// <summary>Median of the first third of the steady-state window.</summary>
    public double? EarlySteadyValue { get; init; }
    /// <summary>Median of the last third of the steady-state window.</summary>
    public double? LateSteadyValue { get; init; }
    public double? PeakValue { get; init; }
    public double? SteadyP95Value { get; init; }
    public double? EndValue { get; init; }
    public double? AbsoluteGrowth { get; init; }
    public double? RelativeGrowthPercent { get; init; }
    public double? TrendSlopePerMinute { get; init; }
    /// <summary>High / Medium / Low from the fit (R²); Low = the trend explains little of the variation.</summary>
    public string? TrendConfidence { get; init; }
    /// <summary>Median during the cooldown after the workload, when configured.</summary>
    public double? CooldownValue { get; init; }
    public ResourceAssessmentState AssessmentState { get; init; } = ResourceAssessmentState.NotAssessed;
    public string AssessmentReason { get; init; } = "";
    public List<ResourcePolicyResult> PolicyResults { get; init; } = [];
    public List<string> Limitations { get; init; } = [];
}

public sealed record ResourceComponentObservation
{
    public string TargetId { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public ResourceComponentRole Role { get; init; }
    public string ProviderId { get; init; } = "";
    public ResourceCollectionState CollectionState { get; init; } = ResourceCollectionState.NotConfigured;
    public string? CollectionDetail { get; init; }
    /// <summary>Bounded series (downsampled deterministically past the cap).</summary>
    public List<ResourceSample> Samples { get; init; } = [];
    public int RawSampleCount { get; init; }
    public int FailedSampleCount { get; init; }
    public bool Downsampled { get; init; }
    public List<ResourceDiscontinuity> Discontinuities { get; init; } = [];
    /// <summary>Context for interpreting drift: image, memory/CPU limit (only what the provider reports).</summary>
    public string? Image { get; init; }
    public double? MemoryLimitBytes { get; init; }
    public double? CpuLimit { get; init; }
    public List<ResourceMetric> AvailableMetrics { get; init; } = [];
    public List<ResourceMetric> UnavailableMetrics { get; init; } = [];
    public List<ResourceStabilitySummary> Summaries { get; init; } = [];
    public List<string> Limitations { get; init; } = [];
}

/// <summary>Deterministic, evidence-backed wording. There is deliberately no generic "MemoryLeak" finding and no root cause.</summary>
public sealed record ResourceFinding(string Code, string Severity, string TargetId, ResourceMetric? Metric, string Message, string Evidence);

public sealed record ResourceStabilityAssessment
{
    public bool Configured { get; init; }
    public DateTimeOffset? ObservationStart { get; init; }
    public DateTimeOffset? ObservationEnd { get; init; }
    public DateTimeOffset? WorkloadStart { get; init; }
    public DateTimeOffset? WorkloadEnd { get; init; }
    public int SampleIntervalSeconds { get; init; }
    public int WarmupExcludedSeconds { get; init; }
    public int CooldownSeconds { get; init; }
    /// <summary>Hash of the policies the run was assessed with (later policy edits never re-assess it).</summary>
    public string PolicyFingerprint { get; init; } = "";
    /// <summary>Collected / PartialEvidence / ProviderUnavailable … across components (a telemetry state, not a quality result).</summary>
    public ResourceCollectionState EvidenceState { get; init; } = ResourceCollectionState.NotConfigured;
    public List<ResourceComponentObservation> Components { get; init; } = [];
    public List<ResourceFinding> Findings { get; init; } = [];
    public List<string> Limitations { get; init; } = [];
    /// <summary>True while the run is active (persisted periodically so partial evidence survives cancellation or a restart).</summary>
    public bool InProgress { get; init; }
}

public sealed record ResourceMetricDelta
{
    public string TargetId { get; init; } = "";
    public ResourceMetric Metric { get; init; }
    public ResourceStatistic Statistic { get; init; }
    public double? Reference { get; init; }
    public double? Current { get; init; }
    public double? AbsoluteDelta { get; init; }
    public double? RelativePercent { get; init; }
    public MetricChangeDirection Direction { get; init; } = MetricChangeDirection.NotAvailable;
    public PerformanceDriftState? PolicyState { get; init; }
    public string? PolicyText { get; init; }
}

/// <summary>Resource drift against the baseline run — separate from absolute resource policies (both are shown).</summary>
public sealed record ResourceDriftAssessment
{
    public Guid BaselineRunId { get; init; }
    public string? BaselineId { get; init; }
    public bool Compatible { get; init; }
    public List<string> CompatibilityNotes { get; init; } = [];
    public List<ResourceMetricDelta> Deltas { get; init; } = [];
    public PerformanceDriftState State { get; init; } = PerformanceDriftState.NotAssessed;
    public List<ResourceFinding> Findings { get; init; } = [];
}

public sealed record ResourceProviderCapability(string ProviderId, string DisplayName, string Availability, string Detail, List<ResourceMetric> Metrics);

public sealed record ResourceTargetStatus(ResourceObservationTarget Target, bool Allowed, string Availability, string Detail, string? Instance);

public static class ResourceFormat
{
    public static bool LowerIsBetter(ResourceMetric m) => true;

    public static bool IsBytes(ResourceMetric m) => m is ResourceMetric.ContainerMemoryBytes or ResourceMetric.WorkingSetBytes or ResourceMetric.PrivateBytes
        or ResourceMetric.ManagedHeapBytes or ResourceMetric.GcHeapAfterGcBytes or ResourceMetric.LohBytes;

    public static string Label(ResourceMetric m) => m switch
    {
        ResourceMetric.ContainerMemoryBytes => "Container memory",
        ResourceMetric.WorkingSetBytes => "Working set",
        ResourceMetric.PrivateBytes => "Private bytes",
        ResourceMetric.ManagedHeapBytes => "Managed heap",
        ResourceMetric.GcHeapAfterGcBytes => "Managed heap after GC (floor)",
        ResourceMetric.LohBytes => "Large object heap",
        ResourceMetric.AllocationRateBytesPerSecond => "Allocation rate",
        ResourceMetric.Gen2CollectionsPerMinute => "Gen 2 collections",
        ResourceMetric.GcPauseMsPerMinute => "GC pause",
        ResourceMetric.CpuPercent => "CPU",
        ResourceMetric.ThreadCount => "Threads",
        _ => "Handles",
    };

    public static string Format(double? value, ResourceMetric m)
    {
        if (value is not { } v) return "Unavailable";
        string N(double x, string f = "0.#") => x.ToString(f, CultureInfo.InvariantCulture);
        if (IsBytes(m))
        {
            var a = Math.Abs(v);
            return a >= 1024d * 1024 * 1024 ? $"{N(v / (1024d * 1024 * 1024), "0.##")} GiB" : a >= 1024d * 1024 ? $"{N(v / (1024d * 1024))} MiB" : a >= 1024 ? $"{N(v / 1024)} KiB" : $"{N(v, "0")} B";
        }
        return m switch
        {
            ResourceMetric.AllocationRateBytesPerSecond => Format(v, ResourceMetric.ManagedHeapBytes) + "/s",
            ResourceMetric.CpuPercent => $"{N(v)} %",
            ResourceMetric.Gen2CollectionsPerMinute => $"{N(v, "0.##")}/min",
            ResourceMetric.GcPauseMsPerMinute => $"{N(v)} ms/min",
            _ => N(v, "0"),
        };
    }

    public static string State(ResourceAssessmentState s) => s switch
    {
        ResourceAssessmentState.StableWithinPolicy => "Stable within policy",
        ResourceAssessmentState.IncreasingWithinTolerance => "Increasing within tolerance",
        ResourceAssessmentState.PotentialRegression => "Potential resource regression — needs investigation",
        ResourceAssessmentState.Regression => "Resource regression (policy violated)",
        ResourceAssessmentState.InsufficientEvidence => "Insufficient evidence",
        ResourceAssessmentState.NotComparable => "Not comparable",
        ResourceAssessmentState.NotAssessed => "Descriptive only — no policy",
        _ => "Unavailable",
    };

    public static string Collection(ResourceCollectionState s) => s switch
    {
        ResourceCollectionState.NotConfigured => "Not configured",
        ResourceCollectionState.Collected => "Collected",
        ResourceCollectionState.PartialEvidence => "Partial evidence",
        ResourceCollectionState.MetricsUnavailable => "Metrics unavailable",
        ResourceCollectionState.ProviderUnavailable => "Provider unavailable",
        ResourceCollectionState.CollectionFailed => "Collection failed",
        _ => "Target not found",
    };
}
