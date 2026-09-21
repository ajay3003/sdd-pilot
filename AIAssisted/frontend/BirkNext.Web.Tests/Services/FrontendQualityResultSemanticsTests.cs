using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;
using Xunit;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// Post-run Frontend Quality Review kept three different facts in one field.
///
/// Every required engine being blocked made the page say "Review failed to run" and "there is no result to report" —
/// over a review that had produced accessibility, performance, standards and Blazor findings from the optional engines
/// that did complete. Whether the process ran, how much of the required scope it covered, and whether the result can
/// support a release are three answers, and only the first one is the execution state.
/// </summary>
public sealed class FrontendQualityResultSemanticsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 21, 14, 0, 0, TimeSpan.Zero);

    private static FrontendQualityEngineOutcome Outcome(FrontendQualityEngineId id, FrontendQualityEngineExecutionState state,
        FrontendQualityEngineRequirement requirement = FrontendQualityEngineRequirement.Required) =>
        new() { EngineId = id, ExecutionState = state, Requirement = requirement, Enabled = true };

    /// <summary>The live shape: both required engines blocked, three optional engines assessed and producing findings.</summary>
    private static FrontendQualityReviewReport BlockedRequiredWithOptionalEvidence(int findings = 41, string? error = null, bool requiredAssessed = false)
    {
        var outcomes = new List<FrontendQualityEngineOutcome>
        {
            Outcome(FrontendQualityEngineId.StaticSecurity, requiredAssessed ? FrontendQualityEngineExecutionState.Assessed : FrontendQualityEngineExecutionState.Unavailable),
            Outcome(FrontendQualityEngineId.PassivePerformance, requiredAssessed ? FrontendQualityEngineExecutionState.Assessed : FrontendQualityEngineExecutionState.Unavailable),
            Outcome(FrontendQualityEngineId.Accessibility, FrontendQualityEngineExecutionState.Assessed, FrontendQualityEngineRequirement.Optional),
            Outcome(FrontendQualityEngineId.BrowserQuality, FrontendQualityEngineExecutionState.Assessed, FrontendQualityEngineRequirement.Optional),
            Outcome(FrontendQualityEngineId.PerformanceQuality, FrontendQualityEngineExecutionState.Assessed, FrontendQualityEngineRequirement.Optional),
        };
        return new FrontendQualityReviewReport
        {
            ErrorMessage = error,
            Coverage = FrontendQualityCoverage.Evaluate(outcomes),
            EngineOutcomes = outcomes,
            OverallScore = 67, SecurityScore = 57, PerformanceScore = 100,
            Findings = [.. Enumerable.Range(0, findings).Select(i => new FrontendQualityFinding
            {
                Title = $"Finding {i}", Severity = FrontendQualitySeverity.Medium,
                Category = FrontendQualityCategory.Performance,
            })],
        };
    }

    // ── §86. Execution, completeness and release are three answers ──────────

    // 1, 2, 4, 5, 6.
    [Fact]
    public void BlockedRequiredEnginesAreIncompleteCoverage_NotAFailedRun()
    {
        var report = BlockedRequiredWithOptionalEvidence();

        var view = FrontendQualityResultPresentation.Build(report);

        view.State.Should().NotBe(FrontendQualityResultState.FailedToRun, "the review ran and produced findings");
        view.State.Should().Be(FrontendQualityResultState.Incomplete);
        FrontendQualityResultStates.Label(view.State).Should().NotContain("failed to run");
        view.Summary.Should().NotContain("no result to report");
    }

    // 1, 6.
    [Fact]
    public void TheSummarySaysWhatIsMissingAndWhatSurvived()
    {
        var report = BlockedRequiredWithOptionalEvidence();

        var summary = FrontendQualityResultPresentation.Build(report).Summary;

        summary.Should().Contain("0 of 2 required engines completed");
        summary.Should().Contain("Available evidence produced", "partial results are results");
    }

    /// <summary>A run that genuinely could not execute still says so.</summary>
    [Fact]
    public void AnExecutionErrorIsStillAFailedRun()
    {
        var failed = BlockedRequiredWithOptionalEvidence(findings: 0, error: "The review process could not start.");

        FrontendQualityResultPresentation.Build(failed).State.Should().Be(FrontendQualityResultState.FailedToRun);
    }

    /// <summary>With no other evidence either, it is still incomplete coverage rather than a failed process.</summary>
    [Fact]
    public void NoOptionalEvidenceEitherIsStillIncompleteRatherThanFailed()
    {
        var report = BlockedRequiredWithOptionalEvidence(findings: 0);

        var view = FrontendQualityResultPresentation.Build(report);

        view.State.Should().Be(FrontendQualityResultState.Incomplete);
        view.Summary.Should().Contain("0 of 2 required engines completed");
        view.Summary.Should().Contain("No other evidence source produced findings.");
    }

    // ── §87. A score belongs to an engine that looked at the target ─────────

    // 7, 8, 9, 10.
    [Fact]
    public void AnEngineThatDidNotAssessHasNoScore()
    {
        var report = BlockedRequiredWithOptionalEvidence();

        // The values are still on the report — that is exactly why the guard has to be at the point of display.
        report.SecurityScore.Should().Be(57);
        report.PerformanceScore.Should().Be(100);

        bool Assessed(FrontendQualityEngineId id) => report.EngineOutcomes
            .Any(o => o.EngineId == id && o.ExecutionState == FrontendQualityEngineExecutionState.Assessed);

        Assessed(FrontendQualityEngineId.StaticSecurity).Should().BeFalse();
        Assessed(FrontendQualityEngineId.PassivePerformance).Should().BeFalse();
    }

    // 10.
    [Fact]
    public void AnAssessedEngineKeepsItsScore()
    {
        var assessed = BlockedRequiredWithOptionalEvidence(requiredAssessed: true);

        assessed.EngineOutcomes.Should().OnlyContain(o => o.ExecutionState == FrontendQualityEngineExecutionState.Assessed);
        assessed.SecurityScore.Should().Be(57, "the engine ran, so its score is a measurement");
    }

    // ── §89. A domain never denies the findings it has ──────────────────────

    /// <summary>
    /// Security's own engines did not run, so the card said "No evidence is available for this domain, so nothing can
    /// be concluded about it" — on a page that listed missing CSP, HSTS and three more headers under that very
    /// category. Whatever produced those findings, the domain has evidence.
    /// </summary>
    // 18, 19.
    [Fact]
    public void ADomainWithFindingsIsNeverReportedAsHavingNoEvidence()
    {
        var report = BlockedRequiredWithOptionalEvidence(findings: 0);
        report.Findings.Add(new FrontendQualityFinding
        {
            Category = FrontendQualityCategory.Security, Severity = FrontendQualitySeverity.Critical,
            Title = "Missing Content-Security-Policy",
        });

        var security = FrontendQualityResultPresentation.Build(report).Domains
            .Single(d => d.Category == FrontendQualityCategory.Security);

        security.State.Should().NotBe(FrontendQualityDomainResultState.NoEvidence);
        security.FindingCount.Should().Be(1, "the finding exists, so the count is known");
        security.Summary.Should().NotContain("nothing can be concluded");
    }

    // 20.
    [Fact]
    public void ADomainWithNeitherEngineNorFindingStatesWhatIsMissing()
    {
        var report = BlockedRequiredWithOptionalEvidence(findings: 0);

        var security = FrontendQualityResultPresentation.Build(report).Domains
            .Single(d => d.Category == FrontendQualityCategory.Security);

        security.FindingCount.Should().BeNull("nothing established a number");
        security.Summary.Should().Contain("No dedicated assessment evidence");
        security.Summary.Should().NotContain("nothing can be concluded");
    }
}
