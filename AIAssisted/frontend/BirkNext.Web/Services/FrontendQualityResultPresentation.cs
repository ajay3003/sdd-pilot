using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

/// <summary>
/// Pure projection of a COMPLETED <see cref="FrontendQualityReviewReport"/> into the result view. Every value is read
/// from the report itself — the engine outcomes it recorded, the findings it produced and the WCAG assessment it ran —
/// so a result never changes because configuration changed afterwards.
///
/// Three rules this file exists to keep:
/// <list type="bullet">
/// <item>An execution problem is never rendered as a quality finding, and a quality finding never as an execution problem.</item>
/// <item>Absence of evidence is never a count. A domain nothing assessed has an unknown number of issues, not zero.</item>
/// <item>The accessibility profile shown belongs to the run, not to the current selection.</item>
/// </list>
/// </summary>
public static class FrontendQualityResultPresentation
{
    /// <summary>The order the result domains are read in: the statutory obligation first, then the rest by review weight.</summary>
    public static readonly IReadOnlyList<FrontendQualityCategory> DomainOrder =
    [
        FrontendQualityCategory.Accessibility,
        FrontendQualityCategory.Performance,
        FrontendQualityCategory.Security,
        FrontendQualityCategory.Standards,
        FrontendQualityCategory.BlazorWasm,
        FrontendQualityCategory.Readiness,
    ];

    /// <param name="currentProfileId">
    /// The profile currently selected for the NEXT run. Passed only to detect drift; it never labels this result.
    /// </param>
    public static FrontendQualityResultView Build(FrontendQualityReviewReport report, string? currentProfileId = null)
    {
        var accessibility = Accessibility(report);
        var domains = DomainOrder.Select(category => Domain(report, category, accessibility)).ToList();
        var state = State(report, domains, accessibility);

        return new FrontendQualityResultView(
            State: state,
            Summary: Summary(state, report, domains),
            Environment: report.TargetEnvironment?.Name ?? "Unnamed environment",
            EnvironmentType: report.TargetEnvironment?.EnvironmentType ?? "—",
            // The target the review actually ran against, as the report recorded it — not whatever is selected now.
            Url: report.TargetEnvironment?.TargetUrl is { Length: > 0 } recorded ? recorded
                : string.IsNullOrWhiteSpace(report.TargetUrl) ? "—" : report.TargetUrl,
            CompletedAt: report.CompletedAt ?? (report.GeneratedAt == default ? null : report.GeneratedAt),
            ProfileLabel: accessibility?.ProfileLabel,
            CriteriaInScope: accessibility?.CriteriaInScope,
            Domains: domains,
            Accessibility: accessibility,
            ProfileDrift: ProfileDrift(report, currentProfileId));
    }

    // ── Overall result ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Derived only from what the report records. Execution problems outrank quality statements: a review that could not
    /// run says so, instead of reporting an encouraging absence of findings.
    /// </summary>
    private static FrontendQualityResultState State(
        FrontendQualityReviewReport report,
        IReadOnlyList<FrontendQualityDomainResult> domains,
        FrontendQualityAccessibilityResult? accessibility)
    {
        if (!string.IsNullOrWhiteSpace(report.ErrorMessage))
            return FrontendQualityResultState.FailedToRun;

        if (report.PreflightStatus is PreflightStatus.AuthenticationRequired or PreflightStatus.Unreachable or PreflightStatus.TimedOut)
            return FrontendQualityResultState.Blocked;

        if (report.Coverage?.RequiredCoverageState == FrontendQualityRequiredCoverageState.NoTrustworthyRequiredAssessment)
            return FrontendQualityResultState.FailedToRun;

        if (report.Coverage?.RequiredCoverageState == FrontendQualityRequiredCoverageState.SomeRequiredNotAssessed)
            return FrontendQualityResultState.Incomplete;

        // A completed review that leaves work for a human says so before it says anything about limitations.
        if (report.ManualReviewItems.Count > 0 || accessibility is { RequireManualReview: > 0 } or { ManualOnly: > 0 })
            return FrontendQualityResultState.CompletedWithManualReview;

        return domains.Any(d => d.State is FrontendQualityDomainResultState.CompletedWithLimitedEvidence
                                    or FrontendQualityDomainResultState.NoEvidence
                                    or FrontendQualityDomainResultState.FailedToRun)
            ? FrontendQualityResultState.CompletedWithLimitations
            : FrontendQualityResultState.Completed;
    }

