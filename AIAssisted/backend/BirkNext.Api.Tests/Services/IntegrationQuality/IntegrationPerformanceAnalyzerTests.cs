using BirkNext.Api.Services.IntegrationQuality;
using Xunit;

namespace BirkNext.Api.Tests.Services.IntegrationQuality;

/// <summary>
/// Phase 3 Checkpoint 6: performance metrics derived from observed runtime evidence.
///
/// The central rule under test is that a metric exists only when something was actually
/// measured. Several tests assert null rather than zero, because a zero would read as a
/// measurement of zero rather than as an absent measurement.
/// </summary>
public class IntegrationPerformanceAnalyzerTests
{
    private readonly IntegrationPerformanceAnalyzer _analyzer = new();
    private static readonly DateTime T0 = new(2026, 9, 17, 9, 30, 0, DateTimeKind.Utc);

    private static RuntimeIntegrationEvidence Sample(
        double? durationMs,
        int secondsOffset = 0,
        RuntimeEvidenceOutcome outcome = RuntimeEvidenceOutcome.Success,
        RuntimeEvidenceType type = RuntimeEvidenceType.HttpRequestObserved,
        RuntimeEvidenceSource source = RuntimeEvidenceSource.EndpointDiscovery) =>
        new()
        {
            IntegrationId = "i1",
            EvidenceType = type,
            Source = source,
            Direction = RuntimeEvidenceDirection.Outbound,
            Outcome = outcome,
            ObservedAt = T0.AddSeconds(secondsOffset),
            DurationMs = durationMs
        };

    // ── Absence of evidence ──────────────────────────────────────────────────

    [Fact]
    public void NoEvidence_IsUnavailable()
    {
        var metrics = _analyzer.Analyze([]);

        Assert.Equal(PerformanceEvidenceState.Unavailable, metrics.EvidenceState);
        Assert.Equal(0, metrics.SampleCount);
    }

    [Fact]
    public void NullEvidence_IsUnavailable() =>
        Assert.Equal(PerformanceEvidenceState.Unavailable, _analyzer.Analyze(null).EvidenceState);

    [Fact]
    public void NoEvidence_ReportsNullNotZeroLatency()
    {
        var metrics = _analyzer.Analyze([]);

        // A zero here would read as "measured 0 ms" rather than "never measured".
        Assert.Null(metrics.MinDurationMs);
        Assert.Null(metrics.MaxDurationMs);
        Assert.Null(metrics.AverageDurationMs);
        Assert.Null(metrics.P50DurationMs);
        Assert.Null(metrics.P95DurationMs);
        Assert.Null(metrics.P99DurationMs);
    }

    [Fact]
    public void NoEvidence_ReportsNullThroughputAndErrorRate()
    {
        var metrics = _analyzer.Analyze([]);

        Assert.Null(metrics.RequestsPerSecond);
        Assert.Null(metrics.ErrorRate);
        Assert.Null(metrics.ObservationWindowSeconds);
    }

    [Fact]
    public void EvidenceWithoutDurations_ProducesNoLatency()
    {
        var metrics = _analyzer.Analyze([Sample(null), Sample(null, 1)]);

        Assert.Equal(PerformanceEvidenceState.Unavailable, metrics.EvidenceState);
        Assert.Equal(2, metrics.SampleCount);
        Assert.Equal(0, metrics.TimedSampleCount);
        Assert.Null(metrics.AverageDurationMs);
        Assert.Null(metrics.P95DurationMs);
    }

    // ── Sample-count semantics ───────────────────────────────────────────────

    [Fact]
    public void SingleSample_ReportsBasicStatsButFlagsInsufficientEvidence()
    {
        var metrics = _analyzer.Analyze([Sample(100)]);

        Assert.Equal(PerformanceEvidenceState.InsufficientSamples, metrics.EvidenceState);
        Assert.Equal(100, metrics.MinDurationMs);
        Assert.Equal(100, metrics.MaxDurationMs);
        Assert.Equal(100, metrics.AverageDurationMs);
    }

