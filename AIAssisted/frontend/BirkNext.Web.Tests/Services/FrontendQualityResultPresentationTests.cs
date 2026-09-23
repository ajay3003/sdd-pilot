using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// The result presenter is where a finished review stops being engine state and becomes a reviewable outcome. These tests
/// hold three invariants that matter more than any layout: an execution problem is never a quality finding, an absence of
/// evidence is never a count, and the accessibility profile belongs to the run that produced the result.
/// </summary>
public sealed class FrontendQualityResultPresentationTests
{
    private static FrontendQualityEngineOutcome Outcome(
        FrontendQualityEngineId id,
        FrontendQualityEngineExecutionState state = FrontendQualityEngineExecutionState.Assessed,
        FrontendQualityEngineRequirement requirement = FrontendQualityEngineRequirement.Required,
        bool enabled = true,
        FrontendQualityEngineOutcomeReason reason = FrontendQualityEngineOutcomeReason.None) => new()
        {
            EngineId = id, DisplayName = id.ToString(), Enabled = enabled,
            Requirement = requirement, ExecutionState = state, OutcomeReason = reason,
        };

    private static FrontendQualityReviewReport Report(
        IEnumerable<FrontendQualityEngineOutcome>? outcomes = null,
        IEnumerable<FrontendQualityFinding>? findings = null,
        WcagAssessment? wcag = null,
        string? error = null,
        PreflightStatus? preflight = null) => new()
        {
            TargetUrl = "https://m2lbdev.example.test/",
            TargetEnvironment = new FrontendReviewTargetIdentity("dev", "M2LB DEV", "Development", "https://m2lbdev.example.test/", DateTime.UtcNow),
            GeneratedAt = DateTime.UtcNow,
            CompletedAt = DateTime.UtcNow,
            EngineOutcomes = (outcomes ?? DefaultOutcomes()).ToList(),
            Findings = (findings ?? []).ToList(),
            Wcag = wcag,
            ErrorMessage = error,
            PreflightStatus = preflight,
        };

    /// <summary>Every engine assessed: the baseline a "nothing went wrong" result is measured against.</summary>
    private static IEnumerable<FrontendQualityEngineOutcome> DefaultOutcomes() =>
        Enum.GetValues<FrontendQualityEngineId>().Select(id => Outcome(id));

    private static FrontendQualityFinding Finding(FrontendQualityCategory category, FrontendQualitySeverity severity = FrontendQualitySeverity.Medium) =>
        new() { Category = category, Severity = severity, Title = $"{category} finding" };

    private static WcagAssessment Assessment(string profileId, params (string CriterionId, WcagStatus Status)[] results)
    {
        var profile = WcagProfiles.Resolve(profileId);
        return new WcagAssessment
        {
            Profile = profile,
            PagesWithEvidence = 1,
            Results = results.Select(r => new WcagCriterionResult
            {
                Definition = WcagRegistry.All.Single(d => d.CriterionId == r.CriterionId),
                Page = "/",
                Status = r.Status,
                LastTested = DateTimeOffset.UtcNow,
            }).ToList(),
        };
    }

    // ── §5, §30, §31. Overall result vocabulary ───────────────────────────────────────────────────────────────────

    [Fact]
    public void ACleanRunIsCompleted_AndNeverClaimsMoreThanThat()
    {
        var view = FrontendQualityResultPresentation.Build(Report());

        view.State.Should().Be(FrontendQualityResultState.Completed);
        view.StateLabel.Should().Be("Completed");
        foreach (var claim in new[] { "Passed", "Compliant", "Secure", "Healthy", "Fully compatible" })
            view.StateLabel.Should().NotContain(claim);
    }

    // 29 (§47). An execution failure is reported as one, not as an encouraging absence of findings.
    [Fact]
    public void AnExecutionErrorIsAFailedRun_NotAQualityResult()
    {
        var view = FrontendQualityResultPresentation.Build(Report(error: "The review could not start."));

        view.State.Should().Be(FrontendQualityResultState.FailedToRun);
        FrontendQualityResultStates.IsCompleted(view.State).Should().BeFalse();
        view.Summary.Should().Be("The review could not start.");
        view.Summary.Should().NotContain("0 findings");
    }

    [Theory]
    [InlineData(PreflightStatus.AuthenticationRequired)]
    [InlineData(PreflightStatus.Unreachable)]
    [InlineData(PreflightStatus.TimedOut)]
    public void AnUnreachableOrProtectedTargetIsBlocked_NotCompleted(PreflightStatus status)
    {
        var view = FrontendQualityResultPresentation.Build(Report(preflight: status));

        view.State.Should().Be(FrontendQualityResultState.Blocked);
        FrontendQualityResultStates.IsCompleted(view.State).Should().BeFalse();
    }