    private static string Summary(
        FrontendQualityResultState state,
        FrontendQualityReviewReport report,
        IReadOnlyList<FrontendQualityDomainResult> domains)
    {
        var reviewed = domains.Count(d => FrontendQualityDomainResultStates.CarriesFindings(d.State));
        var findings = domains.Where(d => d.FindingCount.HasValue).Sum(d => d.FindingCount!.Value);

        return state switch
        {
            FrontendQualityResultState.FailedToRun =>
                report.ErrorMessage ?? "No required evidence source produced a trustworthy assessment, so there is no result to report.",
            FrontendQualityResultState.Blocked =>
                report.PreflightMessage ?? "The target could not be reviewed, so no evidence was collected.",
            FrontendQualityResultState.Incomplete =>
                $"{reviewed} of {domains.Count} review domains produced evidence; required coverage is incomplete.",
            FrontendQualityResultState.CompletedWithManualReview =>
                $"{findings} finding{(findings == 1 ? "" : "s")} across {reviewed} of {domains.Count} review domains. Parts of this review can only be completed by a person.",
            FrontendQualityResultState.CompletedWithLimitations =>
                $"{findings} finding{(findings == 1 ? "" : "s")} across {reviewed} of {domains.Count} review domains; some evidence was unavailable.",
            _ => $"{findings} finding{(findings == 1 ? "" : "s")} across all {domains.Count} review domains.",
        };
    }

    // ── Per-domain result ─────────────────────────────────────────────────────────────────────────────────────────

    private static FrontendQualityDomainResult Domain(
        FrontendQualityReviewReport report,
        FrontendQualityCategory category,
        FrontendQualityAccessibilityResult? accessibility)
    {
        var engines = FrontendQualityCategoryEngines.For(category).ToHashSet();
        var outcomes = report.EngineOutcomes.Where(o => engines.Contains(o.EngineId)).ToList();
        // Only engines that actually took part in this run can be missing from it; a disabled engine is not a gap.
        var active = outcomes.Where(o => !FrontendQualityCoverage.IsInactive(o)).ToList();
        var assessed = active.Where(o => o.ExecutionState == FrontendQualityEngineExecutionState.Assessed).ToList();
        var errored = active.Where(o => o.ExecutionState is FrontendQualityEngineExecutionState.EngineError
            or FrontendQualityEngineExecutionState.TimedOut
            or FrontendQualityEngineExecutionState.Cancelled).ToList();

        // Accessibility also draws on the WCAG assessment, which can hold recorded manual reviews when no engine ran.
        var hasWcagEvidence = category == FrontendQualityCategory.Accessibility && accessibility is { WithEvidence: > 0 };

        var state =
            assessed.Count > 0 || hasWcagEvidence
                ? assessed.Count < active.Count ? FrontendQualityDomainResultState.CompletedWithLimitedEvidence
                                                : FrontendQualityDomainResultState.Completed
            : errored.Count > 0 ? FrontendQualityDomainResultState.FailedToRun
            : active.Count > 0 ? FrontendQualityDomainResultState.NoEvidence
            : FrontendQualityDomainResultState.NotAssessed;

        int? findingCount = FrontendQualityDomainResultStates.CarriesFindings(state)
            ? report.Findings.Count(f => f.Category == category)
            : null;

        return new FrontendQualityDomainResult(
            category,
            FrontendQualityCategoryEngines.Label(category),
            state,
            Summary(category, state, findingCount, accessibility),
            Limitation(state, active, assessed, errored),
            findingCount);
    }