    [Fact]
    public void FourSamples_RemainInsufficientForDistribution()
    {
        var metrics = _analyzer.Analyze([Sample(10), Sample(20, 1), Sample(30, 2), Sample(40, 3)]);

        Assert.Equal(PerformanceEvidenceState.InsufficientSamples, metrics.EvidenceState);
    }

    [Fact]
    public void FiveSamples_AreObserved()
    {
        var metrics = _analyzer.Analyze(
            [Sample(10), Sample(20, 1), Sample(30, 2), Sample(40, 3), Sample(50, 4)]);

        Assert.Equal(PerformanceEvidenceState.Observed, metrics.EvidenceState);
        Assert.Equal(IntegrationPerformanceAnalyzer.MinimumSamplesForDistribution, 5);
    }

    // ── Deterministic statistics ─────────────────────────────────────────────

    [Fact]
    public void MinMaxAndAverage_AreDeterministic()
    {
        var metrics = _analyzer.Analyze(
            [Sample(50), Sample(10, 1), Sample(30, 2), Sample(20, 3), Sample(40, 4)]);

        Assert.Equal(10, metrics.MinDurationMs);
        Assert.Equal(50, metrics.MaxDurationMs);
        Assert.Equal(30, metrics.AverageDurationMs);
    }

    [Theory]
    // Nearest-rank on 1..10: rank = ceil(p/100 * 10).
    [InlineData(50, 5)]
    [InlineData(95, 10)]
    [InlineData(99, 10)]
    public void Percentile_UsesNearestRank(int percentile, double expected)
    {
        var sorted = Enumerable.Range(1, 10).Select(i => (double)i).ToList();
        Assert.Equal(expected, IntegrationPerformanceAnalyzer.Percentile(sorted, percentile));
    }

    [Fact]
    public void Percentile_ReturnsAnObservedValue()
    {
        // Nearest-rank never interpolates, so every reported percentile is a real sample.
        var sorted = new List<double> { 10, 20, 30, 40, 100 };

        Assert.Contains(IntegrationPerformanceAnalyzer.Percentile(sorted, 95)!.Value, sorted);
        Assert.Contains(IntegrationPerformanceAnalyzer.Percentile(sorted, 50)!.Value, sorted);
    }

    [Fact]
    public void Percentile_OnEmptySet_IsNull() =>
        Assert.Null(IntegrationPerformanceAnalyzer.Percentile([], 95));

    [Fact]
    public void Percentiles_AreOrdered()
    {
        var metrics = _analyzer.Analyze(
            Enumerable.Range(1, 100).Select(i => Sample(i, i)).ToList());

        Assert.True(metrics.P50DurationMs <= metrics.P95DurationMs);
        Assert.True(metrics.P95DurationMs <= metrics.P99DurationMs);
    }

    // ── Outcomes and error rate ──────────────────────────────────────────────

    [Fact]
    public void SuccessAndFailureCounts_AreTracked()
    {
        var metrics = _analyzer.Analyze(
        [
            Sample(10), Sample(20, 1),
            Sample(30, 2, RuntimeEvidenceOutcome.Error)
        ]);

        Assert.Equal(2, metrics.SuccessfulSampleCount);
        Assert.Equal(1, metrics.FailedSampleCount);
    }

    [Fact]
    public void ErrorRate_IsFailuresOverRecordedOutcomes()
    {
        var metrics = _analyzer.Analyze(
        [
            Sample(10), Sample(20, 1), Sample(30, 2), Sample(40, 3),
            Sample(50, 4, RuntimeEvidenceOutcome.Error)
        ]);

        Assert.Equal(0.2, metrics.ErrorRate);
    }

    [Fact]
    public void UnknownOutcomes_ProduceNoErrorRate()
    {
        var metrics = _analyzer.Analyze(
        [
            Sample(10, 0, RuntimeEvidenceOutcome.Unknown),
            Sample(20, 1, RuntimeEvidenceOutcome.Unknown)
        ]);

        // No outcome was recorded, so an error rate of 0 would be an invention.
        Assert.Null(metrics.ErrorRate);
        Assert.Null(metrics.SuccessfulSampleCount);
        Assert.Null(metrics.FailedSampleCount);
    }

