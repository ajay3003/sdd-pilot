using System.Globalization;
using System.Text.Json.Serialization;
using BirkNext.Applicability;

namespace BirkNext.PerformanceTests;

// ── Performance Test Review: generic, provider-independent contracts ─────────────────────────────────────────────────────
// Configured ≠ Ready ≠ Executed ≠ Completed ≠ Passed. Measured ≠ Passed (a metric without a threshold is measured, never passed).
// Provider unavailable ≠ performance failed; execution failure ≠ threshold failure; no evidence / no score ≠ 0. Load-test metrics are their own
// evidence type: never merged with single-request API latency (API Quality Review) or browser performance (Lighthouse / Core Web Vitals).
// Nothing here names a provider concept (k6 VUs-as-executors, stages, metric keys, scripts): providers translate these models.

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PerformanceTargetType { RestHttp, GraphQlHttp, GenericHttp }

/// <summary>Why the workload is applied. Baseline = small reference load; Load = expected load; Stress = beyond expected capacity (controlled);
/// Soak = sustained load over a longer period.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WorkloadPurpose { Baseline, Load, Stress, Soak }

/// <summary>Concurrent virtual users, or a target arrival rate (requests started per second).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WorkloadMode { VirtualUsers, ArrivalRate }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PerformanceMetric { LatencyP50Ms, LatencyP90Ms, LatencyP95Ms, LatencyP99Ms, LatencyMeanMs, LatencyMaxMs, ErrorRatePercent, ThroughputRps, FailedRequests }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ThresholdOperator { LessThan, LessThanOrEqual, GreaterThan, GreaterThanOrEqual }

/// <summary>Required: a miss fails the run's quality. Advisory: a miss is a warning.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ThresholdSeverity { Required, Advisory }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TestDataSelection { RoundRobin, Sequential, Random, UniquePerVirtualUser }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PerformanceReadinessState { Ready, Optional, NeedsConfiguration, NeedsAuthentication, NeedsTestData, ProviderUnavailable, UnsafeEnvironment, InvalidScenario, Blocked }

/// <summary>Execution only. A threshold miss is a quality result of a Completed run, never ExecutionFailed.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PerformanceRunState { Queued, Preparing, Running, Cancelling, Cancelled, Completed, ExecutionFailed, TimedOut, Blocked }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ProviderAvailability { Available, Unavailable, VersionUnsupported, Misconfigured }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PerformanceQualityVerdict { Pass, Warning, Fail, NotAssessed }

/// <summary>Descriptive direction of a metric change. Not a verdict: "Worse" is not "Regression".</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MetricChangeDirection { Improved, Unchanged, Worse, NotAvailable }

/// <summary>Drift against a baseline. Regression only when an explicit drift policy is violated.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PerformanceDriftState { Improved, Stable, DegradedWithinTolerance, Regression, NotComparable, NotAssessed }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PerformanceBaselineStatus { Active, Superseded, Historical }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PerformanceComparisonKind { OriginalBaseline, RunVsActiveBaseline, RunVsHistoricalBaseline, RunVsRun }

public sealed record PerformanceNameValue(string Name, string Value);

/// <summary>One HTTP request of a scenario. The destination is always <see cref="RelativePath"/> under the definition's configured target origin.</summary>
public sealed record HttpPerformanceStep
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..12];
    public string Name { get; init; } = "";
    public string Method { get; init; } = "GET";
    public string RelativePath { get; init; } = "/";
    public List<PerformanceNameValue> Headers { get; init; } = [];
    public List<PerformanceNameValue> QueryParameters { get; init; } = [];
    public string? BodyTemplate { get; init; }
    public string? ContentType { get; init; }
    public List<int> ExpectedStatusCodes { get; init; } = [200];
    public int? ThinkTimeMs { get; init; }
    /// <summary>A POST that only reads (search/query API). Required for a REST POST; never allows PUT/PATCH/DELETE.</summary>
    public bool PostIsSafeRead { get; init; }
    /// <summary>GraphQL target: the operation document (query only — mutations and subscriptions are blocked), variables JSON and operation name.</summary>
    public string? GraphQlQuery { get; init; }
    public string? GraphQlVariablesJson { get; init; }
    public string? GraphQlOperationName { get; init; }
}

