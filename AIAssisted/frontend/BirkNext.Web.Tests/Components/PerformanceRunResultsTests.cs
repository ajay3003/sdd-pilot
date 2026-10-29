using BirkNext.Applicability;
using BirkNext.PerformanceTests;
using BirkNext.Web.Components;
using Bunit;
using FluentAssertions;
using P = BirkNext.Web.Services.PerformanceTestPresentation;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// Results analyses ONE run from its own immutable evidence: one primary status (execution, then thresholds — never merged), required vs advisory
/// thresholds, status codes (never zero when not captured), per-operation rows from the run's snapshot, a compact baseline snapshot that is the
/// run's stored comparison (the same deltas Compare shows), explicit "not retained" response evidence and the configuration the run used.
/// </summary>
public sealed class PerformanceRunResultsTests : BunitContext
{
    private PerformanceTestRun? _compared;
    private PerformanceTestRun? _history;

    public PerformanceRunResultsTests() => JSInterop.Mode = JSRuntimeMode.Loose;

    private static readonly PerformanceTestDefinition Snapshot = new()
    {
        Id = "def-1", Name = "Baseline check", Version = 2, EnvironmentName = "Development", TargetOrigin = "https://app.example.test",
        Scenario = new PerformanceScenario
        {
            Name = "Barn lookup",
            Steps = [new HttpPerformanceStep { Name = "list", Method = "GET", RelativePath = "/api/brukere" }, new HttpPerformanceStep { Name = "barn", Method = "GET", RelativePath = "/api/brukere/{brukerId}/barn" }],
        },
        Workload = new PerformanceWorkload { VirtualUsers = 5 },
        Thresholds = [new PerformanceThreshold(), new PerformanceThreshold(), new PerformanceThreshold()],
    };

    private static PerformanceThresholdResult Threshold(PerformanceMetric metric, CheckOutcome outcome, ThresholdSeverity severity, double expected, double measured) =>
        new() { Metric = metric, Operator = ThresholdOperator.LessThan, Expected = expected, Measured = measured, Outcome = outcome, Severity = severity };

    private static PerformanceTestRun Run(PerformanceRunState state = PerformanceRunState.Completed, PerformanceQualityVerdict verdict = PerformanceQualityVerdict.Fail) => new()
    {
        RunId = Guid.Parse("11111111-2222-3333-4444-555555555555"), DefinitionId = "def-1", DefinitionVersion = 2, DefinitionSnapshot = Snapshot, State = state, Verdict = verdict,
        TargetOrigin = Snapshot.TargetOrigin, StartedAt = new DateTimeOffset(2026, 10, 28, 9, 0, 0, TimeSpan.Zero), ProviderVersion = "1.0.0",
        Metrics = new PerformanceMetrics
        {
            RequestCount = 1240, SuccessfulRequests = 1230, FailedRequests = 10, ErrorRatePercent = 0.8, RequestsPerSecond = 46, DurationSeconds = 200,
            Latency = new PerformanceLatency { P50Ms = 120, P90Ms = 300, P95Ms = 420, P99Ms = 690, MinMs = 20, MaxMs = 900, MeanMs = 150 },
            Steps = [new PerformanceStepMetrics("list", 620, 0, 200, 300, 23), new PerformanceStepMetrics("barn", 620, 1.6, 420, 690, 23)],
            StatusCodes = new() { ["200"] = 1230, ["429"] = 7, ["500"] = 3 }, TransportErrors = new() { ["timeout"] = 2 },
        },
        ThresholdResults =
        [
            Threshold(PerformanceMetric.LatencyP95Ms, CheckOutcome.Pass, ThresholdSeverity.Required, 500, 420),
            Threshold(PerformanceMetric.ErrorRatePercent, CheckOutcome.Fail, ThresholdSeverity.Required, 0.5, 0.8),
            Threshold(PerformanceMetric.LatencyP99Ms, CheckOutcome.Fail, ThresholdSeverity.Advisory, 600, 690),
        ],
    };