    // ── Observation window and throughput ────────────────────────────────────

    [Fact]
    public void ObservationWindow_IsSpanBetweenFirstAndLast()
    {
        var metrics = _analyzer.Analyze([Sample(10), Sample(20, 60)]);

        Assert.Equal(T0, metrics.FirstObservedAt);
        Assert.Equal(T0.AddSeconds(60), metrics.LastObservedAt);
        Assert.Equal(60, metrics.ObservationWindowSeconds);
    }

    [Fact]
    public void Throughput_IsSamplesOverWindow()
    {
        var metrics = _analyzer.Analyze([Sample(10), Sample(20, 5), Sample(30, 10)]);

        Assert.Equal(0.3, metrics.RequestsPerSecond);
    }

    [Fact]
    public void Throughput_IsNullWhenAllSamplesShareOneInstant()
    {
        // Without an elapsed window, "3 samples" must not become "3 per second".
        var metrics = _analyzer.Analyze([Sample(10), Sample(20), Sample(30)]);

        Assert.Null(metrics.RequestsPerSecond);
        Assert.Null(metrics.ObservationWindowSeconds);
    }

    [Fact]
    public void Throughput_IsNotInferredFromCountAlone()
    {
        var metrics = _analyzer.Analyze([Sample(10)]);
        Assert.Null(metrics.RequestsPerSecond);
    }

    // ── Provenance ───────────────────────────────────────────────────────────

    [Fact]
    public void EvidenceSourcesAndTypes_ArePreserved()
    {
        var metrics = _analyzer.Analyze(
        [
            Sample(10),
            Sample(20, 1, type: RuntimeEvidenceType.GraphQlOperationObserved,
                   source: RuntimeEvidenceSource.AuthenticatedProxy)
        ]);

        Assert.Contains(RuntimeEvidenceSource.EndpointDiscovery, metrics.EvidenceSources);
        Assert.Contains(RuntimeEvidenceSource.AuthenticatedProxy, metrics.EvidenceSources);
        Assert.Contains(RuntimeEvidenceType.HttpRequestObserved, metrics.EvidenceTypes);
        Assert.Contains(RuntimeEvidenceType.GraphQlOperationObserved, metrics.EvidenceTypes);
    }

    [Fact]
    public void MixedEvidenceTypes_AreAggregatedTogether()
    {
        var metrics = _analyzer.Analyze(
        [
            Sample(10),
            Sample(30, 1, type: RuntimeEvidenceType.GraphQlOperationObserved)
        ]);

        Assert.Equal(2, metrics.TimedSampleCount);
        Assert.Equal(20, metrics.AverageDurationMs);
    }

    // ── Historical comparison ────────────────────────────────────────────────

    private static IntegrationPerformanceSummary Summary(
        double? p95 = 100, double? errorRate = 0.1, double? throughput = 5) =>
        new()
        {
            SampleCount = 50,
            P95DurationMs = p95,
            ErrorRate = errorRate,
            RequestsPerSecond = throughput,
            EvidenceState = PerformanceEvidenceState.Observed
        };

    private IntegrationPerformanceMetrics Metrics(double p95, double errorRate = 0.1, int count = 10)
    {
        var samples = Enumerable.Range(0, count)
            .Select(i => Sample(p95, i))
            .ToList();

        var baseline = _analyzer.Analyze(samples);
        return baseline;
    }

    [Fact]
    public void NoPreviousSummary_YieldsNoComparison() =>
        Assert.Empty(_analyzer.Compare(Metrics(100), null));

    [Fact]
    public void LatencyIncrease_IsRegression()
    {
        var change = _analyzer.Compare(Metrics(150), Summary(p95: 100))
            .Single(c => c.Metric == "p95 latency");

        Assert.Equal(PerformanceChangeState.Regressed, change.ChangeState);
        Assert.Equal(50, change.AbsoluteChange);
        Assert.Equal(50, change.PercentageChange);
    }

    [Fact]
    public void LatencyDecrease_IsImprovement()
    {
        var change = _analyzer.Compare(Metrics(50), Summary(p95: 100))
            .Single(c => c.Metric == "p95 latency");

        Assert.Equal(PerformanceChangeState.Improved, change.ChangeState);
    }