public sealed record PerformanceScenario
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..12];
    public string Name { get; init; } = "";
    public List<HttpPerformanceStep> Steps { get; init; } = [];
    /// <summary>An approved synthetic data profile whose columns resolve <c>{placeholder}</c> values. Null = no parameters.</summary>
    public string? TestDataProfileId { get; init; }
}

public sealed record PerformanceWorkload
{
    public WorkloadPurpose Purpose { get; init; } = WorkloadPurpose.Baseline;
    public WorkloadMode Mode { get; init; } = WorkloadMode.VirtualUsers;
    /// <summary>Target concurrency (VirtualUsers mode) — also the generator pool ceiling in ArrivalRate mode.</summary>
    public int? VirtualUsers { get; init; } = 5;
    /// <summary>Arrival-rate mode: rate at the start of ramp-up and the target rate held in steady state (requests per second).</summary>
    public double? StartRequestsPerSecond { get; init; }
    public double? RequestsPerSecond { get; init; }
    public int WarmupSeconds { get; init; } = 30;
    public int RampUpSeconds { get; init; } = 30;
    public int SteadyStateSeconds { get; init; } = 120;
    public int RampDownSeconds { get; init; } = 15;
    /// <summary>Upper bound for the whole workload; null = the sum of the phases.</summary>
    public int? MaxDurationSeconds { get; init; }

    [JsonIgnore] public int TotalSeconds => WarmupSeconds + RampUpSeconds + SteadyStateSeconds + RampDownSeconds;
}

/// <summary>An absolute expectation on a measured metric (BirkNext evaluates it; providers never decide it).</summary>
public sealed record PerformanceThreshold
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..12];
    public PerformanceMetric Metric { get; init; }
    public ThresholdOperator Operator { get; init; } = ThresholdOperator.LessThan;
    public double Value { get; init; }
    public ThresholdSeverity Severity { get; init; } = ThresholdSeverity.Required;
}

/// <summary>Accepted change against the baseline. Without a policy, drift is descriptive only (deltas, never Regression).</summary>
public sealed record PerformanceDriftPolicy
{
    public PerformanceMetric Metric { get; init; }
    /// <summary>Accepted worsening in percent of the baseline value (latency +20 → regression above +20 %; throughput 15 → regression below −15 %).</summary>
    public double? AllowedRelativeChangePercent { get; init; }
    /// <summary>Accepted worsening in the metric's unit (ms, percentage points for error rate, requests for failed requests).</summary>
    public double? AllowedAbsoluteChange { get; init; }
    public ThresholdSeverity Severity { get; init; } = ThresholdSeverity.Required;
}

/// <summary>Per-definition limits; the backend's configured maximums always win (a definition can only be stricter).</summary>
public sealed record PerformanceTestSafetyPolicy
{
    public int? MaxVirtualUsers { get; init; }
    public double? MaxRequestsPerSecond { get; init; }
    public int? MaxDurationSeconds { get; init; }
    public long? MaxTotalRequests { get; init; }
}

/// <summary>Effective limits after combining the backend maximums with a definition's own policy.</summary>
public sealed record PerformanceSafetyLimits(int MaxVirtualUsers, double MaxRequestsPerSecond, int MaxDurationSeconds, int MaxSoakDurationSeconds, long MaxTotalRequests,
    bool AllowStress, bool AllowSoak);