    /// <summary>
    /// What this domain found, in result words. Never an engine name and never an engine's availability: an unavailable
    /// optional engine is a limitation on the evidence, not the domain's result.
    /// </summary>
    private static string Summary(
        FrontendQualityCategory category,
        FrontendQualityDomainResultState state,
        int? findingCount,
        FrontendQualityAccessibilityResult? accessibility)
    {
        if (category == FrontendQualityCategory.Accessibility && accessibility is not null
            && FrontendQualityDomainResultStates.CarriesFindings(state))
            return accessibility.Statement;

        if (state == FrontendQualityDomainResultState.NotAssessed)
            return "This area was not assessed in this run.";

        if (state == FrontendQualityDomainResultState.NoEvidence)
            return "No evidence is available for this domain, so nothing can be concluded about it.";

        if (state == FrontendQualityDomainResultState.FailedToRun)
            return "An evidence source for this domain did not complete, so the domain was not reviewed.";

        var found = findingCount switch
        {
            0 => "No findings were recorded in the evidence reviewed",
            1 => "1 finding was recorded",
            _ => $"{findingCount} findings were recorded",
        };

        return category switch
        {
            FrontendQualityCategory.Performance => $"{found} for performance.",
            FrontendQualityCategory.Security => $"{found} for security. Passive and static review cannot establish that an application is secure.",
            FrontendQualityCategory.Standards => $"{found} for standards compliance, derived from the checks this review ran.",
            FrontendQualityCategory.BlazorWasm => $"{found} for the Blazor/WASM delivery of this frontend.",
            FrontendQualityCategory.Readiness => $"{found}. QA readiness is derived from the evidence the other domains produced.",
            _ => $"{found}.",
        };
    }

    /// <summary>One line about what was missing, in evidence words. Null when nothing was.</summary>
    private static string? Limitation(
        FrontendQualityDomainResultState state,
        IReadOnlyList<FrontendQualityEngineOutcome> active,
        IReadOnlyList<FrontendQualityEngineOutcome> assessed,
        IReadOnlyList<FrontendQualityEngineOutcome> errored)
    {
        if (state is FrontendQualityDomainResultState.NotAssessed) return null;

        if (state is FrontendQualityDomainResultState.FailedToRun)
            return $"{errored.Count} evidence source{(errored.Count == 1 ? "" : "s")} did not complete. This is an execution problem, not a review finding.";

        var missing = active.Count - assessed.Count;
        if (missing <= 0) return null;

        // Browser-dependent evidence is the common case and worth naming as a kind, never as a product.
        var browserOnly = active.Except(assessed).All(o => o.OutcomeReason is
            FrontendQualityEngineOutcomeReason.BrowserCompanionNotConnected or
            FrontendQualityEngineOutcomeReason.BrowserCompanionNoEvidence or
            FrontendQualityEngineOutcomeReason.PerformanceEvidenceUnavailable or
            FrontendQualityEngineOutcomeReason.SessionUnavailable or
            FrontendQualityEngineOutcomeReason.BrowserDomUnavailableForMethod);

        return browserOnly
            ? "Browser evidence was unavailable, so this domain was reviewed on its static evidence only."
            : $"{missing} evidence source{(missing == 1 ? "" : "s")} did not contribute to this domain.";
    }

    // ── Accessibility ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Counts per WCAG status, from the assessment THIS run produced. Failed, require-manual-review, manual-only and
    /// not-assessed stay four separate numbers: merging any two of them would turn an unknown into a verdict.
    /// </summary>
    public static FrontendQualityAccessibilityResult? Accessibility(FrontendQualityReviewReport report)
    {
        if (report.Wcag is not { } assessment) return null;

        var criteria = WcagAssessmentSummary.Criteria(assessment);
        return new FrontendQualityAccessibilityResult(
            ProfileLabel: assessment.TargetLabel,
            CriteriaInScope: criteria.Count,
            Failed: criteria.Count(c => c.Status == WcagStatus.Fail),
            RequireManualReview: criteria.Count(c => c.Status == WcagStatus.ManualReviewRequired),
            ManualOnly: criteria.Count(c => c.Definition.AutomationLevel == WcagAutomation.Manual),
            NotAssessed: criteria.Count(c => c.Status == WcagStatus.NotTested),
            WithEvidence: criteria.Count(c => c.EvidenceCount > 0));
    }

    /// <summary>
    /// The selected profile has moved on from the one this result ran under. The result is not relabelled: it keeps the
    /// profile it was produced with, and the user is told a new run is what updates it.
    /// </summary>
    public static string? ProfileDrift(FrontendQualityReviewReport report, string? currentProfileId)
    {
        if (report.Wcag?.Profile is not { } used || string.IsNullOrWhiteSpace(currentProfileId)) return null;
        if (used.ProfileId == currentProfileId) return null;

        return $"This result was produced under {used.Label}. " +
               $"The accessibility profile has since been changed to {WcagProfiles.Resolve(currentProfileId).Label}. " +
               "Run the review again to assess the target against the new profile.";
    }
}