    // 30 (§47). Partial evidence and a completed review are not mutually exclusive.
    [Fact]
    public void PartialEvidenceStillCompletesTheReview_WithLimitations()
    {
        var outcomes = DefaultOutcomes().Select(o => o.EngineId == FrontendQualityEngineId.Lighthouse
            ? o with { ExecutionState = FrontendQualityEngineExecutionState.Unavailable, Requirement = FrontendQualityEngineRequirement.Optional }
            : o);

        var view = FrontendQualityResultPresentation.Build(Report(outcomes));

        view.State.Should().Be(FrontendQualityResultState.CompletedWithLimitations);
        FrontendQualityResultStates.IsCompleted(view.State).Should().BeTrue();
        view.Domain(FrontendQualityCategory.Performance).State
            .Should().Be(FrontendQualityDomainResultState.CompletedWithLimitedEvidence);
    }

    [Fact]
    /// <summary>
    /// Every required engine being unavailable is incomplete required coverage, not a failed run. This asserted
    /// FailedToRun, which is what made the page say "there is no result to report" over a review that had produced
    /// findings from the optional engines that did complete.
    /// </summary>
    public void NoTrustworthyRequiredAssessmentIsIncompleteCoverage_NotAFailedRun()
    {
        var outcomes = DefaultOutcomes().Select(o => o with { ExecutionState = FrontendQualityEngineExecutionState.Unavailable });

        var view = FrontendQualityResultPresentation.Build(Report(outcomes));

        view.State.Should().Be(FrontendQualityResultState.Incomplete);
        view.Summary.Should().NotContain("no result to report");
    }

    // ── §26, §29. A domain's result is never an engine's availability ─────────────────────────────────────────────

    // 26 (§47).
    [Fact]
    public void AnUnavailableOptionalEngineDoesNotBecomeTheDomainResult()
    {
        var outcomes = DefaultOutcomes().Select(o => o.EngineId == FrontendQualityEngineId.Lighthouse
            ? o with { ExecutionState = FrontendQualityEngineExecutionState.Unavailable, Requirement = FrontendQualityEngineRequirement.Optional }
            : o);

        var performance = FrontendQualityResultPresentation.Build(Report(outcomes, [Finding(FrontendQualityCategory.Performance)]))
            .Domain(FrontendQualityCategory.Performance);

        performance.State.Should().Be(FrontendQualityDomainResultState.CompletedWithLimitedEvidence);
        performance.FindingCount.Should().Be(1);
        performance.Summary.Should().Contain("1 finding was recorded");
        performance.Summary.Should().NotContain("Lighthouse");
    }

    // 27 (§47). Missing browser evidence limits the evidence; it does not fail the review.
    [Fact]
    public void MissingBrowserEvidenceIsALimitation_NotAFailure()
    {
        var outcomes = DefaultOutcomes().Select(o => o.EngineId == FrontendQualityEngineId.BrowserRuntime
            ? o with
            {
                ExecutionState = FrontendQualityEngineExecutionState.Unavailable,
                Requirement = FrontendQualityEngineRequirement.Optional,
                OutcomeReason = FrontendQualityEngineOutcomeReason.BrowserCompanionNotConnected,
            }
            : o);

        var blazor = FrontendQualityResultPresentation.Build(Report(outcomes)).Domain(FrontendQualityCategory.BlazorWasm);

        blazor.State.Should().Be(FrontendQualityDomainResultState.CompletedWithLimitedEvidence);
        blazor.State.Should().NotBe(FrontendQualityDomainResultState.FailedToRun);
        // 20, 72. The engine that did not contribute is NAMED. "1 evidence source did not contribute to this domain"
        // was true and sent the reader to the engine matrix to work out which one.
        blazor.Limitation.Should().Be("BrowserRuntime did not contribute to this domain.", "the outcome's own display name is used verbatim");
        blazor.Limitation.Should().NotContain("1 evidence source");
        blazor.Limitation.Should().NotContainAny("Browser Companion", "pairing", "Browser Quality");
    }

