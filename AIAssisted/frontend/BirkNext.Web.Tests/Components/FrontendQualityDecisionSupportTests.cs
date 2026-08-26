using System.Text.RegularExpressions;
using BirkNext.Web.Components;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using BirkNext.Web.Tests.Services;
using Bunit;
using FluentAssertions;
using DecisionFixtures = BirkNext.Web.Tests.Services.FrontendQualityDecisionSupportTests;

namespace BirkNext.Web.Tests.Components;

public sealed class FrontendQualityDecisionSupportTests : BunitContext
{
    [Fact]
    public void RendersDispositionCoverageAndEveryConfiguredEngineExactlyOnce()
    {
        var cut = Render<FrontendQualityDecisionSupport>(p => p.Add(c => c.Report, DecisionFixtures.Report()));

        cut.Markup.Should().Contain("Release disposition").And.Contain("ReviewRequired")
            .And.Contain("All required engines assessed").And.Contain("Required assessed:").And.Contain("2 / 2")
            .And.Contain("Optional assessed:").And.Contain("4 / 4");
        cut.FindAll("tr[data-engine-id]").Should().HaveCount(6);
        cut.FindAll("tr[data-engine-id]").Select(row => row.GetAttribute("data-engine-id")).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void GroupedCsp_IsOnePrimaryCardWithTwoInspectableSources()
    {
        var report = DecisionFixtures.Report();
        var before = System.Text.Json.JsonSerializer.Serialize(report.Findings);
        var cut = Render<FrontendQualityDecisionSupport>(p => p.Add(c => c.Report, report));

        cut.FindAll("[data-logical-id='headers:csp:missing']").Should().ContainSingle();
        cut.FindAll("[data-logical-id='headers:csp:missing'] [data-source-finding-id]").Should().HaveCount(2);
        cut.Markup.Should().Contain("Static Security").And.Contain("Passive Security / ZAP");
        System.Text.Json.JsonSerializer.Serialize(report.Findings).Should().Be(before);
    }

    [Fact]
    public void BrowserRuntimeAndManualReview_AreVisibleWithApprovedScopeWording()
    {
        var markup = Render<FrontendQualityDecisionSupport>(p => p.Add(c => c.Report, DecisionFixtures.Report())).Markup;

        markup.Should().Contain("Browser Runtime").And.Contain("Chromium").And.Contain("Console errors:")
            .And.Contain("Critical resource failures:").And.Contain("WASM bootstrap failure")
            .And.Contain("Manual verification required").And.Contain("Keyboard navigation")
            .And.Contain("do not establish WCAG conformance")
            .And.Contain("Passive checks only").And.Contain("No active scan, spider, fuzzing")
            .And.Contain("Synthetic lab performance only");
    }

    [Fact]
    public void ScoreLabelsAreSourceScopedAndUnsupportedAffirmativeClaimsAreAbsent()
    {
        var markup = Render<FrontendQualityDecisionSupport>(p => p.Add(c => c.Report, DecisionFixtures.Report())).Markup;

        markup.Should().Contain("Legacy static review score").And.Contain("Static security score")
            .And.Contain("Passive performance score").And.Contain("Lighthouse performance score")
            .And.NotContain("Overall Quality Score").And.NotContain("release ready").And.NotContain("approved for production")
            .And.NotContain("WCAG compliant").And.NotContain("fully compliant");
        Regex.IsMatch(markup, @"\b(safe|secure|accessible)\b", RegexOptions.IgnoreCase).Should().BeFalse();
    }

    [Fact]
    public void UnknownStandaloneLogicalIssue_RemainsVisible()
    {
        var report = DecisionFixtures.Report();
        var unknown = report.LogicalIssues[0] with { LogicalId = "finding:Lighthouse:unknown:0", CanonicalTitle = "Unknown diagnostic", Sources = [FrontendQualityEngineId.Lighthouse], FindingInstances = [report.LogicalIssues[0].FindingInstances[0]] };
        report = Copy(report, [unknown]);

        Render<FrontendQualityDecisionSupport>(p => p.Add(c => c.Report, report)).Markup.Should().Contain("Unknown diagnostic");
    }

    [Fact]
    public void NosniffGroupedIssue_IsOnePrimaryCardWithBothSources()
    {
        var source = DecisionFixtures.Report();
        var issue = source.LogicalIssues[0] with { LogicalId = "headers:nosniff:missing", CanonicalTitle = "X-Content-Type-Options nosniff header missing" };
        var report = Copy(source, [issue]);

        var cut = Render<FrontendQualityDecisionSupport>(p => p.Add(c => c.Report, report));

        cut.FindAll("[data-logical-id='headers:nosniff:missing']").Should().ContainSingle();
        cut.FindAll("[data-logical-id='headers:nosniff:missing'] [data-source-finding-id]").Should().HaveCount(2);
    }

    [Theory]
    [InlineData(FrontendQualityRequiredCoverageState.SomeRequiredNotAssessed, "Some required engines not assessed")]
    [InlineData(FrontendQualityRequiredCoverageState.NoTrustworthyRequiredAssessment, "No trustworthy required assessment")]
    public void IncompleteCoverage_IsProminentAndMissingRequiredOutcomeRemainsVisible(
        FrontendQualityRequiredCoverageState state, string expected)
    {
        var source = DecisionFixtures.Report();
        var outcomes = DecisionFixtures.AllAssessed();
        outcomes[0] = outcomes[0] with { ExecutionState = FrontendQualityEngineExecutionState.EngineError, SanitizedFailureReason = "Tool unavailable" };
        var report = new FrontendQualityReviewReport
        {
            Coverage = new() { RequiredCoverageState = state }, ReleaseDisposition = state == FrontendQualityRequiredCoverageState.NoTrustworthyRequiredAssessment ? FrontendQualityReleaseDisposition.Blocked : FrontendQualityReleaseDisposition.ReviewRequired,
            EngineOutcomes = outcomes, LogicalIssues = source.LogicalIssues,
        };

        var markup = Render<FrontendQualityDecisionSupport>(p => p.Add(c => c.Report, report)).Markup;
        markup.Should().Contain(expected).And.Contain("Engine error").And.Contain("Tool unavailable");
    }

    [Fact]
    public void ApprovedSanitizedEvidence_DoesNotExposeUiExportSentinel()
    {
        const string sentinel = "SECRET-PHASE2E-UIEXPORT-12345";
        var source = DecisionFixtures.Report();
        var instance = source.LogicalIssues[0].FindingInstances[0] with { SanitizedEvidence = [ReportExportService.SanitizePassive(sentinel)] };
        var issue = source.LogicalIssues[0] with { FindingInstances = [instance, source.LogicalIssues[0].FindingInstances[1]] };

        var markup = Render<FrontendQualityDecisionSupport>(p => p.Add(c => c.Report, Copy(source, [issue]))).Markup;
        markup.Should().NotContain(sentinel).And.Contain("REDACTED");
    }

    private static FrontendQualityReviewReport Copy(FrontendQualityReviewReport source, List<FrontendQualityLogicalIssue> issues) => new()
    {
        TargetUrl = source.TargetUrl, GeneratedAt = source.GeneratedAt, OverallScore = source.OverallScore,
        SecurityScore = source.SecurityScore, PerformanceScore = source.PerformanceScore, Coverage = source.Coverage,
        ReleaseDisposition = source.ReleaseDisposition, EngineOutcomes = source.EngineOutcomes, Findings = source.Findings,
        LogicalIssues = issues, ManualReviewItems = source.ManualReviewItems, BrowserRuntimeReport = source.BrowserRuntimeReport,
        LighthouseReport = source.LighthouseReport,
    };
}
