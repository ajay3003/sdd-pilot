using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// "Assessment completeness: Full" was read off required coverage alone. A review with three optional engines that
/// never ran, two domains reporting limited evidence and an outstanding manual accessibility assessment described
/// itself as complete.
///
/// The four dimensions are independent and none of them implies another: a review can execute perfectly, cover
/// everything required, be missing optional depth, and still owe a person work no engine can do.
/// </summary>
public sealed class FrontendQualityResultCompletenessTests
{
    private static FrontendQualityEngineOutcome Outcome(
        FrontendQualityEngineId id, FrontendQualityEngineRequirement requirement,
        FrontendQualityEngineExecutionState state = FrontendQualityEngineExecutionState.Assessed,
        string? displayName = null) => new()
        {
            EngineId = id, DisplayName = displayName ?? id.ToString(), Enabled = true,
            Requirement = requirement, ExecutionState = state,
        };

    /// <summary>The live shape: both required engines assessed, two of five optional engines assessed.</summary>
    private static List<FrontendQualityEngineOutcome> RequiredCompleteOptionalPartial() =>
    [
        Outcome(FrontendQualityEngineId.StaticSecurity, FrontendQualityEngineRequirement.Required),
        Outcome(FrontendQualityEngineId.PassivePerformance, FrontendQualityEngineRequirement.Required),
        Outcome(FrontendQualityEngineId.BrowserQuality, FrontendQualityEngineRequirement.Optional, displayName: "Browser Quality"),
        Outcome(FrontendQualityEngineId.PerformanceQuality, FrontendQualityEngineRequirement.Optional, displayName: "BirkNext Performance Quality"),
        Outcome(FrontendQualityEngineId.Accessibility, FrontendQualityEngineRequirement.Optional, FrontendQualityEngineExecutionState.Unavailable, "Accessibility"),
        Outcome(FrontendQualityEngineId.Lighthouse, FrontendQualityEngineRequirement.Optional, FrontendQualityEngineExecutionState.Unavailable, "Lighthouse"),
        Outcome(FrontendQualityEngineId.PassiveSecurity, FrontendQualityEngineRequirement.Optional, FrontendQualityEngineExecutionState.Unavailable, "Passive Security"),
    ];

    private static FrontendQualityReviewReport Report(
        List<FrontendQualityEngineOutcome>? outcomes = null,
        WcagAssessment? wcag = null,
        List<FrontendQualityManualReviewItem>? manual = null,
        FrontendQualityReleaseDisposition disposition = FrontendQualityReleaseDisposition.ReviewRequired)
    {
        var engines = outcomes ?? RequiredCompleteOptionalPartial();
        return new FrontendQualityReviewReport
        {
            TargetUrl = "https://m2lbdev.example.test/", GeneratedAt = DateTime.UtcNow, CompletedAt = DateTime.UtcNow,
            EngineOutcomes = engines, Coverage = FrontendQualityCoverage.Evaluate(engines),
            ReleaseDisposition = disposition, Wcag = wcag, ManualReviewItems = manual ?? [],
        };
    }

    // ── §62. The five completeness cases ──────────────────────────────────────────────────────────────────────────

    // 1, 3. Required complete + optional partial is NOT "Full".
    [Fact]
    public void RequiredCompleteWithOptionalPartial_IsNeverReportedAsFullCompleteness()
    {
        var result = FrontendQualityResultPresentation.Build(Report());
        var completeness = result.Completeness.Should().NotBeNull().And.Subject.As<FrontendQualityCompleteness>();

        completeness.RequiredCoverage.Should().Be(FrontendQualityRequiredCoverageState.AllRequiredAssessed);
        completeness.RequiredCoverageLabel.Should().Be("Complete");
        completeness.OptionalCoverage.Should().Be(FrontendQualityOptionalCoverageState.Partial);
        completeness.OptionalCoverageLabel.Should().Be("Partial");
        completeness.OptionalAssessed.Should().Be(2);
        completeness.OptionalTotal.Should().Be(5);

        // 3. The single word that hid all of this is gone.
        completeness.IsFullyComplete.Should().BeFalse();
        completeness.RequiredCoverageLabel.Should().NotBe("Full");
        completeness.OptionalCoverageLabel.Should().NotBe("Full");
    }

    // 4, 5. An optional engine that could not run is not an execution failure.
    [Fact]
    public void AnUnavailableOptionalEngineLeavesExecutionCompleted()
    {
        var completeness = FrontendQualityResultPresentation.Build(Report()).Completeness!;

        FrontendQualityResultStates.IsCompleted(completeness.Execution).Should().BeTrue();
        completeness.ExecutionLabel.Should().Be("Completed");
    }

    // 2. Manual assessment is its own dimension and does not move with coverage.
    [Fact]
    public void ManualAssessmentIsIndependentOfEveryCoverageDimension()
    {
        var everythingAssessed = RequiredCompleteOptionalPartial()
            .Select(o => o with { ExecutionState = FrontendQualityEngineExecutionState.Assessed }).ToList();

        var withoutManual = FrontendQualityResultPresentation.Build(Report(everythingAssessed)).Completeness!;
        withoutManual.OptionalCoverage.Should().Be(FrontendQualityOptionalCoverageState.Complete);
        withoutManual.ManualAssessment.Should().Be(FrontendQualityManualAssessmentState.NotRequired);
        withoutManual.IsFullyComplete.Should().BeTrue("this is the one case a single \"Full\" would have been honest");

        var withManual = FrontendQualityResultPresentation.Build(Report(everythingAssessed,
            manual: [new FrontendQualityManualReviewItem { Title = "Keyboard navigation", Reason = "r", Source = "s" }])).Completeness!;
        withManual.OptionalCoverage.Should().Be(FrontendQualityOptionalCoverageState.Complete, "coverage is unchanged");
        withManual.ManualAssessment.Should().Be(FrontendQualityManualAssessmentState.Required);
        withManual.IsFullyComplete.Should().BeFalse();
    }