    // 28 (§47). No evidence is an unknown, never a zero.
    [Fact]
    public void NoEvidenceIsNotReportedAsZeroFindings()
    {
        var outcomes = DefaultOutcomes().Select(o => o with { ExecutionState = FrontendQualityEngineExecutionState.Unavailable });

        var security = FrontendQualityResultPresentation.Build(Report(outcomes)).Domain(FrontendQualityCategory.Security);

        security.State.Should().Be(FrontendQualityDomainResultState.NoEvidence);
        security.FindingCount.Should().BeNull("a domain nothing assessed has an unknown number of issues");
        security.Summary.Should().Be("No dedicated assessment evidence for this domain.");
        security.Summary.Should().NotContain("0");
    }

    [Fact]
    public void ADomainWithNoActiveEngineSaysItWasNotAssessed()
    {
        var outcomes = DefaultOutcomes().Select(o => o with { Enabled = false, ExecutionState = FrontendQualityEngineExecutionState.Disabled });

        var performance = FrontendQualityResultPresentation.Build(Report(outcomes)).Domain(FrontendQualityCategory.Performance);

        performance.State.Should().Be(FrontendQualityDomainResultState.NotAssessed);
        performance.FindingCount.Should().BeNull();
        performance.Summary.Should().Be("Not assessed in this run.");
    }

    [Fact]
    public void AnEngineThatErroredIsAnExecutionProblemForItsDomain()
    {
        var outcomes = DefaultOutcomes().Select(o =>
            FrontendQualityCategoryEngines.For(FrontendQualityCategory.Performance).Contains(o.EngineId)
                ? o with { ExecutionState = FrontendQualityEngineExecutionState.EngineError }
                : o);

        var performance = FrontendQualityResultPresentation.Build(Report(outcomes)).Domain(FrontendQualityCategory.Performance);

        performance.State.Should().Be(FrontendQualityDomainResultState.FailedToRun);
        performance.FindingCount.Should().BeNull();
        performance.Limitation.Should().Contain("execution problem, not a review finding");
    }

    // ── §18. Domain order ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void DomainsAreOrderedAccessibilityFirst()
    {
        FrontendQualityResultPresentation.Build(Report()).Domains.Select(d => d.Category)
            .Should().Equal(
                FrontendQualityCategory.Accessibility,
                FrontendQualityCategory.Performance,
                FrontendQualityCategory.Security,
                FrontendQualityCategory.Standards,
                FrontendQualityCategory.BlazorWasm,
                FrontendQualityCategory.Readiness);
    }

    // ── §9, §10. Accessibility semantics ──────────────────────────────────────────────────────────────────────────

    // 4 (§43). Zero automated failures is stated as an absence of detected violations, never as conformance.
    [Fact]
    public void ZeroAutomatedFailuresIsNeverRenderedAsCompliance()
    {
        var wcag = Assessment(WcagProfiles.NorwegianId, ("1.1.1", WcagStatus.Pass), ("1.3.1", WcagStatus.NotTested));

        var accessibility = FrontendQualityResultPresentation.Accessibility(Report(wcag: wcag))!;

        accessibility.Failed.Should().Be(0);
        accessibility.Statement.Should().Be(
            "No automated WCAG violations were detected in the available evidence. Manual assessment is still required.");
        foreach (var claim in new[] { "Passed", "Compliant", "conformance", "No accessibility issues" })
            accessibility.Statement.Should().NotContain(claim);
    }

    // 5, 6, 7 (§43). Four statuses, four numbers. None of them is folded into another.
    [Fact]
    public void FailedManualReviewManualOnlyAndNotAssessedStayFourSeparateCounts()
    {
        var wcag = Assessment(WcagProfiles.NorwegianId,
            ("1.1.1", WcagStatus.Fail),
            ("1.3.1", WcagStatus.ManualReviewRequired),
            ("1.4.3", WcagStatus.NotTested));

        var accessibility = FrontendQualityResultPresentation.Accessibility(Report(wcag: wcag))!;

        accessibility.Failed.Should().Be(1);
        accessibility.RequireManualReview.Should().Be(1);
        accessibility.NotAssessed.Should().BeGreaterThan(0, "criteria the profile covers but the run never reached");
        accessibility.ManualOnly.Should().BeGreaterThan(0, "manual-only describes how a criterion is assessed, not its result");
        // Manual-only is a property of the criterion, so it overlaps the others rather than adding to them.
        (accessibility.Failed + accessibility.RequireManualReview + accessibility.NotAssessed)
            .Should().BeLessThanOrEqualTo(accessibility.CriteriaInScope);
        accessibility.Statement.Should().Be("1 criterion has automated failure evidence. Manual assessment is still required.");
    }