/// <summary>Approved test data: synthetic/QA values referenced by placeholders. No secrets and no realistic identity numbers.</summary>
public sealed record PerformanceTestDataProfile
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..12];
    public string EnvironmentId { get; init; } = "";
    public string Name { get; init; } = "";
    public string? Description { get; init; }
    public List<string> Columns { get; init; } = [];
    public List<List<string>> Rows { get; init; } = [];
    public TestDataSelection Selection { get; init; } = TestDataSelection.RoundRobin;
    /// <summary>The person saving the profile confirms the values are synthetic or approved QA test data.</summary>
    public bool ApprovedSynthetic { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed record PerformanceTestDefinition
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string EnvironmentId { get; init; } = "";
    public string? ProjectId { get; init; }
    public string Name { get; init; } = "";
    public string? Description { get; init; }
    public PerformanceTargetType TargetType { get; init; } = PerformanceTargetType.RestHttp;
    /// <summary>Scheme + host (+ port) of the configured Target Environment. Steps are always relative to it; no other destination exists.</summary>
    public string TargetOrigin { get; init; } = "";
    /// <summary>The Target Environment classification (Local, Development, QA, Test, RC …) the definition was saved for.</summary>
    public string EnvironmentType { get; init; } = "";
    public string EnvironmentName { get; init; } = "";
    public PerformanceScenario Scenario { get; init; } = new();
    public PerformanceWorkload Workload { get; init; } = new();
    public List<PerformanceThreshold> Thresholds { get; init; } = [];
    public List<PerformanceDriftPolicy> DriftPolicies { get; init; } = [];
    public string ProviderId { get; init; } = PerformanceProviderIds.K6;
    /// <summary>Null = unauthenticated. Authentication reuses the Target Environment; no credential is ever stored here.</summary>
    public string? AuthenticationReference { get; init; }
    public PerformanceTestSafetyPolicy SafetyPolicy { get; init; } = new();
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    /// <summary>Incremented on every saved change; runs keep the version and a full snapshot.</summary>
    public int Version { get; init; } = 1;
    /// <summary>Hash of the whole definition content (thresholds and drift policies included).</summary>
    public string Fingerprint { get; init; } = "";
    /// <summary>Hash of what makes two runs comparable: target, type, scenario steps, workload, environment class, test-data semantics.
    /// Thresholds, drift policies and display names are excluded.</summary>
    public string ComparisonFingerprint { get; init; } = "";
    /// <summary>Archived instead of deleted when runs reference it.</summary>
    public bool Archived { get; init; }
}

public static class PerformanceProviderIds
{
    public const string K6 = "performance.k6";
}

public sealed record PerformanceProviderCapabilities
{
    public bool Http { get; init; }
    public bool GraphQl { get; init; }
    public List<WorkloadPurpose> Purposes { get; init; } = [];
    public List<WorkloadMode> Modes { get; init; } = [];
    public bool Cancellation { get; init; }
    public List<PerformanceMetric> Metrics { get; init; } = [];
    public bool RequiresExternalExecutable { get; init; }
}

public sealed record PerformanceProviderStatus
{
    public string ProviderId { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public ProviderAvailability Availability { get; init; } = ProviderAvailability.Unavailable;
    public string? Version { get; init; }
    public string Detail { get; init; } = "";
    public PerformanceProviderCapabilities Capabilities { get; init; } = new();
}

public sealed record PerformanceReadinessItem(string Key, string Label, PerformanceReadinessState State, string Detail, bool Blocking);

public sealed record PerformanceTestReadiness
{
    public bool Ready { get; init; }
    public List<PerformanceReadinessItem> Items { get; init; } = [];
    /// <summary>Tester-friendly reasons the run cannot start. Empty when ready.</summary>
    public List<string> Blockers { get; init; } = [];
    public PerformanceSafetyLimits? Limits { get; init; }
    public int EstimatedDurationSeconds { get; init; }
    /// <summary>Upper bound of requests the workload can issue (exact for arrival rate; conservative for virtual users).</summary>
    public long EstimatedMaxRequests { get; init; }
    public List<string> Timeline { get; init; } = [];
    public PerformanceProviderStatus? Provider { get; init; }
}

public sealed record PerformanceLatency
{
    public double? MinMs { get; init; }
    public double? MeanMs { get; init; }
    public double? P50Ms { get; init; }
    public double? P90Ms { get; init; }
    public double? P95Ms { get; init; }
    public double? P99Ms { get; init; }
    public double? MaxMs { get; init; }
}

public sealed record PerformanceStepMetrics(string StepName, long RequestCount, double? ErrorRatePercent, double? P95Ms, double? P99Ms, double? RequestsPerSecond);

/// <summary>Normalized, provider-independent metrics. Latency in milliseconds, throughput in requests/second, error rate in percent (0–100).</summary>
public sealed record PerformanceMetrics
{
    public long RequestCount { get; init; }
    public long SuccessfulRequests { get; init; }
    public long FailedRequests { get; init; }
    public double? ErrorRatePercent { get; init; }
    public double? RequestsPerSecond { get; init; }
    public double DurationSeconds { get; init; }
    public PerformanceLatency Latency { get; init; } = new();
    public List<PerformanceStepMetrics> Steps { get; init; } = [];
    /// <summary>Iterations the load generator could not start (not enough generator capacity for the configured rate). Null = not reported.</summary>
    public long? DroppedIterations { get; init; }

