using System.Text.RegularExpressions;
using BirkNext.Web.Components;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using BirkNext.Web.Tests.Services;
using Bunit;
using FluentAssertions;
using DecisionFixtures = BirkNext.Web.Tests.Services.FrontendQualityDecisionSupportTests;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// Engine coverage: which engines ran, which did not, and why.
///
/// This component used to render the release disposition, the logical issues, the manual-verification list and the
/// score cards as well. Each of those is now owned by the surface that is actually about it — the result summary, the
/// logical-issues view, the manual follow-up section and the engine-scores disclosure — so none of them is rendered
/// twice in two vocabularies. The tests moved with them.
/// </summary>
public sealed class FrontendQualityDecisionSupportTests : BunitContext
{
    // 41, 42. The matrix is the diagnostic surface, and every configured engine appears in it exactly once.
    [Fact]
    public void RendersCoverageAndEveryConfiguredEngineExactlyOnce()
    {
        var cut = Render<FrontendQualityDecisionSupport>(p => p.Add(c => c.Report, DecisionFixtures.Report()));

        cut.Markup.Should().Contain("All required engines assessed")
            .And.Contain("Required assessed:").And.Contain("2 of 2")
            .And.Contain("Optional assessed:").And.Contain("4 of 4 active");
        cut.FindAll("tr[data-engine-id]").Should().HaveCount(6);
        cut.FindAll("tr[data-engine-id]").Select(row => row.GetAttribute("data-engine-id")).Should().OnlyHaveUniqueItems();
    }

    // 5. The release decision belongs at the top of the result, not inside the engine diagnostics.
    [Fact]
    public void TheReleaseDecisionIsNotRestatedInsideTheEngineDiagnostics()
    {
        var markup = Render<FrontendQualityDecisionSupport>(p => p.Add(c => c.Report, DecisionFixtures.Report())).Markup;

        markup.Should().NotContain("Release disposition");
        markup.Should().NotContain("Critical/high logical issues");
        // 36. And the source occurrence count is never presented as a release severity count.
        markup.Should().NotContain("Source findings:");
    }

    [Fact]
    public void EngineScopeStatementsAndBrowserRuntimeDetailRemain()
    {
        var markup = Render<FrontendQualityDecisionSupport>(p => p.Add(c => c.Report, DecisionFixtures.Report())).Markup;

        markup.Should().Contain("Browser Runtime").And.Contain("Chromium").And.Contain("Console errors:")
            .And.Contain("Critical resource failures:").And.Contain("WASM bootstrap failure")
            .And.Contain("do not establish WCAG conformance")
            .And.Contain("Passive checks only").And.Contain("No active scan, spider, fuzzing")
            .And.Contain("Synthetic lab performance only");
    }

    // 39, 76. Scores are not part of the engine diagnostics either; they have their own disclosure.
    [Fact]
    public void ScoresAreNotRenderedHere()
    {
        var markup = Render<FrontendQualityDecisionSupport>(p => p.Add(c => c.Report, DecisionFixtures.Report())).Markup;

        markup.Should().NotContain("Legacy static review score").And.NotContain("Passive performance score");
    }

    [Theory]
    [InlineData(FrontendQualityRequiredCoverageState.SomeRequiredNotAssessed, "Some required engines not assessed")]
    [InlineData(FrontendQualityRequiredCoverageState.NoTrustworthyRequiredAssessment, "No trustworthy required assessment")]
    public void IncompleteCoverage_IsProminentAndMissingRequiredOutcomeRemainsVisible(
        FrontendQualityRequiredCoverageState state, string expected)
    {
        var source = DecisionFixtures.Report();
        var outcomes = DecisionFixtures.AllAssessed();
        outcomes[0] = outcomes[0] with { ExecutionState = FrontendQualityEngineExecutionState.EngineError, OutcomeReason = FrontendQualityEngineOutcomeReason.EngineError, SanitizedFailureReason = "Tool unavailable" };
        var report = new FrontendQualityReviewReport
        {
            Coverage = new() { RequiredCoverageState = state }, ReleaseDisposition = state == FrontendQualityRequiredCoverageState.NoTrustworthyRequiredAssessment ? FrontendQualityReleaseDisposition.Blocked : FrontendQualityReleaseDisposition.ReviewRequired,
            EngineOutcomes = outcomes, LogicalIssues = source.LogicalIssues,
        };

        var markup = Render<FrontendQualityDecisionSupport>(p => p.Add(c => c.Report, report)).Markup;
        markup.Should().Contain(expected).And.Contain("Engine error").And.Contain("Tool unavailable");
    }
}

/// <summary>
/// The logical-issues view: the actionable result surface. One card is one problem, whatever number of observations
/// support it and however many pages it was seen on.
/// </summary>
public sealed class FrontendQualityLogicalIssuesViewTests : BunitContext
{
    private static IRenderedComponent<FrontendQualityLogicalIssues> Render(FrontendQualityReviewReport report) =>
        new FrontendQualityLogicalIssuesViewTests().Render<FrontendQualityLogicalIssues>(p => p.Add(c => c.Issues, report.LogicalIssues));