    private IRenderedComponent<PerformanceRunResults> Render(PerformanceTestRun? run, PerformanceBaseline? active = null, PerformanceTestDefinition? current = null) =>
        Render<PerformanceRunResults>(p => p.Add(x => x.Run, run).Add(x => x.ActiveBaseline, active).Add(x => x.Current, current ?? Snapshot)
            .Add(x => x.OnOpenComparison, (PerformanceTestRun r) => _compared = r).Add(x => x.OnOpenHistory, (PerformanceTestRun? r) => _history = r));

    [Fact]
    public void Completed_run_has_one_primary_status_and_required_vs_advisory_thresholds()
    {
        var cut = Render(Run());
        cut.Find("[data-testid=pt-result-status]").TextContent.Should().Contain("Run completed").And.Contain("Required thresholds failed");
        cut.Find("[data-testid=pt-result-summary]").ClassList.Should().Contain("prr-tone-error");
        cut.Find("[data-testid=pt-result-threshold-summary]").TextContent.Should().Be("1 passed · 1 failed · 1 advisory failed");
        cut.Find("[data-testid=pt-result-percentiles]").TextContent.Should().Be("120 ms · 300 ms · 420 ms · 690 ms");
        cut.Find("[data-testid=pt-result-failed]").TextContent.Should().Be("10 (0.8 %)");
        var advisory = cut.Find("[data-testid=pt-threshold-result][data-severity=Advisory]");
        advisory.TextContent.Should().Contain("Advisory").And.Contain("Advisory failed (warning)");
        advisory.QuerySelector(".sd-pill")!.ClassList.Should().Contain("sd-pill-partial", "an advisory failure is not shown like a required failure");
    }

    [Theory]
    [InlineData(PerformanceRunState.Cancelled, "Cancelled", "Partial results — no pass/fail conclusion")]
    [InlineData(PerformanceRunState.ExecutionFailed, "Run failed", "Execution error — not a performance result")]
    [InlineData(PerformanceRunState.Running, "Run in progress", "Partial metrics may change")]
    public void Execution_states_are_never_a_performance_verdict(PerformanceRunState state, string primary, string secondary)
    {
        var cut = Render(Run(state));
        cut.Find("[data-testid=pt-result-status]").TextContent.Should().Contain(primary).And.Contain(secondary).And.NotContain("Thresholds");
    }

    [Fact]
    public void Status_codes_and_transport_failures_are_grouped_and_missing_capture_is_not_zero()
    {
        var cut = Render(Run());
        cut.FindAll("[data-testid=pt-result-status-code]").Select(r => r.GetAttribute("data-code")).Should().Equal("200", "429", "500");
        cut.Find("[data-testid=pt-result-transport][data-kind=timeout]").TextContent.Should().Contain("Timeout (no response)").And.Contain("2");

        var old = Render(Run() with { Metrics = Run().Metrics! with { StatusCodes = null, TransportErrors = null } });
        old.Find("[data-testid=pt-result-status-codes-none]").TextContent.Should().Be("Status distribution not captured by this run.");
        old.FindAll("[data-testid=pt-result-status-code]").Should().BeEmpty();
    }

    [Fact]
    public void Operations_come_from_the_runs_snapshot_failures_first()
    {
        var cut = Render(Run());
        var ops = cut.FindAll("[data-testid=pt-result-op]");
        ops.Select(o => o.GetAttribute("data-step")).Should().Equal("barn", "list");
        ops[0].TextContent.Should().Contain("GET /api/brukere/{brukerId}/barn").And.Contain("620").And.Contain("1.6 %").And.Contain("420 ms").And.Contain("690 ms");
        ops[0].QuerySelector("[data-testid=pt-result-op-details]")!.HasAttribute("open").Should().BeFalse();
    }