    public double? Value(PerformanceMetric metric) => metric switch
    {
        PerformanceMetric.LatencyP50Ms => Latency.P50Ms,
        PerformanceMetric.LatencyP90Ms => Latency.P90Ms,
        PerformanceMetric.LatencyP95Ms => Latency.P95Ms,
        PerformanceMetric.LatencyP99Ms => Latency.P99Ms,
        PerformanceMetric.LatencyMeanMs => Latency.MeanMs,
        PerformanceMetric.LatencyMaxMs => Latency.MaxMs,
        PerformanceMetric.ErrorRatePercent => ErrorRatePercent,
        PerformanceMetric.ThroughputRps => RequestsPerSecond,
        _ => FailedRequests,
    };
}

public sealed record PerformanceThresholdResult
{
    public string ThresholdId { get; init; } = "";
    public PerformanceMetric Metric { get; init; }
    public ThresholdOperator Operator { get; init; }
    public double Expected { get; init; }
    public double? Measured { get; init; }
    public ThresholdSeverity Severity { get; init; }
    /// <summary>Pass / Fail (required miss) / Warning (advisory miss) / NotAssessed (metric unavailable or run not completed).</summary>
    public CheckOutcome Outcome { get; init; }
    public string Explanation { get; init; } = "";
}

public sealed record PerformanceMetricDelta
{
    public PerformanceMetric Metric { get; init; }
    public double? Reference { get; init; }
    public double? Current { get; init; }
    public double? AbsoluteDelta { get; init; }
    /// <summary>Null when the reference is 0 or a value is missing (no invalid/infinite relative change).</summary>
    public double? RelativePercent { get; init; }
    public MetricChangeDirection Direction { get; init; }
    /// <summary>Set when a drift policy exists for the metric.</summary>
    public PerformanceDriftState? PolicyState { get; init; }
    public string? PolicyText { get; init; }
}

/// <summary>A drift policy was violated: performance changed beyond the accepted tolerance. Says nothing about why.</summary>
public sealed record PerformanceRegressionFinding(PerformanceMetric Metric, double Baseline, double Current, double Delta, string Policy, ThresholdSeverity Severity);

public sealed record PerformanceDriftAssessment
{
    public string BaselineId { get; init; } = "";
    public int BaselineVersion { get; init; }
    public Guid BaselineRunId { get; init; }
    public bool Compatible { get; init; }
    public List<string> CompatibilityNotes { get; init; } = [];
    public List<PerformanceMetricDelta> Deltas { get; init; } = [];
    public PerformanceDriftState State { get; init; } = PerformanceDriftState.NotAssessed;
    public List<PerformanceRegressionFinding> Findings { get; init; } = [];
}

/// <summary>Where a run executed from (no host name, no user identity) — helps interpret load-generator limits.</summary>
public sealed record PerformanceExecutionHost(string OperatingSystem, int LogicalProcessors, string BirkNextVersion);

public sealed record PerformanceTestRun
{
    public Guid RunId { get; init; } = Guid.NewGuid();
    public string EnvironmentId { get; init; } = "";
    public string? ProjectId { get; init; }
    public string DefinitionId { get; init; } = "";
    public int DefinitionVersion { get; init; }
    public string DefinitionFingerprint { get; init; } = "";
    public string ComparisonFingerprint { get; init; } = "";
    /// <summary>The exact definition this run executed (thresholds, drift policies, workload). Later edits never change it.</summary>
    public PerformanceTestDefinition DefinitionSnapshot { get; init; } = new();
    public string ProviderId { get; init; } = "";
    public string? ProviderVersion { get; init; }
    public string TargetOrigin { get; init; } = "";
    public string EnvironmentType { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }
    public PerformanceRunState State { get; init; } = PerformanceRunState.Queued;
    public string? StateReason { get; init; }
    public PerformanceMetrics? Metrics { get; init; }
    /// <summary>True when metrics cover only part of the workload (cancelled/timed out): never assessed against thresholds.</summary>
    public bool MetricsPartial { get; init; }
    public List<PerformanceThresholdResult> ThresholdResults { get; init; } = [];
    public PerformanceQualityVerdict Verdict { get; init; } = PerformanceQualityVerdict.NotAssessed;
    /// <summary>Shared ScoreSemantics over the threshold outcomes; null quality when nothing was assessed — never 0.</summary>
    public QualityResult? Quality { get; init; }
    /// <summary>The baseline active for this run's scope when the run was created. Never re-bound later.</summary>
    public string? BaselineIdAtRun { get; init; }
    public PerformanceDriftAssessment? Drift { get; init; }
    public List<string> Limitations { get; init; } = [];
    public string MetricsSource { get; init; } = "";
    public PerformanceExecutionHost? Host { get; init; }
    /// <summary>Telemetry enrichment state for the run window (optional; never affects quality).</summary>
    public string Observability { get; init; } = "Not configured";
    public string? VersionLabel { get; init; }
    public Guid? SourceSnapshotId { get; init; }
    /// <summary>Bounded, redacted provider diagnostics (provider-specific, never the source of UI logic).</summary>
    public string? ProviderDiagnostics { get; init; }