    [Fact]
    public void NoOptionalEngineActiveIsNotTheSameAsNoOptionalCoverage()
    {
        var requiredOnly = RequiredCompleteOptionalPartial()
            .Where(o => o.Requirement == FrontendQualityEngineRequirement.Required).ToList();

        var completeness = FrontendQualityResultPresentation.Build(Report(requiredOnly)).Completeness!;

        completeness.OptionalCoverage.Should().Be(FrontendQualityOptionalCoverageState.NotApplicable);
        completeness.OptionalCoverageLabel.Should().Be("No optional engine active");
        completeness.IsFullyComplete.Should().BeTrue("nothing optional was expected, so nothing optional is missing");
    }

    // ── §5, §35, §36. The release decision ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ReleaseReasonsCountLogicalIssues_NotSourceOccurrences()
    {
        var report = Report();
        // One contrast defect, observed on five routes.
        var issue = new FrontendQualityLogicalIssue
        {
            LogicalId = "rule:BrowserQuality:a11y-contrast:contrast minimum",
            CanonicalTitle = "Contrast (Minimum)",
            PrimarySeverity = FrontendQualitySeverity.High,
            Recommendation = "Increase the contrast ratio.",
            AffectedPages = ["/", "/a", "/b", "/c", "/d"],
            FindingInstances = Enumerable.Range(0, 5).Select(i => new FrontendQualityFindingInstance
            {
                EngineId = FrontendQualityEngineId.BrowserQuality, SourceSystem = "Browser Quality",
                SourceFindingId = $"bq-{i}", Title = "Contrast (Minimum)", Description = "d", Recommendation = "r",
                Severity = FrontendQualitySeverity.High,
            }).ToList(),
        };

        var release = FrontendQualityResultPresentation.Release(
            report, FrontendQualityResultPresentation.Completeness(report, FrontendQualityResultState.Completed, null),
            [issue], null);

        release.Label.Should().Be("Review required");
        // 36. One issue, not five. The occurrence count says how much evidence there is, not how serious it is.
        release.Reasons.Should().Contain("1 critical or high logical issue to resolve.");
        release.Reasons.Should().NotContain(r => r.Contains("5 critical"));
    }

    // 35. "Review required" never overstates itself, and the strongest statement is never an approval.
    [Fact]
    public void NoAutomatedBlockIsStatedWithoutClaimingReleaseReadiness()
    {
        var report = Report(disposition: FrontendQualityReleaseDisposition.NoAutomatedBlockDetected);

        var release = FrontendQualityResultPresentation.Build(report).Release!;

        release.Label.Should().Be("No automated block detected");
        release.Statement.Should().Contain("not a release approval");
        release.Statement.Should().NotContainAny("ready to release", "approved", "safe");
        release.Disposition.Should().Be(FrontendQualityReleaseDisposition.NoAutomatedBlockDetected);
    }

    // 35. Blocked is used only when blocked semantics apply.
    [Fact]
    public void BlockedIsReservedForBlockedSemantics()
    {
        FrontendQualityResultPresentation.Build(Report()).Release!.Label.Should().Be("Review required");
        FrontendQualityResultPresentation.Build(Report(disposition: FrontendQualityReleaseDisposition.Blocked))
            .Release!.Label.Should().Be("Blocked");
    }

    // ── §20, §72. What did not contribute, by name ────────────────────────────────────────────────────────────────

    [Fact]
    public void LimitedEvidenceNamesTheEngineThatDidNotContribute()
    {
        var result = FrontendQualityResultPresentation.Build(Report());

        var performance = result.Domain(FrontendQualityCategory.Performance);
        performance.Limitation.Should().Contain("Lighthouse");
        performance.Limitation.Should().NotContain("1 evidence source");

        var security = result.Domain(FrontendQualityCategory.Security);
        security.Limitation.Should().Contain("Passive Security");
        security.Limitation.Should().NotContain("evidence source did not contribute", "the source is known, so it is named");
    }

    // 19, 71. A blocked dedicated engine with findings from another source is explained, not left to be inferred.
    [Fact]
    public void AccessibilityExplainsWhichSourceCoveredItWhenItsOwnEngineDidNotRun()
    {
        var outcomes = RequiredCompleteOptionalPartial();
        var report = new FrontendQualityReviewReport
        {
            TargetUrl = "https://m2lbdev.example.test/", GeneratedAt = DateTime.UtcNow,
            EngineOutcomes = outcomes, Coverage = FrontendQualityCoverage.Evaluate(outcomes),
            Findings =
            [
                new FrontendQualityFinding
                {
                    Id = "bq-contrast", Title = "Contrast (Minimum) — /", Category = FrontendQualityCategory.Accessibility,
                    Severity = FrontendQualitySeverity.High, Description = "d", Recommendation = "r",
                    EngineId = FrontendQualityEngineId.BrowserQuality, SourceSystem = "Browser Quality",
                },
            ],
        };

        var accessibility = FrontendQualityResultPresentation.Build(report).Domain(FrontendQualityCategory.Accessibility);

        // The engine matrix still says the Accessibility engine did not run; the domain says where its evidence came from.
        accessibility.Limitation.Should().Contain("Accessibility did not run");
        accessibility.Limitation.Should().Contain("Browser Quality");
        accessibility.FindingCount.Should().Be(1, "the domain has evidence even though its own engine did not run");
    }
}