    // 9, 55, 64. One CSP issue, two inspectable sources, and the source findings untouched.
    [Fact]
    public void GroupedCsp_IsOneCardWithTwoInspectableSources()
    {
        var report = DecisionFixtures.Report();
        var before = System.Text.Json.JsonSerializer.Serialize(report.Findings);

        var cut = Render<FrontendQualityLogicalIssues>(p => p.Add(c => c.Issues, report.LogicalIssues));

        cut.FindAll("[data-logical-id='headers:csp:missing']").Should().ContainSingle();
        cut.FindAll("[data-logical-id='headers:csp:missing'] [data-source-finding-id]").Should().HaveCount(2);
        cut.Markup.Should().Contain("Static Security").And.Contain("Passive Security / ZAP");
        System.Text.Json.JsonSerializer.Serialize(report.Findings).Should().Be(before, "grouping never edits the source findings");
    }

    [Fact]
    public void NosniffGroupedIssue_IsOneCardWithBothSources()
    {
        var source = DecisionFixtures.Report();
        var issue = source.LogicalIssues[0] with { LogicalId = "headers:nosniff:missing", CanonicalTitle = "X-Content-Type-Options nosniff header missing" };

        var cut = Render<FrontendQualityLogicalIssues>(p => p.Add(c => c.Issues, new List<FrontendQualityLogicalIssue> { issue }));

        cut.FindAll("[data-logical-id='headers:nosniff:missing']").Should().ContainSingle();
        cut.FindAll("[data-logical-id='headers:nosniff:missing'] [data-source-finding-id]").Should().HaveCount(2);
    }

    // 30, 82. Raw evidence is never removed, however aggressively issues group.
    [Fact]
    public void UnknownStandaloneLogicalIssue_RemainsVisible()
    {
        var report = DecisionFixtures.Report();
        var unknown = report.LogicalIssues[0] with
        {
            LogicalId = "rule:Lighthouse:unknown:unknown diagnostic",
            CanonicalTitle = "Unknown diagnostic",
            Sources = [FrontendQualityEngineId.Lighthouse],
            FindingInstances = [report.LogicalIssues[0].FindingInstances[0]],
        };

        Render<FrontendQualityLogicalIssues>(p => p.Add(c => c.Issues, new List<FrontendQualityLogicalIssue> { unknown }))
            .Markup.Should().Contain("Unknown diagnostic");
    }

    [Fact]
    public void ApprovedSanitizedEvidence_DoesNotExposeUiExportSentinel()
    {
        const string sentinel = "SECRET-PHASE2E-UIEXPORT-12345";
        var source = DecisionFixtures.Report();
        var instance = source.LogicalIssues[0].FindingInstances[0] with { SanitizedEvidence = [ReportExportService.SanitizePassive(sentinel)] };
        var issue = source.LogicalIssues[0] with { FindingInstances = [instance, source.LogicalIssues[0].FindingInstances[1]] };

        var markup = Render<FrontendQualityLogicalIssues>(p => p.Add(c => c.Issues, new List<FrontendQualityLogicalIssue> { issue })).Markup;

        markup.Should().NotContain(sentinel).And.Contain("REDACTED");
    }
}

/// <summary>
/// Engine scores, and the claims they are not allowed to make.
/// </summary>
public sealed class FrontendQualityEngineScoresTests : BunitContext
{
    // 39, 40, 76. Each score names its source; none of them is presented as the review's result.
    [Fact]
    public void ScoreLabelsAreSourceScopedAndUnsupportedAffirmativeClaimsAreAbsent()
    {
        var markup = Render<FrontendQualityEngineScores>(p => p.Add(c => c.Report, DecisionFixtures.Report())).Markup;

        markup.Should().Contain("Legacy static review score").And.Contain("Static security score")
            .And.Contain("Passive performance score").And.Contain("Lighthouse performance score")
            .And.NotContain("Overall Quality Score").And.NotContain("release ready").And.NotContain("approved for production")
            .And.NotContain("WCAG compliant").And.NotContain("fully compliant");
        // 40. The incomparability is stated, not left to be inferred.
        markup.Should().Contain("not comparable with each other");
        Regex.IsMatch(markup, @"\b(safe|secure|accessible)\b", RegexOptions.IgnoreCase).Should().BeFalse();
    }

    // 76. An engine that never ran reports that, never a zero and never a stale number.
    [Fact]
    public void AnEngineThatNeverRanReportsNotAssessedRatherThanZero()
    {
        var source = DecisionFixtures.Report();
        var outcomes = source.EngineOutcomes
            .Select(o => o.EngineId == FrontendQualityEngineId.Lighthouse
                ? o with { ExecutionState = FrontendQualityEngineExecutionState.Unavailable }
                : o)
            .ToList();
        var report = new FrontendQualityReviewReport
        {
            TargetUrl = source.TargetUrl, GeneratedAt = source.GeneratedAt, OverallScore = source.OverallScore,
            SecurityScore = source.SecurityScore, PerformanceScore = source.PerformanceScore,
            Coverage = source.Coverage, EngineOutcomes = outcomes, LighthouseReport = source.LighthouseReport,
        };

        var lighthouse = Render<FrontendQualityEngineScores>(p => p.Add(c => c.Report, report))
            .Find("[data-score=lighthouse] [data-testid=fqr-engine-score-value]").TextContent.Trim();

        lighthouse.Should().Be("Not assessed");
        lighthouse.Should().NotBe("0");
    }
}