    [JsonIgnore] public bool IsActive => State is PerformanceRunState.Queued or PerformanceRunState.Preparing or PerformanceRunState.Running or PerformanceRunState.Cancelling;
}

public sealed record PerformanceBaseline
{
    public string BaselineId { get; init; } = Guid.NewGuid().ToString("N");
    public string EnvironmentId { get; init; } = "";
    public string? ProjectId { get; init; }
    public string DefinitionId { get; init; } = "";
    /// <summary>Scope: one Active baseline per definition + environment + comparison fingerprint.</summary>
    public string ComparisonFingerprint { get; init; } = "";
    public Guid RunId { get; init; }
    public int Version { get; init; }
    public string? Name { get; init; }
    public string? Reason { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset EffectiveFrom { get; init; }
    public DateTimeOffset? SupersededAt { get; init; }
    public string? SupersededByBaselineId { get; init; }
    public PerformanceBaselineStatus Status { get; init; } = PerformanceBaselineStatus.Active;
}

public sealed record PerformanceRunComparison
{
    public PerformanceComparisonKind Kind { get; init; }
    /// <summary>True for any comparison other than the run's original baseline assessment (never persisted as the run's result).</summary>
    public bool AdHoc { get; init; }
    public Guid CurrentRunId { get; init; }
    public Guid ReferenceRunId { get; init; }
    public string? ReferenceBaselineId { get; init; }
    public bool Compatible { get; init; }
    public List<string> CompatibilityNotes { get; init; } = [];
    public List<PerformanceMetricDelta> Deltas { get; init; } = [];
    public PerformanceDriftAssessment? Drift { get; init; }
}

public sealed record PerformanceRunRequest
{
    public string DefinitionId { get; init; } = "";
    /// <summary>Optional release/deployment version label recorded with the run.</summary>
    public string? VersionLabel { get; init; }
}

public sealed record PerformanceBaselinePromotion
{
    public Guid RunId { get; init; }
    public string? Name { get; init; }
    public string? Reason { get; init; }
    /// <summary>Required to promote a completed run whose required thresholds failed (the UI warns first).</summary>
    public bool AcceptThresholdFailures { get; init; }
}

public sealed record PerformanceBaselinePromotionResult(PerformanceBaseline? Baseline, PerformanceBaseline? Superseded, string? Error);

public sealed record PerformanceTestOverview
{
    public List<PerformanceTestDefinition> Definitions { get; init; } = [];
    public List<PerformanceTestDataProfile> DataProfiles { get; init; } = [];
    public PerformanceProviderStatus? Provider { get; init; }
    public PerformanceSafetyLimits? Limits { get; init; }
}

// ── Pure, deterministic rules shared by backend (authoritative) and UI (preview) ───────────────────────────────────────

public static class PerformanceTestRules
{
    public static readonly string[] ReadOnlyMethods = ["GET", "HEAD", "OPTIONS"];
    public static readonly string[] BlockedMethods = ["PUT", "PATCH", "DELETE", "CONNECT", "TRACE"];

    public static bool LowerIsBetter(PerformanceMetric m) => m != PerformanceMetric.ThroughputRps;

    public static string Unit(PerformanceMetric m) => m switch
    {
        PerformanceMetric.ErrorRatePercent => "%",
        PerformanceMetric.ThroughputRps => "req/s",
        PerformanceMetric.FailedRequests => "requests",
        _ => "ms",
    };

    public static string Label(PerformanceMetric m) => m switch
    {
        PerformanceMetric.LatencyP50Ms => "P50 latency",
        PerformanceMetric.LatencyP90Ms => "P90 latency",
        PerformanceMetric.LatencyP95Ms => "P95 latency",
        PerformanceMetric.LatencyP99Ms => "P99 latency",
        PerformanceMetric.LatencyMeanMs => "Mean latency",
        PerformanceMetric.LatencyMaxMs => "Max latency",
        PerformanceMetric.ErrorRatePercent => "Error rate",
        PerformanceMetric.ThroughputRps => "Throughput",
        _ => "Failed requests",
    };