    [Fact]
    public void ManualOnlyCriteriaAreNotCountedAsFailures()
    {
        var wcag = Assessment(WcagProfiles.NorwegianId, ("1.1.1", WcagStatus.Pass));

        var accessibility = FrontendQualityResultPresentation.Accessibility(Report(wcag: wcag))!;

        accessibility.ManualOnly.Should().BeGreaterThan(0);
        accessibility.Failed.Should().Be(0);
    }

    // A completed review that leaves work for a human says so at result level.
    [Fact]
    public void ManualReviewObligationsSurfaceInTheOverallResult()
    {
        var wcag = Assessment(WcagProfiles.NorwegianId, ("1.1.1", WcagStatus.ManualReviewRequired));

        FrontendQualityResultPresentation.Build(Report(wcag: wcag)).State
            .Should().Be(FrontendQualityResultState.CompletedWithManualReview);
    }

    // ── §6, §7, §33. Profile binding ──────────────────────────────────────────────────────────────────────────────

    // 1, 2, 3 (§43). The profile and its scope come from the assessment the run produced.
    [Theory]
    [InlineData(WcagProfiles.NorwegianId)]
    [InlineData(WcagProfiles.Wcag21AaId)]
    [InlineData(WcagProfiles.ExtendedId)]
    public void TheResultCarriesTheProfileItRanUnder_AndThatProfilesCriteriaCount(string profileId)
    {
        var view = FrontendQualityResultPresentation.Build(Report(wcag: Assessment(profileId, ("1.1.1", WcagStatus.Pass))));

        var profile = WcagProfiles.Resolve(profileId);
        view.ProfileLabel.Should().Be(profile.Label);
        view.CriteriaInScope.Should().Be(profile.CriteriaInScope);
        view.Accessibility!.CriteriaInScope.Should().Be(profile.CriteriaInScope);
    }

    // 8 (§43). The critical one: a later selection must not relabel a finished result.
    [Fact]
    public void ChangingTheSelectedProfileNeverRelabelsAFinishedResult()
    {
        var report = Report(wcag: Assessment(WcagProfiles.NorwegianId, ("1.1.1", WcagStatus.Pass)));

        var view = FrontendQualityResultPresentation.Build(report, currentProfileId: WcagProfiles.ExtendedId);

        view.ProfileLabel.Should().Be(WcagProfiles.Norwegian.Label, "the result keeps the profile it was produced under");
        view.CriteriaInScope.Should().Be(WcagProfiles.Norwegian.CriteriaInScope);
        view.ProfileLabel.Should().NotBe(WcagProfiles.Extended.Label);
    }

    // 9 (§43). The divergence is stated, and a rerun is what resolves it.
    [Fact]
    public void AChangedProfileAsksForANewRunInsteadOfMutatingTheOldResult()
    {
        var report = Report(wcag: Assessment(WcagProfiles.NorwegianId, ("1.1.1", WcagStatus.Pass)));

        var drift = FrontendQualityResultPresentation.Build(report, WcagProfiles.ExtendedId).ProfileDrift;

        drift.Should().Contain(WcagProfiles.Norwegian.Label)
            .And.Contain(WcagProfiles.Extended.Label)
            .And.Contain("Run the review again");
    }

    [Fact]
    public void NoDriftNoticeWhenTheSelectionStillMatchesTheRun()
    {
        var report = Report(wcag: Assessment(WcagProfiles.NorwegianId, ("1.1.1", WcagStatus.Pass)));

        FrontendQualityResultPresentation.Build(report, WcagProfiles.NorwegianId).ProfileDrift.Should().BeNull();
        FrontendQualityResultPresentation.Build(report).ProfileDrift.Should().BeNull("no selection was supplied");
    }

    // ── §35, §36. Metadata comes from the report ──────────────────────────────────────────────────────────────────

    [Fact]
    public void MetadataDescribesTheTargetTheReviewActuallyRanAgainst()
    {
        var view = FrontendQualityResultPresentation.Build(Report());

        view.Environment.Should().Be("M2LB DEV");
        view.EnvironmentType.Should().Be("Development");
        view.Url.Should().Be("https://m2lbdev.example.test/");
        view.CompletedAt.Should().NotBeNull();
    }

    [Fact]
    public void NoTimestampIsInventedForAReportThatCarriesNone()
    {
        var report = new FrontendQualityReviewReport { TargetUrl = "https://x.test/" };

        var view = FrontendQualityResultPresentation.Build(report);

        view.CompletedAt.Should().BeNull();
        view.ProfileLabel.Should().BeNull("no WCAG assessment ran, so no profile is claimed");
        view.Accessibility.Should().BeNull();
    }
}
