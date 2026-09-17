using System.Text.Json.Serialization;

namespace BirkNext.Api.Services.IntegrationQuality;

/// <summary>
/// Phase 3, Checkpoint 6: derives performance metrics from observed runtime evidence.
///
/// This is the single authoritative implementation. Report building, the snapshot layer, the
/// Razor page and the export all consume its output; none of them recompute statistics.
///
/// Metrics are produced only from evidence that actually carries a measured duration. Nothing is
/// inferred from reachability, from an HTTP 200, from a configured timeout, or from the absence
/// of errors. Where a metric cannot be computed it stays null, because a zero would be
/// indistinguishable from a real measurement of zero.
/// </summary>
public sealed class IntegrationPerformanceAnalyzer
{
    /// <summary>
    /// Below this many timed samples, percentiles are reported but the evidence state is
    /// downgraded, because a handful of samples cannot support a distribution claim.
    /// </summary>
    public const int MinimumSamplesForDistribution = 5;

    public IntegrationPerformanceMetrics Analyze(IReadOnlyList<RuntimeIntegrationEvidence>? evidence)
    {
        if (evidence is null || evidence.Count == 0)
            return new IntegrationPerformanceMetrics
            {
                EvidenceState = PerformanceEvidenceState.Unavailable
            };

        var durations = evidence
            .Where(e => e.DurationMs.HasValue)
            .Select(e => e.DurationMs!.Value)
            .OrderBy(d => d)
            .ToList();

        var outcomes = evidence
            .Where(e => e.Outcome != RuntimeEvidenceOutcome.Unknown)
            .ToList();

        var successCount = outcomes.Count(e => e.Outcome == RuntimeEvidenceOutcome.Success);
        var failureCount = outcomes.Count(e => e.Outcome == RuntimeEvidenceOutcome.Error);

        var firstObservedAt = evidence.Min(e => e.ObservedAt);
        var lastObservedAt = evidence.Max(e => e.ObservedAt);
        var windowSeconds = (lastObservedAt - firstObservedAt).TotalSeconds;

        // Throughput needs a real elapsed window. Samples that collapse to one instant give no
        // window, and dividing by it would turn "100 samples" into "100 per second".
        double? throughput = windowSeconds > 0
            ? evidence.Count / windowSeconds
            : null;

        var state = durations.Count switch
        {
            0 => PerformanceEvidenceState.Unavailable,
            < MinimumSamplesForDistribution => PerformanceEvidenceState.InsufficientSamples,
            _ => PerformanceEvidenceState.Observed
        };

        return new IntegrationPerformanceMetrics
        {
            EvidenceState = state,

            SampleCount = evidence.Count,
            TimedSampleCount = durations.Count,
            SuccessfulSampleCount = outcomes.Count > 0 ? successCount : null,
            FailedSampleCount = outcomes.Count > 0 ? failureCount : null,

            MinDurationMs = durations.Count > 0 ? durations[0] : null,
            MaxDurationMs = durations.Count > 0 ? durations[^1] : null,
            AverageDurationMs = durations.Count > 0 ? durations.Average() : null,
            P50DurationMs = Percentile(durations, 50),
            P95DurationMs = Percentile(durations, 95),
            P99DurationMs = Percentile(durations, 99),

            FirstObservedAt = firstObservedAt,
            LastObservedAt = lastObservedAt,
            ObservationWindowSeconds = windowSeconds > 0 ? windowSeconds : null,

            RequestsPerSecond = throughput,

            // Only meaningful when outcomes were actually recorded.
            ErrorRate = outcomes.Count > 0 ? (double)failureCount / outcomes.Count : null,

            EvidenceSources = evidence.Select(e => e.Source).Distinct().OrderBy(s => s).ToList(),
            EvidenceTypes = evidence.Select(e => e.EvidenceType).Distinct().OrderBy(t => t).ToList()
        };
    }