    public static string Symbol(ThresholdOperator o) => o switch
    {
        ThresholdOperator.LessThan => "<",
        ThresholdOperator.LessThanOrEqual => "≤",
        ThresholdOperator.GreaterThan => ">",
        _ => "≥",
    };

    public static string Format(double? value, PerformanceMetric metric) => value is not { } v ? "—"
        : metric switch
        {
            PerformanceMetric.ErrorRatePercent => v.ToString("0.##", CultureInfo.InvariantCulture) + " %",
            PerformanceMetric.ThroughputRps => v.ToString("0.##", CultureInfo.InvariantCulture) + " req/s",
            PerformanceMetric.FailedRequests => v.ToString("0", CultureInfo.InvariantCulture),
            _ => v.ToString("0.#", CultureInfo.InvariantCulture) + " ms",
        };

    public static bool Satisfies(double measured, ThresholdOperator op, double expected) => op switch
    {
        ThresholdOperator.LessThan => measured < expected,
        ThresholdOperator.LessThanOrEqual => measured <= expected,
        ThresholdOperator.GreaterThan => measured > expected,
        _ => measured >= expected,
    };

    /// <summary>
    /// BirkNext's threshold evaluation (providers never decide it). A metric without a threshold is measured, not passed. Only a Completed
    /// run with complete metrics is assessed; anything else is NotAssessed — cancellation, timeout and execution failure are never quality.
    /// </summary>
    public static List<PerformanceThresholdResult> EvaluateThresholds(IEnumerable<PerformanceThreshold> thresholds, PerformanceMetrics? metrics, bool assessable) =>
        thresholds.Select(t =>
        {
            var measured = metrics?.Value(t.Metric);
            if (!assessable || measured is null)
                return new PerformanceThresholdResult
                {
                    ThresholdId = t.Id, Metric = t.Metric, Operator = t.Operator, Expected = t.Value, Measured = assessable ? null : measured, Severity = t.Severity,
                    Outcome = CheckOutcome.NotAssessed,
                    Explanation = !assessable ? "Not assessed: the run did not complete with full metrics." : "Not assessed: the provider did not report this metric.",
                };
            var ok = Satisfies(measured.Value, t.Operator, t.Value);
            return new PerformanceThresholdResult
            {
                ThresholdId = t.Id, Metric = t.Metric, Operator = t.Operator, Expected = t.Value, Measured = measured, Severity = t.Severity,
                Outcome = ok ? CheckOutcome.Pass : t.Severity == ThresholdSeverity.Required ? CheckOutcome.Fail : CheckOutcome.Warning,
                Explanation = $"{Label(t.Metric)} {Format(measured, t.Metric)} {(ok ? "met" : "did not meet")} the {t.Severity.ToString().ToLowerInvariant()} expectation {Symbol(t.Operator)} {Format(t.Value, t.Metric)}.",
            };
        }).ToList();

    /// <summary>Shared ScoreSemantics over the threshold outcomes (no second scoring model) and the run verdict.</summary>
    public static (QualityResult Quality, PerformanceQualityVerdict Verdict) Quality(IReadOnlyCollection<PerformanceThresholdResult> results)
    {
        var quality = ScoreSemantics.Compute(results.Select(r => r.Outcome));
        var verdict = quality.QualityPercent is null ? PerformanceQualityVerdict.NotAssessed
            : results.Any(r => r.Outcome == CheckOutcome.Fail) ? PerformanceQualityVerdict.Fail
            : results.Any(r => r.Outcome == CheckOutcome.Warning) ? PerformanceQualityVerdict.Warning
            : PerformanceQualityVerdict.Pass;
        return (quality, verdict);
    }

    public static readonly PerformanceMetric[] ComparedMetrics =
        [PerformanceMetric.LatencyP50Ms, PerformanceMetric.LatencyP90Ms, PerformanceMetric.LatencyP95Ms, PerformanceMetric.LatencyP99Ms, PerformanceMetric.LatencyMeanMs,
         PerformanceMetric.ThroughputRps, PerformanceMetric.ErrorRatePercent];

    /// <summary>Within ±1 % (relative) or an exactly equal value: Unchanged. Descriptive only.</summary>
    public const double UnchangedTolerancePercent = 1.0;

