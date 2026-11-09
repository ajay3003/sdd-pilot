using BirkNext.Applicability;
using BirkNext.PerformanceTests;
using FluentAssertions;
using Xunit;

namespace BirkNext.Api.Tests.Services.PerformanceTests;

/// <summary>
/// Comparability gate, regression verdicts and baseline eligibility (shared rules, backend-authoritative). Synthetic run documents: the numbers are
/// chosen so each verdict has one cause.
/// </summary>
public sealed class PerformanceRegressionRulesTests
{
    private static readonly PerformanceDriftPolicy P95Within20 = new() { Metric = PerformanceMetric.LatencyP95Ms, AllowedRelativeChangePercent = 20 };

    private static PerformanceTestDefinition Definition(PerformanceWorkload? workload = null, string? dataProfile = null, string? auth = null) => new()
    {
        Id = "def-1", Name = "Children API", TargetOrigin = "https://api.local.test", EnvironmentType = "Development",
        Scenario = new PerformanceScenario { Steps = [new HttpPerformanceStep { Name = "List", Method = "GET", RelativePath = "/children" }], TestDataProfileId = dataProfile },
        Workload = workload ?? new PerformanceWorkload { Mode = WorkloadMode.VirtualUsers, VirtualUsers = 2, WarmupSeconds = 5, RampUpSeconds = 5, SteadyStateSeconds = 20, RampDownSeconds = 0 },
        AuthenticationReference = auth,
    };

    private static PerformanceTestRun Run(double p95, double rps = 40, double errors = 0, long requests = 1000, PerformanceTestDefinition? definition = null, string? version = null,
        string environment = "local", double? p99 = null, long? dropped = null, PerformanceQualityVerdict verdict = PerformanceQualityVerdict.Pass, string? image = "k6@sha256:aa") => new()
    {
        EnvironmentId = environment, DefinitionId = "def-1", DefinitionVersion = 1, TargetOrigin = "https://api.local.test", EnvironmentType = "Development",
        ComparisonFingerprint = "fp", State = PerformanceRunState.Completed, DefinitionSnapshot = definition ?? Definition(), VersionLabel = version, Verdict = verdict,
        ProviderId = "performance.k6", ProviderVersion = "1.0.0", ImageDigest = image, MetricsSource = "k6 summary", CreatedAt = DateTimeOffset.UtcNow, FinishedAt = DateTimeOffset.UtcNow,
        Metrics = new PerformanceMetrics
        {
            RequestCount = requests, RequestsPerSecond = rps, ErrorRatePercent = errors, DroppedIterations = dropped,
            Latency = new PerformanceLatency { P50Ms = p95 / 2, P90Ms = p95 * .9, P95Ms = p95, P99Ms = p99 ?? p95 * 1.5, MeanMs = p95 / 2 },
        },
    };

    private static readonly PerformanceBaseline Baseline = new() { BaselineId = "b1", Version = 1, Status = PerformanceBaselineStatus.Active, Approval = BaselineApprovalState.Approved };

    private static MetricRegression M(PerformanceRegressionReport r, string key) => r.Metrics.Single(m => m.Key == key);

    // ── Comparability ───────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_same_measurement_is_comparable()
    {
        PerformanceRegressionRules.Comparability(Run(300), Run(310)).Level.Should().Be(ComparabilityLevel.Comparable);
    }

    [Theory]
    [InlineData("environment")]
    [InlineData("vus")]
    [InlineData("arrival")]
    [InlineData("dataset")]
    [InlineData("auth")]
    public void A_different_measurement_is_not_comparable_and_names_the_difference(string change)
    {
        var current = change switch
        {
            "environment" => Run(300, environment: "other"),
            "vus" => Run(300, definition: Definition(new PerformanceWorkload { VirtualUsers = 10, WarmupSeconds = 5, RampUpSeconds = 5, SteadyStateSeconds = 20, RampDownSeconds = 0 })),
            "arrival" => Run(300, definition: Definition(new PerformanceWorkload { Mode = WorkloadMode.ArrivalRate, RequestsPerSecond = 5, VirtualUsers = 2, WarmupSeconds = 5, RampUpSeconds = 5, SteadyStateSeconds = 20, RampDownSeconds = 0 })),
            "dataset" => Run(300, definition: Definition(dataProfile: "children-v2")),
            _ => Run(300, definition: Definition(auth: "api-credential")),
        };
        var c = PerformanceRegressionRules.Comparability(Run(300), current);
        c.Level.Should().Be(ComparabilityLevel.NotComparable);
        var dimension = change switch
        {
            "environment" => "Environment", "vus" => "Virtual users", "arrival" => "Workload type", "dataset" => "Test data profile", _ => "Authentication",
        };
        c.Differences.Should().Contain(d => d.Severity == ComparabilitySeverity.Blocking && d.Dimension == dimension);
    }