    /// <summary>
    /// Nearest-rank percentile on the ascending-sorted sample set: the value at 1-based rank
    /// ceil(p/100 * n). Chosen because it is deterministic, returns an actually observed value,
    /// and needs no interpolation choice that could drift between releases. Changing this
    /// definition would silently change every historical comparison, so it is pinned by tests.
    /// </summary>
    public static double? Percentile(IReadOnlyList<double> ascendingSorted, int percentile)
    {
        if (ascendingSorted.Count == 0)
            return null;

        var rank = (int)Math.Ceiling(percentile / 100.0 * ascendingSorted.Count);
        var index = Math.Clamp(rank - 1, 0, ascendingSorted.Count - 1);

        return ascendingSorted[index];
    }

    /// <summary>
    /// Compares current metrics against a previous snapshot's recorded performance.
    ///
    /// Direction is applied conservatively. Latency and error rate have an agreed direction, so a
    /// rise is a regression. Throughput does not: more traffic is not inherently better or worse
    /// without a target, so it is reported as Changed rather than judged.
    /// </summary>
    public IReadOnlyList<PerformanceChange> Compare(
        IntegrationPerformanceMetrics? current,
        IntegrationPerformanceSummary? previous)
    {
        if (previous is null || current is null)
            return [];

        var changes = new List<PerformanceChange>();

        AddChange(changes, "p50 latency", previous.P50DurationMs, current.P50DurationMs, lowerIsBetter: true);
        AddChange(changes, "p95 latency", previous.P95DurationMs, current.P95DurationMs, lowerIsBetter: true);
        AddChange(changes, "p99 latency", previous.P99DurationMs, current.P99DurationMs, lowerIsBetter: true);
        AddChange(changes, "average latency", previous.AverageDurationMs, current.AverageDurationMs, lowerIsBetter: true);
        AddChange(changes, "error rate", previous.ErrorRate, current.ErrorRate, lowerIsBetter: true);
        AddChange(changes, "throughput", previous.RequestsPerSecond, current.RequestsPerSecond, lowerIsBetter: null);

        return changes;
    }

    private static void AddChange(
        List<PerformanceChange> changes,
        string metric,
        double? previousValue,
        double? currentValue,
        bool? lowerIsBetter)
    {
        // Nothing to say when either side was never measured. "Unavailable" is not a regression.
        if (previousValue is null && currentValue is null)
            return;

        if (previousValue is null || currentValue is null)
        {
            changes.Add(new PerformanceChange
            {
                Metric = metric,
                PreviousValue = previousValue,
                CurrentValue = currentValue,
                ChangeState = PerformanceChangeState.NoComparableBaseline
            });
            return;
        }

        var absolute = currentValue.Value - previousValue.Value;

        // Percentage is undefined against a zero baseline, so it stays null rather than infinite.
        double? percentage = previousValue.Value != 0
            ? absolute / previousValue.Value * 100.0
            : null;

        var state = absolute == 0
            ? PerformanceChangeState.Unchanged
            : lowerIsBetter switch
            {
                true => absolute > 0 ? PerformanceChangeState.Regressed : PerformanceChangeState.Improved,
                false => absolute > 0 ? PerformanceChangeState.Improved : PerformanceChangeState.Regressed,
                null => PerformanceChangeState.Changed
            };

        changes.Add(new PerformanceChange
        {
            Metric = metric,
            PreviousValue = previousValue,
            CurrentValue = currentValue,
            AbsoluteChange = absolute,
            PercentageChange = percentage,
            ChangeState = state
        });
    }