    public static PerformanceMetricDelta Delta(PerformanceMetric metric, double? reference, double? current)
    {
        if (reference is not { } r || current is not { } c)
            return new PerformanceMetricDelta { Metric = metric, Reference = reference, Current = current, Direction = MetricChangeDirection.NotAvailable };
        var abs = Math.Round(c - r, 3);
        double? rel = r == 0 ? null : Math.Round((c - r) / r * 100, 1);
        var unchanged = abs == 0 || rel is { } p && Math.Abs(p) < UnchangedTolerancePercent;
        var better = LowerIsBetter(metric) ? c < r : c > r;
        return new PerformanceMetricDelta
        {
            Metric = metric, Reference = r, Current = c, AbsoluteDelta = abs, RelativePercent = rel,
            Direction = unchanged ? MetricChangeDirection.Unchanged : better ? MetricChangeDirection.Improved : MetricChangeDirection.Worse,
        };
    }

    /// <summary>Why two runs are not directly comparable (empty = compatible): environment, target, scenario, workload, test data.</summary>
    public static List<string> CompatibilityNotes(PerformanceTestRun current, PerformanceTestRun reference)
    {
        var notes = new List<string>();
        if (!string.Equals(current.EnvironmentId, reference.EnvironmentId, StringComparison.Ordinal)) notes.Add("Not comparable — different environment.");
        if (!string.Equals(current.TargetOrigin, reference.TargetOrigin, StringComparison.OrdinalIgnoreCase)) notes.Add("Not comparable — different target.");
        if (current.ComparisonFingerprint != reference.ComparisonFingerprint)
        {
            var a = current.DefinitionSnapshot; var b = reference.DefinitionSnapshot;
            if (a.Workload != b.Workload) notes.Add("Not comparable — workload changed.");
            if (!StepsEqual(a.Scenario, b.Scenario) || a.TargetType != b.TargetType) notes.Add("Not comparable — scenario changed.");
            if (a.Scenario.TestDataProfileId != b.Scenario.TestDataProfileId) notes.Add("Not comparable — test data profile changed.");
            if (!string.Equals(a.EnvironmentType, b.EnvironmentType, StringComparison.OrdinalIgnoreCase)) notes.Add("Not comparable — environment classification changed.");
            if (notes.Count == 0) notes.Add("Not comparable — the comparison fingerprint differs.");
        }
        if (reference.State != PerformanceRunState.Completed || reference.MetricsPartial || reference.Metrics is null) notes.Add("Not comparable — the reference run has no complete metrics.");
        if (current.State != PerformanceRunState.Completed || current.MetricsPartial || current.Metrics is null) notes.Add("Not comparable — this run has no complete metrics.");
        return notes;
    }

    private static bool StepsEqual(PerformanceScenario a, PerformanceScenario b) =>
        System.Text.Json.JsonSerializer.Serialize(a.Steps) == System.Text.Json.JsonSerializer.Serialize(b.Steps);

    public static List<PerformanceMetricDelta> Deltas(PerformanceMetrics? reference, PerformanceMetrics? current) =>
        ComparedMetrics.Select(m => Delta(m, reference?.Value(m), current?.Value(m))).ToList();