    [Fact]
    public void A_different_application_or_runtime_version_is_comparable_with_warnings()
    {
        var c = PerformanceRegressionRules.Comparability(Run(300, version: "1.4.0"), Run(300, version: "1.5.0", image: "k6@sha256:bb"));
        c.Level.Should().Be(ComparabilityLevel.ComparableWithWarnings);
        c.Differences.Select(d => d.Dimension).Should().BeEquivalentTo("Application version", "Runtime image");
    }

    [Fact]
    public void A_run_without_complete_metrics_is_insufficient_evidence_and_missing_metadata_is_named()
    {
        var partial = Run(300) with { MetricsPartial = true, State = PerformanceRunState.Cancelled };
        PerformanceRegressionRules.Comparability(Run(300), partial).Level.Should().Be(ComparabilityLevel.InsufficientEvidence);
        var c = PerformanceRegressionRules.Comparability(Run(300, version: "1.4.0"), Run(300, version: null));
        c.Differences.Should().ContainSingle(d => d.Dimension == "Application version" && d.Current == "not recorded");
    }

    // ── Regression ──────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_single_run_beyond_the_tolerance_is_at_most_a_potential_regression_and_thresholds_stay_separate()
    {
        var r = PerformanceRegressionRules.Evaluate(Baseline, Run(300), [Run(400)], [P95Within20]);
        M(r, "p95").Verdict.Should().Be(RegressionVerdict.PotentialRegression);
        M(r, "p95").Should().Match<MetricRegression>(m => m.Baseline == 300 && m.Current == 400 && m.AbsoluteDelta == 100 && m.RelativePercent == 33.3 && m.Tolerance == "+20 %");
        r.Verdict.Should().Be(RegressionVerdict.PotentialRegression);
        r.Thresholds.Should().ContainSingle(t => t.Verdict == PerformanceQualityVerdict.Pass, "threshold PASS and regression are both visible");
        r.Limitations.Should().Contain(l => l.Contains("at most Potential"));
    }

    [Fact]
    public void Three_agreeing_comparable_runs_confirm_a_regression()
    {
        var r = PerformanceRegressionRules.Evaluate(Baseline, Run(300), [Run(400), Run(390), Run(410)], [P95Within20]);
        M(r, "p95").Should().Match<MetricRegression>(m => m.Verdict == RegressionVerdict.ConfirmedRegression && m.Current == 400 && m.RunsBeyondTolerance == 3 && m.Aggregation.Contains("median of 3"));
        r.Verdict.Should().Be(RegressionVerdict.ConfirmedRegression);
    }

    [Fact]
    public void A_median_beyond_the_tolerance_without_agreement_stays_potential()
    {
        var r = PerformanceRegressionRules.Evaluate(Baseline, Run(300), [Run(400), Run(400), Run(400), Run(290), Run(290)], [P95Within20]);
        M(r, "p95").Should().Match<MetricRegression>(m => m.Verdict == RegressionVerdict.PotentialRegression && m.RunsBeyondTolerance == 3);
    }

    [Fact]
    public void Runs_spreading_more_than_twice_the_tolerance_are_inconclusive_noise()
    {
        var r = PerformanceRegressionRules.Evaluate(Baseline, Run(300), [Run(300), Run(450)], [P95Within20]);
        M(r, "p95").Verdict.Should().Be(RegressionVerdict.Inconclusive);
        M(r, "p95").Reason.Should().Contain("noise");
    }

    [Fact]
    public void A_failed_threshold_with_a_stable_comparison_is_reported_as_both()
    {
        var r = PerformanceRegressionRules.Evaluate(Baseline, Run(300), [Run(305, verdict: PerformanceQualityVerdict.Fail) with
            { ThresholdResults = [new PerformanceThresholdResult { Outcome = CheckOutcome.Fail }] }], [P95Within20]);
        r.Verdict.Should().Be(RegressionVerdict.Stable);
        r.Thresholds.Should().ContainSingle(t => t.Verdict == PerformanceQualityVerdict.Fail && t.Failed == 1);
    }