    [Fact]
    public void Baseline_snapshot_reuses_the_runs_stored_comparison_and_links_to_compare()
    {
        var deltas = new List<PerformanceMetricDelta>
        {
            PerformanceTestRules.Delta(PerformanceMetric.LatencyP95Ms, 380, 420), PerformanceTestRules.Delta(PerformanceMetric.LatencyP50Ms, 100, 120),
            PerformanceTestRules.Delta(PerformanceMetric.ErrorRatePercent, 0.4, 0.8), PerformanceTestRules.Delta(PerformanceMetric.ThroughputRps, 49, 46),
        };
        var run = Run() with { BaselineIdAtRun = "base-1", Drift = new PerformanceDriftAssessment { BaselineId = "base-1", BaselineVersion = 3, BaselineRunId = Guid.NewGuid(), Compatible = true, Deltas = deltas, State = PerformanceDriftState.DegradedWithinTolerance } };
        var cut = Render(run, new PerformanceBaseline { BaselineId = "base-1", Version = 3 });
        var rows = cut.FindAll("[data-testid=pt-result-baseline-delta]");
        rows.Select(r => r.GetAttribute("data-metric")).Should().Equal("LatencyP95Ms", "ErrorRatePercent", "ThroughputRps");
        rows[0].TextContent.Should().Contain(P.Delta(deltas[0]), "the same delta text Compare renders for the same stored comparison");
        cut.Find("[data-testid=pt-result-baseline-state]").TextContent.Should().Be(P.Label(PerformanceDriftState.DegradedWithinTolerance));
        cut.Find("[data-testid=pt-results-open-compare]").Click();
        _compared.Should().BeSameAs(run);

        var limited = Render(run with { Drift = run.Drift with { Compatible = false, CompatibilityNotes = ["Workload changed."] } });
        limited.Find("[data-testid=pt-result-baseline-limited]").TextContent.Should().Contain("Baseline comparison limited").And.Contain("Workload changed.");
        limited.FindAll("[data-testid=pt-result-baseline-delta]").Should().BeEmpty("no misleading deltas");

        var none = Render(Run());
        none.FindAll("[data-testid=pt-result-baseline]").Should().BeEmpty("no comparison noise without a baseline");
        none.Find("[data-testid=pt-result-nobaseline]").TextContent.Should().StartWith("No baseline selected");
        none.Find("[data-testid=pt-results-promote-history]").Click();
        _history.Should().NotBeNull();
    }

    [Fact]
    public void Response_evidence_resources_graphql_and_historical_configuration_are_explicit()
    {
        var cut = Render(Run(), current: Snapshot with { Version = 5, Scenario = Snapshot.Scenario with { Name = "Edited later" } });
        cut.Find("[data-testid=pt-result-response-state]").TextContent.Should().StartWith("Response body not retained");
        cut.Find("[data-testid=pt-result-probe-state]").TextContent.Should().Be("None recorded with this run");
        cut.Find("[data-testid=pt-result-resources]").TextContent.Should().Be("Not assessed", "disabled observation is never a pass");
        cut.Find("[data-testid=pt-result-scenario]").TextContent.Should().Be("Barn lookup", "the run's own snapshot, not the current draft");
        cut.Find("[data-testid=pt-result-config-differs]").TextContent.Should().Contain("now v5").And.Contain("used v2");
        cut.Find("[data-testid=pt-results-view-scenario]").TextContent.Should().Contain("differs");

        var graphQl = Render(Run() with { DefinitionSnapshot = Snapshot with { TargetType = PerformanceTargetType.GraphQlHttp } });
        graphQl.Find("[data-testid=pt-result-graphql]").TextContent.Should().Contain("GraphQL errors: not assessed").And.Contain("HTTP 200 can still carry GraphQL errors");
    }

    [Fact]
    public void Without_a_run_there_are_no_empty_counters()
    {
        var cut = Render(null);
        cut.Find("[data-testid=pt-no-results]").TextContent.Should().Contain("No completed run selected");
        cut.FindAll("[data-testid=pt-result-summary]").Should().BeEmpty();
    }
}
