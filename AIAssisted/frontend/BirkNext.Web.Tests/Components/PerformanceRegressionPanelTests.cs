using BirkNext.Applicability;
using BirkNext.PerformanceTests;
using BirkNext.Web.Components;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// Regression panel: only approved baselines are references (proposals and rejections are never offered), the verdict shows comparability first,
/// the metric table carries the definition's tolerance or "none", thresholds are listed beside the verdict, an exploratory comparison is labelled,
/// and the trend keeps not-comparable runs off by default. The backend's report is rendered as is (synthetic fixtures).
/// </summary>
public sealed class PerformanceRegressionPanelTests : BunitContext
{
    private static readonly Guid BaselineRun = Guid.Parse("b0000000-0000-0000-0000-000000000001");
    private static readonly Guid Current = Guid.Parse("c0000000-0000-0000-0000-000000000002");
    private readonly Mock<IPerformanceTestApiService> _api = new();
    private List<Guid>? _requested;

    public PerformanceRegressionPanelTests()
    {
        _api.Setup(a => a.TrendsAsync("local", "def-1", It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new PerformanceTrend
        {
            DefinitionId = "def-1", BaselineId = "b1", BaselineRunId = BaselineRun, MetricKeys = ["p95"], ExcludedPoints = 1,
            Points =
            [
                Point(BaselineRun, 300, ComparabilityLevel.Comparable, baseline: true), Point(Current, 400, ComparabilityLevel.Comparable),
                Point(Guid.NewGuid(), 900, ComparabilityLevel.NotComparable),
            ],
        });
        _api.Setup(a => a.RegressionAsync("local", "b1", It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
            .Callback((string _, string _, IReadOnlyCollection<Guid> runs, CancellationToken _) => _requested = [.. runs])
            .ReturnsAsync(PerformanceApiResult<PerformanceRegressionReport>.Ok(Report(exploratory: false)));
        Services.AddSingleton(_api.Object);
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private static PerformanceTrendPoint Point(Guid id, double p95, ComparabilityLevel level, bool baseline = false) => new()
    {
        RunId = id, FinishedAt = DateTimeOffset.UtcNow, State = PerformanceRunState.Completed, IsBaselineRun = baseline, Comparability = level,
        ThresholdVerdict = PerformanceQualityVerdict.Pass, Values = new() { ["p95"] = p95 },
    };

    private static PerformanceRegressionReport Report(bool exploratory) => new()
    {
        BaselineId = "b1", BaselineVersion = 1, BaselineRunId = BaselineRun, CurrentRunIds = [Current], Exploratory = exploratory,
        Comparability = exploratory
            ? new PerformanceComparability { Level = ComparabilityLevel.NotComparable, Summary = "Not comparable: Different concurrency.", Differences = [new("Virtual users", "2", "10", ComparabilitySeverity.Blocking, "Different concurrency.")] }
            : new PerformanceComparability { Level = ComparabilityLevel.ComparableWithWarnings, Summary = "Comparable with warnings: Different application versions.", Differences = [new("Application version", "1.4", "1.5", ComparabilitySeverity.Warning, "Different application versions.")] },
        Verdict = exploratory ? RegressionVerdict.Inconclusive : RegressionVerdict.PotentialRegression, VerdictReason = "P95 latency: Beyond the tolerance in a single run.",
        Metrics =
        [
            new MetricRegression { Key = "p95", Label = "P95 latency", Unit = "ms", Baseline = 300, Current = 400, AbsoluteDelta = 100, RelativePercent = 33.3, Tolerance = "+20 %", Runs = 1, RunsBeyondTolerance = 1,
                Verdict = exploratory ? RegressionVerdict.Inconclusive : RegressionVerdict.PotentialRegression, Reason = "Beyond the tolerance in a single run.", Aggregation = "single run" },
            new MetricRegression { Key = "p99", Label = "P99 latency", Unit = "ms", Baseline = 450, Current = 600, Runs = 1, Verdict = RegressionVerdict.NotAssessed, Reason = "No tolerance configured.", Aggregation = "single run" },
        ],
        Thresholds = [new RunThresholdStatus(Current, PerformanceQualityVerdict.Pass, 0, 0)],
        Derivation = ["Tolerance: the drift policy configured on the definition."], Limitations = ["1 current run(s): a regression can be at most Potential."],
    };

    private IRenderedComponent<PerformanceRegressionPanel> Render(params PerformanceBaseline[] baselines) => Render<PerformanceRegressionPanel>(p => p
        .Add(x => x.EnvironmentId, "local").Add(x => x.DefinitionId, "def-1")
        .Add(x => x.Runs, [Run(BaselineRun), Run(Current)])
        .Add(x => x.Baselines, [.. baselines]));

    private static PerformanceTestRun Run(Guid id) => new() { RunId = id, State = PerformanceRunState.Completed, CreatedAt = DateTimeOffset.UtcNow, FinishedAt = DateTimeOffset.UtcNow };

    private static PerformanceBaseline B(string id, PerformanceBaselineStatus status) => new() { BaselineId = id, Name = id, Version = 1, Status = status, RunId = BaselineRun };

    [Fact]
    public void Only_approved_baselines_are_offered_and_a_proposal_alone_explains_what_is_missing()
    {
        Render(B("p", PerformanceBaselineStatus.Proposed), B("r", PerformanceBaselineStatus.Rejected)).Find("[data-testid=pt-regression-no-baseline]").TextContent.Should().Contain("approve");
        var cut = Render(B("b1", PerformanceBaselineStatus.Active), B("p", PerformanceBaselineStatus.Proposed), B("old", PerformanceBaselineStatus.Superseded));
        cut.FindAll("[data-testid=pt-regression-baseline] option").Select(o => o.GetAttribute("value")).Should().Equal("b1", "old");
        cut.FindAll("[data-testid=pt-regression-run]").Should().ContainSingle("the baseline's own run is not a candidate");
    }

    [Fact]
    public void The_report_shows_comparability_first_the_tolerance_or_none_and_thresholds_beside_the_verdict()
    {
        var cut = Render(B("b1", PerformanceBaselineStatus.Active));
        cut.Find("[data-testid=pt-regression-go]").Click();
        _requested.Should().Equal(Current);
        var result = cut.Find("[data-testid=pt-regression-result]");
        result.GetAttribute("data-verdict").Should().Be("PotentialRegression");
        result.GetAttribute("data-comparability").Should().Be("ComparableWithWarnings");
        cut.Find("[data-testid=pt-regression-differences]").TextContent.Should().Contain("Application version");
        var p95 = cut.Find("[data-testid=pt-regression-metric][data-key=p95]").TextContent;
        p95.Should().Contain("300 ms").And.Contain("400 ms").And.Contain("+100 ms").And.Contain("+33.3 %").And.Contain("+20 %").And.Contain("1 of 1").And.Contain("Potential regression");
        cut.Find("[data-testid=pt-regression-metric][data-key=p99]").TextContent.Should().Contain("none").And.Contain("Not assessed");
        cut.Find("[data-testid=pt-regression-thresholds]").TextContent.Should().Contain("Pass");
        cut.FindAll("[data-testid=pt-regression-exploratory]").Should().BeEmpty();
    }

    [Fact]
    public void A_not_comparable_report_is_labelled_exploratory()
    {
        _api.Setup(a => a.RegressionAsync("local", "b1", It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(PerformanceApiResult<PerformanceRegressionReport>.Ok(Report(exploratory: true)));
        var cut = Render(B("b1", PerformanceBaselineStatus.Active));
        cut.Find("[data-testid=pt-regression-go]").Click();
        cut.Find("[data-testid=pt-regression-exploratory]").TextContent.Should().Contain("not a pass or fail");
        cut.Find("[data-testid=pt-regression-differences] tr[data-severity=Blocking]").TextContent.Should().Contain("Virtual users");
    }

    [Fact]
    public void The_trend_keeps_not_comparable_runs_off_until_asked_and_marks_the_baseline()
    {
        var cut = Render(B("b1", PerformanceBaselineStatus.Active));
        cut.FindAll("[data-testid=pt-trend-point]").Should().HaveCount(2);
        cut.Find("[data-testid=pt-trend-point][data-baseline=true]").TextContent.Should().Contain("(baseline)");
        cut.Find("[data-testid=pt-trend-sparkline]").GetAttribute("aria-label").Should().Contain("P95 of 2 comparable runs").And.Contain("baseline 300 ms");
        cut.Find("[data-testid=pt-trend-show-excluded]").Change(true);
        cut.FindAll("[data-testid=pt-trend-point]").Should().HaveCount(3);
    }
}