    [Fact]
    public void A_zero_baseline_needs_an_absolute_tolerance()
    {
        var relative = new PerformanceDriftPolicy { Metric = PerformanceMetric.ErrorRatePercent, AllowedRelativeChangePercent = 50 };
        M(PerformanceRegressionRules.Evaluate(Baseline, Run(300, errors: 0), [Run(300, errors: 2)], [relative]), "errorRate").Verdict.Should().Be(RegressionVerdict.Inconclusive);
        var absolute = new PerformanceDriftPolicy { Metric = PerformanceMetric.ErrorRatePercent, AllowedAbsoluteChange = 1 };
        var judged = M(PerformanceRegressionRules.Evaluate(Baseline, Run(300, errors: 0), [Run(300, errors: 2)], [absolute]), "errorRate");
        judged.Should().Match<MetricRegression>(m => m.Verdict == RegressionVerdict.PotentialRegression && m.RelativePercent == null && m.Tolerance == "+1 % (points)");
    }

    [Fact]
    public void A_missing_metric_and_too_few_samples_are_inconclusive_not_zero()
    {
        var p99 = new PerformanceDriftPolicy { Metric = PerformanceMetric.LatencyP99Ms, AllowedRelativeChangePercent = 20 };
        var noP99 = Run(300) with { Metrics = Run(300).Metrics! with { Latency = Run(300).Metrics!.Latency with { P99Ms = null } } };
        M(PerformanceRegressionRules.Evaluate(Baseline, Run(300), [noP99], [p99]), "p99").Should().Match<MetricRegression>(m => m.Verdict == RegressionVerdict.Inconclusive && m.Current == null);

        var few = PerformanceRegressionRules.Evaluate(Baseline, Run(300, requests: 50), [Run(400, requests: 50)], [p99, P95Within20]);
        M(few, "p99").Reason.Should().Contain("at least 100");
        M(few, "p95").Verdict.Should().Be(RegressionVerdict.PotentialRegression, "50 requests are enough for P95");
    }

    [Fact]
    public void Without_a_tolerance_changes_are_shown_not_judged()
    {
        var r = PerformanceRegressionRules.Evaluate(Baseline, Run(300), [Run(600)], []);
        M(r, "p95").Should().Match<MetricRegression>(m => m.Verdict == RegressionVerdict.NotAssessed && m.Direction == MetricChangeDirection.Worse && m.Tolerance == null);
        r.Verdict.Should().Be(RegressionVerdict.NotAssessed);
    }

    [Fact]
    public void Throughput_is_judged_under_fixed_users_and_not_under_an_arrival_rate()
    {
        var tps = new PerformanceDriftPolicy { Metric = PerformanceMetric.ThroughputRps, AllowedRelativeChangePercent = 10 };
        M(PerformanceRegressionRules.Evaluate(Baseline, Run(300, rps: 40), [Run(300, rps: 50)], [tps]), "throughput").Verdict.Should().Be(RegressionVerdict.Improved);
        M(PerformanceRegressionRules.Evaluate(Baseline, Run(300, rps: 40), [Run(300, rps: 30)], [tps]), "throughput").Verdict.Should().Be(RegressionVerdict.PotentialRegression);
        var arrival = Definition(new PerformanceWorkload { Mode = WorkloadMode.ArrivalRate, RequestsPerSecond = 5, VirtualUsers = 2, WarmupSeconds = 5, RampUpSeconds = 5, SteadyStateSeconds = 20, RampDownSeconds = 0 });
        M(PerformanceRegressionRules.Evaluate(Baseline, Run(300, rps: 5, definition: arrival), [Run(300, rps: 3, definition: arrival)], [tps]), "throughput").Verdict.Should().Be(RegressionVerdict.NotAssessed);
    }

    [Fact]
    public void Not_comparable_runs_get_exploratory_deltas_and_no_verdict()
    {
        var r = PerformanceRegressionRules.Evaluate(Baseline, Run(300), [Run(900, environment: "other")], [P95Within20]);
        r.Exploratory.Should().BeTrue();
        r.Verdict.Should().Be(RegressionVerdict.Inconclusive);
        r.Metrics.Should().OnlyContain(m => m.Verdict == RegressionVerdict.Inconclusive);
        M(r, "p95").AbsoluteDelta.Should().Be(600, "the difference is still shown, labelled exploratory");
    }