    /// <summary>Reduces metrics to the summary retained in a snapshot for future comparison.</summary>
    public static IntegrationPerformanceSummary? ToSummary(IntegrationPerformanceMetrics? metrics)
    {
        if (metrics is null || metrics.EvidenceState == PerformanceEvidenceState.Unavailable)
            return null;

        return new IntegrationPerformanceSummary
        {
            SampleCount = metrics.SampleCount,
            MinDurationMs = metrics.MinDurationMs,
            MaxDurationMs = metrics.MaxDurationMs,
            AverageDurationMs = metrics.AverageDurationMs,
            P50DurationMs = metrics.P50DurationMs,
            P95DurationMs = metrics.P95DurationMs,
            P99DurationMs = metrics.P99DurationMs,
            ErrorRate = metrics.ErrorRate,
            RequestsPerSecond = metrics.RequestsPerSecond,
            FirstObservedAt = metrics.FirstObservedAt,
            LastObservedAt = metrics.LastObservedAt,
            EvidenceState = metrics.EvidenceState
        };
    }
}

/// <summary>
/// Whether performance could be measured at all. Deliberately not a pass/fail: performance here
/// is evidence, not conformance.
/// </summary>
public enum PerformanceEvidenceState
{
    Unavailable = 0,
    InsufficientSamples = 1,
    Observed = 2,
    Partial = 3,
    Unsupported = 4
}

public sealed class IntegrationPerformanceMetrics
{
    [JsonPropertyName("evidenceState")]
    public PerformanceEvidenceState EvidenceState { get; init; } = PerformanceEvidenceState.Unavailable;

    [JsonPropertyName("sampleCount")]
    public int SampleCount { get; init; }

    /// <summary>Samples that actually carried a measured duration; latency derives only from these.</summary>
    [JsonPropertyName("timedSampleCount")]
    public int TimedSampleCount { get; init; }

    [JsonPropertyName("successfulSampleCount")]
    public int? SuccessfulSampleCount { get; init; }

    [JsonPropertyName("failedSampleCount")]
    public int? FailedSampleCount { get; init; }

    [JsonPropertyName("minDurationMs")]     public double? MinDurationMs { get; init; }
    [JsonPropertyName("maxDurationMs")]     public double? MaxDurationMs { get; init; }
    [JsonPropertyName("averageDurationMs")] public double? AverageDurationMs { get; init; }
    [JsonPropertyName("p50DurationMs")]     public double? P50DurationMs { get; init; }
    [JsonPropertyName("p95DurationMs")]     public double? P95DurationMs { get; init; }
    [JsonPropertyName("p99DurationMs")]     public double? P99DurationMs { get; init; }

    [JsonPropertyName("firstObservedAt")] public DateTime? FirstObservedAt { get; init; }
    [JsonPropertyName("lastObservedAt")]  public DateTime? LastObservedAt { get; init; }

    [JsonPropertyName("observationWindowSeconds")]
    public double? ObservationWindowSeconds { get; init; }

    [JsonPropertyName("requestsPerSecond")]
    public double? RequestsPerSecond { get; init; }

    [JsonPropertyName("errorRate")]
    public double? ErrorRate { get; init; }

    [JsonPropertyName("evidenceSources")]
    public List<RuntimeEvidenceSource> EvidenceSources { get; init; } = [];

    [JsonPropertyName("evidenceTypes")]
    public List<RuntimeEvidenceType> EvidenceTypes { get; init; } = [];
}

public enum PerformanceChangeState
{
    NoComparableBaseline = 0,
    Improved = 1,
    Regressed = 2,
    Unchanged = 3,
    Changed = 4,
    Unavailable = 5
}

public sealed class PerformanceChange
{
    [JsonPropertyName("metric")]           public string Metric { get; init; } = "";
    [JsonPropertyName("previousValue")]    public double? PreviousValue { get; init; }
    [JsonPropertyName("currentValue")]     public double? CurrentValue { get; init; }
    [JsonPropertyName("absoluteChange")]   public double? AbsoluteChange { get; init; }
    [JsonPropertyName("percentageChange")] public double? PercentageChange { get; init; }
    [JsonPropertyName("changeState")]      public PerformanceChangeState ChangeState { get; init; }
}