    /// <summary>
    /// Drift of <paramref name="current"/> against a baseline run. Compatibility first (an incompatible pair is NotComparable, no authoritative drift).
    /// Without drift policies the deltas are descriptive and the state is NotAssessed ("comparison only"). Regression only on a violated policy.
    /// </summary>
    public static PerformanceDriftAssessment Drift(PerformanceTestRun current, PerformanceTestRun baselineRun, PerformanceBaseline baseline, IReadOnlyCollection<PerformanceDriftPolicy> policies)
    {
        var notes = CompatibilityNotes(current, baselineRun);
        var deltas = Deltas(baselineRun.Metrics, current.Metrics);
        var envelope = new PerformanceDriftAssessment
        {
            BaselineId = baseline.BaselineId, BaselineVersion = baseline.Version, BaselineRunId = baseline.RunId, Compatible = notes.Count == 0, CompatibilityNotes = notes,
        };
        if (notes.Count > 0) return envelope with { Deltas = deltas, State = PerformanceDriftState.NotComparable };
        if (policies.Count == 0) return envelope with { Deltas = deltas, State = PerformanceDriftState.NotAssessed };

        var findings = new List<PerformanceRegressionFinding>();
        var evaluated = deltas.Select(d =>
        {
            var policy = policies.FirstOrDefault(p => p.Metric == d.Metric);
            if (policy is null || d.Reference is not { } r || d.Current is not { } c) return d;
            var worsening = LowerIsBetter(d.Metric) ? c - r : r - c; // > 0 = worse
            var relWorse = r == 0 ? (double?)null : worsening / r * 100;
            var violated = (policy.AllowedRelativeChangePercent is { } rel && relWorse is { } rw && rw > rel)
                || (policy.AllowedAbsoluteChange is { } absAllowed && worsening > absAllowed);
            var text = string.Join(" or ", new[]
            {
                policy.AllowedRelativeChangePercent is { } a ? $"{(LowerIsBetter(d.Metric) ? "+" : "−")}{a.ToString("0.#", CultureInfo.InvariantCulture)} %" : null,
                policy.AllowedAbsoluteChange is { } b ? $"{(LowerIsBetter(d.Metric) ? "+" : "−")}{Format(b, d.Metric)}{(d.Metric == PerformanceMetric.ErrorRatePercent ? " (percentage points)" : "")}" : null,
            }.Where(t => t is not null));
            PerformanceDriftState state;
            if (violated)
            {
                state = PerformanceDriftState.Regression;
                findings.Add(new PerformanceRegressionFinding(d.Metric, r, c, Math.Round(c - r, 3), $"Accepted change {text}", policy.Severity));
            }
            else state = d.Direction switch
            {
                MetricChangeDirection.Improved => PerformanceDriftState.Improved,
                MetricChangeDirection.Worse => PerformanceDriftState.DegradedWithinTolerance,
                _ => PerformanceDriftState.Stable,
            };
            return d with { PolicyState = state, PolicyText = $"Accepted change {text}" };
        }).ToList();
        var states = evaluated.Where(d => d.PolicyState is not null).Select(d => d.PolicyState!.Value).ToList();
        var overall = states.Count == 0 ? PerformanceDriftState.NotAssessed
            : states.Contains(PerformanceDriftState.Regression) ? PerformanceDriftState.Regression
            : states.Contains(PerformanceDriftState.DegradedWithinTolerance) ? PerformanceDriftState.DegradedWithinTolerance
            : states.Contains(PerformanceDriftState.Improved) ? PerformanceDriftState.Improved
            : PerformanceDriftState.Stable;
        return envelope with { Deltas = evaluated, State = overall, Findings = findings };
    }

    /// <summary>Phase-by-phase text timeline, e.g. "00:00–00:30 Warm-up at 1 virtual user".</summary>
    public static List<string> Timeline(PerformanceWorkload w)
    {
        static string T(int s) => $"{s / 60:00}:{s % 60:00}";
        var unit = w.Mode == WorkloadMode.VirtualUsers ? "virtual users" : "req/s";
        var target = w.Mode == WorkloadMode.VirtualUsers ? (double)(w.VirtualUsers ?? 0) : w.RequestsPerSecond ?? 0;
        var start = w.Mode == WorkloadMode.VirtualUsers ? WarmupVirtualUsers(w) : w.StartRequestsPerSecond ?? Math.Max(1, Math.Round(target / 10, 1));
        string N(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
        var list = new List<string>();
        var t = 0;
        if (w.WarmupSeconds > 0) { list.Add($"{T(t)}–{T(t + w.WarmupSeconds)} Warm-up at {N(start)} {unit}"); t += w.WarmupSeconds; }
        if (w.RampUpSeconds > 0) { list.Add($"{T(t)}–{T(t + w.RampUpSeconds)} Ramp {N(start)} → {N(target)} {unit}"); t += w.RampUpSeconds; }
        if (w.SteadyStateSeconds > 0) { list.Add($"{T(t)}–{T(t + w.SteadyStateSeconds)} Hold {N(target)} {unit}"); t += w.SteadyStateSeconds; }
        if (w.RampDownSeconds > 0) { list.Add($"{T(t)}–{T(t + w.RampDownSeconds)} Ramp down to 0"); }
        return list;
    }

    /// <summary>Warm-up concurrency: a tenth of the target, at least 1.</summary>
    public static int WarmupVirtualUsers(PerformanceWorkload w) => Math.Max(1, (int)Math.Ceiling((w.VirtualUsers ?? 1) / 10.0));
}