    [Fact]
    public void GraphQL_error_rate_and_dropped_iteration_rate_are_derived_from_counts_only_when_reported()
    {
        var withCounts = Run(300, dropped: 10) with { Metrics = Run(300, dropped: 10).Metrics! with { GraphQlErrors = 25 } };
        PerformanceRegressionRules.Value(withCounts.Metrics, "graphqlErrorRate").Should().Be(2.5);
        PerformanceRegressionRules.Value(withCounts.Metrics, "droppedIterationRate").Should().BeApproximately(0.99, 0.01);
        PerformanceRegressionRules.Value(Run(300).Metrics, "graphqlErrorRate").Should().BeNull("not reported is never 0");
    }

    [Fact]
    public void A_clean_GraphQL_baseline_anchors_the_GraphQL_error_rate_and_HTTP_success_does_not_hide_the_errors()
    {
        // Found by the live k6 acceptance: k6 omits a counter that never received a sample, so a clean GraphQL run stores no GraphQL-error count.
        var graphQl = Definition() with { TargetType = PerformanceTargetType.GraphQlHttp };
        PerformanceTestRun G(long? errors) => Run(300, definition: graphQl) with
            { Metrics = Run(300).Metrics! with { GraphQlErrors = errors, StatusCodes = new() { ["200"] = 1000 }, ErrorRatePercent = 0 } };
        PerformanceRegressionRules.Value(G(null), "graphqlErrorRate").Should().Be(0, "the script counted, and no GraphQL error occurred");
        PerformanceRegressionRules.Value(Run(300), "graphqlErrorRate").Should().BeNull("a REST run never measures GraphQL errors");
        var legacy = G(null) with { Metrics = G(null).Metrics! with { StatusCodes = null } };
        PerformanceRegressionRules.Value(legacy, "graphqlErrorRate").Should().BeNull("an older script did not count at all");

        var r = PerformanceRegressionRules.Evaluate(Baseline, G(null), [G(1000)], [P95Within20]);
        var m = M(r, "graphqlErrorRate");
        m.Should().Match<MetricRegression>(x => x.Baseline == 0 && x.Current == 100 && x.Direction == MetricChangeDirection.Worse);
        M(r, "errorRate").Current.Should().Be(0, "every response was HTTP 200");
    }

    // ── Eligibility and trends ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Eligibility_blocks_incomplete_runs_and_asks_explicit_acceptance_for_warnings()
    {
        var partial = Run(300) with { State = PerformanceRunState.Cancelled, MetricsPartial = true };
        PerformanceRegressionRules.EligibilityProblem(PerformanceRegressionRules.Eligibility(partial), acceptWarnings: true).Should().StartWith("Not eligible");
        var dropped = PerformanceRegressionRules.Eligibility(Run(300, dropped: 5, requests: 10));
        PerformanceRegressionRules.EligibilityProblem(dropped, false).Should().StartWith("Needs explicit acceptance").And.Contain("not started").And.Contain("P95");
        PerformanceRegressionRules.EligibilityProblem(dropped, true).Should().BeNull();
        PerformanceRegressionRules.EligibilityProblem(PerformanceRegressionRules.Eligibility(Run(300)), false).Should().BeNull();
    }

    [Fact]
    public void A_trend_marks_the_baseline_run_and_keeps_incomparable_points_off_the_line()
    {
        var baselineRun = Run(300);
        var trend = PerformanceRegressionRules.Trend("def-1", Baseline with { RunId = baselineRun.RunId }, baselineRun,
            [baselineRun, Run(320, version: "1.5"), Run(300, environment: "other") with { EnvironmentId = "local" }, Run(310, definition: Definition(dataProfile: "x"))]);
        trend.Points.Should().HaveCount(4);
        trend.Points.Single(p => p.IsBaselineRun).Comparability.Should().Be(ComparabilityLevel.Comparable);
        trend.Points.Count(p => p.Comparability == ComparabilityLevel.NotComparable).Should().Be(1);
        trend.ExcludedPoints.Should().Be(1);
        trend.Points[1].Values["p95"].Should().Be(320);
    }
}