    [Fact]
    public void UnchangedLatency_IsUnchanged()
    {
        var change = _analyzer.Compare(Metrics(100), Summary(p95: 100))
            .Single(c => c.Metric == "p95 latency");

        Assert.Equal(PerformanceChangeState.Unchanged, change.ChangeState);
        Assert.Equal(0, change.AbsoluteChange);
    }

    [Fact]
    public void ThroughputChange_IsNotJudged()
    {
        // More traffic is not inherently better or worse without a target.
        var change = _analyzer.Compare(Metrics(100), Summary(throughput: 1))
            .SingleOrDefault(c => c.Metric == "throughput");

        if (change is not null)
            Assert.Equal(PerformanceChangeState.Changed, change.ChangeState);
    }

    [Fact]
    public void ZeroPreviousValue_YieldsNullPercentage()
    {
        var change = _analyzer.Compare(Metrics(100, errorRate: 0), Summary(p95: 0))
            .Single(c => c.Metric == "p95 latency");

        // Division by a zero baseline is undefined, not infinite.
        Assert.Null(change.PercentageChange);
        Assert.Equal(100, change.AbsoluteChange);
    }

    [Fact]
    public void MissingPreviousMetric_IsNotARegression()
    {
        var change = _analyzer.Compare(Metrics(100), Summary(p95: null))
            .Single(c => c.Metric == "p95 latency");

        Assert.Equal(PerformanceChangeState.NoComparableBaseline, change.ChangeState);
        Assert.NotEqual(PerformanceChangeState.Regressed, change.ChangeState);
    }

    [Fact]
    public void BothSidesUnavailable_ProduceNoChangeEntry()
    {
        var changes = _analyzer.Compare(_analyzer.Analyze([]), Summary(p95: null, errorRate: null, throughput: null));

        Assert.DoesNotContain(changes, c => c.Metric == "p95 latency");
    }

    // ── Snapshot summary ─────────────────────────────────────────────────────

    [Fact]
    public void UnavailableMetrics_ProduceNoSnapshotSummary() =>
        Assert.Null(IntegrationPerformanceAnalyzer.ToSummary(_analyzer.Analyze([])));

    [Fact]
    public void SnapshotSummary_CarriesEvidenceState()
    {
        var summary = IntegrationPerformanceAnalyzer.ToSummary(_analyzer.Analyze([Sample(100)]));

        Assert.NotNull(summary);
        Assert.Equal(PerformanceEvidenceState.InsufficientSamples, summary!.EvidenceState);
    }

    [Fact]
    public void SnapshotSummary_PreservesNulls()
    {
        // Samples at one instant: no window, so no throughput to record.
        var summary = IntegrationPerformanceAnalyzer.ToSummary(
            _analyzer.Analyze([Sample(10), Sample(20)]));

        Assert.NotNull(summary);
        Assert.Null(summary!.RequestsPerSecond);
    }

    // ── Semantic guards ──────────────────────────────────────────────────────

    [Fact]
    public void ReachabilityAloneProducesNoPerformance()
    {
        // Reachability is not evidence of traffic. Only evidence records reach the analyzer, and
        // an empty set must stay Unavailable rather than becoming a zeroed measurement.
        var metrics = _analyzer.Analyze([]);

        Assert.Equal(PerformanceEvidenceState.Unavailable, metrics.EvidenceState);
        Assert.Null(metrics.P95DurationMs);
        Assert.Null(metrics.ErrorRate);
        Assert.Null(metrics.RequestsPerSecond);
    }

    [Fact]
    public void MessagingWithoutTelemetry_RemainsUnavailable()
    {
        // No messaging runtime observation source exists, so a messaging integration has no
        // evidence and must not be given fabricated numbers.
        var metrics = _analyzer.Analyze([]);
        Assert.Equal(PerformanceEvidenceState.Unavailable, metrics.EvidenceState);
        Assert.Null(IntegrationPerformanceAnalyzer.ToSummary(metrics));
    }
}
