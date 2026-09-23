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
        // Grouping is deterministic over the findings, so a report that reached this view without its issues populated
        // still gets the same answer rather than reporting that nothing survived. The orchestrator's own grouping is
        // preferred when present, because it is what the run recorded.
        var issues = report.LogicalIssues.Count > 0 || report.Findings.Count == 0
            ? report.LogicalIssues
            : FrontendQualityLogicalIssueGrouper.Group(report.Findings);
        var domains = DomainOrder.Select(category => Domain(report, category, accessibility, issues)).ToList();
        var state = State(report, domains, accessibility);
        var completeness = Completeness(report, state, accessibility);

        // Source observations exclude derived conclusions: QA Readiness restates risks the performance evidence already
        // produced, and counting both reported the same problem twice in the headline.
        var sourceFindings = report.Findings.Count(f => f.Origin == FrontendQualityFindingOrigin.Source);
        var derived = report.Findings.Count(f => f.Origin == FrontendQualityFindingOrigin.Derived);

        return new FrontendQualityResultView(
            State: state,
            Summary: Summary(state, report, domains, issues),
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
            ProfileDrift: ProfileDrift(report, currentProfileId),
            Completeness: completeness,
            Release: Release(report, completeness, issues, accessibility),
            SourceFindingCount: sourceFindings,
            LogicalIssueCount: issues.Count(issue => issue.IsActionable),
            CriticalHighSourceCount: report.Findings.Count(f =>
                f.Origin == FrontendQualityFindingOrigin.Source &&
                f.Severity is FrontendQualitySeverity.Critical or FrontendQualitySeverity.High),
            DerivedIndicatorCount: derived,
            InformationalIssueCount: issues.Count(issue => issue.Informational));
    }

    // ── Completeness, on its own dimensions ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Four independent answers. "Assessment completeness: Full" was read off required coverage alone, so a review with
    /// three optional engines that never ran and an outstanding manual accessibility assessment called itself complete.
    /// </summary>
    public static FrontendQualityCompleteness Completeness(
        FrontendQualityReviewReport report,
        FrontendQualityResultState state,
        FrontendQualityAccessibilityResult? accessibility)
    {
        var coverage = report.Coverage ?? FrontendQualityCoverage.Evaluate(report.EngineOutcomes);
        var optional =
            coverage.OptionalTotal == 0 ? FrontendQualityOptionalCoverageState.NotApplicable
            : coverage.OptionalAssessed == coverage.OptionalTotal ? FrontendQualityOptionalCoverageState.Complete
            : coverage.OptionalAssessed == 0 ? FrontendQualityOptionalCoverageState.None
            : FrontendQualityOptionalCoverageState.Partial;

        var manual = report.ManualReviewItems.Count > 0 || accessibility is { RequireManualAssessment: > 0 }
            ? FrontendQualityManualAssessmentState.Required
            : FrontendQualityManualAssessmentState.NotRequired;

        return new FrontendQualityCompleteness(
            state, coverage.RequiredCoverageState, optional, manual,
            coverage.RequiredAssessed, coverage.RequiredTotal, coverage.OptionalAssessed, coverage.OptionalTotal);
    }

    // ── Release decision ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The decision, with its reasons in the reader's terms. Reasons count LOGICAL ISSUES: the number of times a problem
    /// was observed says how much evidence there is, not how serious the situation is, and using occurrences made a
    /// single contrast defect on five routes look like five reasons not to release.
    /// </summary>
    public static FrontendQualityReleaseDecision Release(
        FrontendQualityReviewReport report,
        FrontendQualityCompleteness completeness,
        IReadOnlyList<FrontendQualityLogicalIssue> issues,
        FrontendQualityAccessibilityResult? accessibility)
    {
        var disposition = report.ReleaseDisposition ?? FrontendQualityReleaseDisposition.ReviewRequired;
        var criticalHigh = issues.Count(issue => issue.IsActionable &&
            issue.PrimarySeverity is FrontendQualitySeverity.Critical or FrontendQualitySeverity.High);

        var reasons = new List<string>();
        if (completeness.RequiredCoverage != FrontendQualityRequiredCoverageState.AllRequiredAssessed)
            reasons.Add($"{completeness.RequiredAssessed} of {completeness.RequiredTotal} required engines completed.");
        if (criticalHigh > 0)
            reasons.Add($"{criticalHigh} critical or high logical issue{(criticalHigh == 1 ? "" : "s")} to resolve.");
        if (accessibility is { RequireManualAssessment: > 0 } a)
            reasons.Add($"{a.RequireManualAssessment} WCAG criteria still require manual assessment.");
        if (completeness.OptionalCoverage is FrontendQualityOptionalCoverageState.Partial or FrontendQualityOptionalCoverageState.None)
            reasons.Add($"Optional evidence is incomplete ({completeness.OptionalAssessed} of {completeness.OptionalTotal} optional engines completed).");

        var (label, statement) = disposition switch
        {
            FrontendQualityReleaseDisposition.Blocked => ("Blocked",
                "A configured release-blocking condition applies, or a required engine could not assess the target."),
            // Deliberately not "ready" or "approved": the review has no basis for either.
            FrontendQualityReleaseDisposition.NoAutomatedBlockDetected => ("No automated block detected",
                "This review's automated evidence found nothing that blocks a release. It is not a release approval."),
            _ => ("Review required",
                "This review's automated evidence has to be read by a person before a release decision can be made."),
        };

        return new FrontendQualityReleaseDecision(disposition, label, statement, reasons);
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

        // Every required engine being blocked is incomplete required COVERAGE, not a failed run. Returning FailedToRun
        // here made the page announce "there is no result to report" over a review that had produced accessibility,
        // performance, standards and Blazor findings from the optional engines that did complete. Execution failure is
        // ErrorMessage; how much of the required scope was covered is a separate fact, and so is whether the result can
        // support a release.
        if (report.Coverage?.RequiredCoverageState == FrontendQualityRequiredCoverageState.NoTrustworthyRequiredAssessment)
            return FrontendQualityResultState.Incomplete;

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

    /// <summary>
    /// The one sentence under the result state. It counts PROBLEMS, because that is the question a reader is asking —
    /// and it says "logical issues", never a bare "N findings". "57 findings" counted every observation, including
    /// three QA Readiness items restating risks the same review had already reported, and read as 57 things to fix.
    /// </summary>
    private static string Summary(
        FrontendQualityResultState state,
        FrontendQualityReviewReport report,
        IReadOnlyList<FrontendQualityDomainResult> domains,
        IReadOnlyList<FrontendQualityLogicalIssue> issues)
    {
        var reviewed = domains.Count(d => FrontendQualityDomainResultStates.CarriesFindings(d.State));
        var actionable = issues.Count(issue => issue.IsActionable);
        var observations = report.Findings.Count(f => f.Origin == FrontendQualityFindingOrigin.Source);

        var scale = actionable == 0
            ? "No logical issue was identified in the evidence reviewed"
            : $"{actionable} logical issue{(actionable == 1 ? "" : "s")} from {observations} source finding{(observations == 1 ? "" : "s")}, across {reviewed} of {domains.Count} review domains";

        return state switch
        {
            FrontendQualityResultState.FailedToRun =>
                report.ErrorMessage ?? "No required evidence source produced a trustworthy assessment, so there is no result to report.",
            FrontendQualityResultState.Blocked =>
                report.PreflightMessage ?? "The target could not be reviewed, so no evidence was collected.",
            // Says what is missing AND what survived, so partial evidence is never presented as nothing.
            FrontendQualityResultState.Incomplete =>
                $"{report.Coverage?.RequiredAssessed ?? 0} of {report.Coverage?.RequiredTotal ?? 0} required engines completed. "
                + (actionable > 0 ? $"Available evidence produced {scale.ToLowerInvariant()}." : "No other evidence source produced findings."),
            FrontendQualityResultState.CompletedWithManualReview =>
                $"{scale}. Parts of this review can only be completed by a person.",
            FrontendQualityResultState.CompletedWithLimitations =>
                $"{scale}; some evidence was unavailable.",
            _ => $"{scale}.",
        };
    }

    // ── Per-domain result ─────────────────────────────────────────────────────────────────────────────────────────

    private static FrontendQualityDomainResult Domain(
        FrontendQualityReviewReport report,
        FrontendQualityCategory category,
        FrontendQualityAccessibilityResult? accessibility,
        IReadOnlyList<FrontendQualityLogicalIssue> issues)
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

        // Findings this run actually recorded for the domain. A domain that HAS findings has evidence, whatever its own
        // engines reported: Security said "No evidence is available for this domain" while the same page listed missing
        // CSP, HSTS, X-Content-Type-Options, Permissions-Policy and Referrer-Policy findings under that very category.
        // Suppressing the count because the engine outcome said so made the card contradict the findings table.
        var categoryFindings = report.Findings.Count(f => f.Category == category);
        // Source and derived, apart: a derived indicator filed under a domain is not one of its source findings.
        var categorySource = report.Findings.Count(f => f.Category == category && f.Origin == FrontendQualityFindingOrigin.Source);
        var categoryDerived = categoryFindings - categorySource;
        // Source findings of THIS domain that belong to logical issues owned by another primary domain (a missing
        // response header observed by a standards check, owned by Security). From the grouping itself, never a title match.
        var contributed = issues.Where(issue => issue.Category != category)
            .SelectMany(issue => issue.FindingInstances.Where(i => i.Category == category).Select(_ => issue.Category))
            .ToList();
        // QA Readiness draws its items from evidence the other domains already reported. They are indicators, not new
        // observations, and the domain says so rather than presenting them as findings of its own.
        var derived = categoryFindings > 0 && report.Findings.Where(f => f.Category == category)
            .All(f => f.Origin == FrontendQualityFindingOrigin.Derived);
        // Distinct problems whose PRIMARY domain is this one. An issue two domains both observe (a missing response
        // header) counts once, where it is owned; the other domain keeps its source observations and lists it as related.
        var categoryIssues = issues.Count(issue => issue.Category == category && issue.IsActionable);

        var state =
            assessed.Count > 0 || hasWcagEvidence
                ? assessed.Count < active.Count ? FrontendQualityDomainResultState.CompletedWithLimitedEvidence
                                                : FrontendQualityDomainResultState.Completed
            // No engine of this domain completed, yet findings for it exist. They came from evidence this run collected,
            // so the domain was reviewed — with less than its own engines would have given it.
            : categoryFindings > 0 ? FrontendQualityDomainResultState.CompletedWithLimitedEvidence
            : errored.Count > 0 ? FrontendQualityDomainResultState.FailedToRun
            : active.Count > 0 ? FrontendQualityDomainResultState.NoEvidence
            : FrontendQualityDomainResultState.NotAssessed;

        var carries = FrontendQualityDomainResultStates.CarriesFindings(state);
        // A derived domain counts its indicators; every other domain counts its SOURCE findings only.
        int? findingCount = carries ? (derived ? categoryDerived : categorySource) : null;

        return new FrontendQualityDomainResult(
            category,
            FrontendQualityCategoryEngines.Label(category),
            state,
            Summary(category, state, findingCount, accessibility),
            Limitation(category, state, active, assessed, errored, categoryFindings),
            findingCount,
            LogicalIssueCount: findingCount is null || derived ? null : categoryIssues,
            Derived: derived,
            ManualAssessmentRequired: category == FrontendQualityCategory.Accessibility && accessibility is { RequireManualAssessment: > 0 },
            DerivedIndicatorCount: carries && !derived ? categoryDerived : 0,
            ContributedToOtherDomains: carries ? contributed.Count : 0,
            ContributedDomainLabels: contributed.Distinct().Select(FrontendQualityCategoryEngines.Label).ToList());
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
            return "Not assessed in this run.";

        // "No evidence" reads as a data failure and, for Security, as a claim that nothing was found anywhere. What is
        // actually missing is this domain's own engine; findings that belong to other domains are unaffected.
        if (state == FrontendQualityDomainResultState.NoEvidence)
            return "No dedicated assessment evidence for this domain.";

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
            // Never "findings": these restate what the other domains already reported, and calling them findings is
            // what let three indicators add three problems to a total that already contained them.
            FrontendQualityCategory.Readiness => findingCount switch
            {
                0 => "No readiness indicator was derived from the evidence this review collected.",
                1 => "1 readiness indicator, derived from evidence the other domains produced.",
                _ => $"{findingCount} readiness indicators, derived from evidence the other domains produced.",
            },
            _ => $"{found}.",
        };
    }

    /// <summary>
    /// What was missing, BY NAME. "1 evidence source did not contribute to this domain" is true and sends the reader to
    /// the engine matrix to work out which one; the engine that did not run is known here, so it is said here.
    ///
    /// The other half is the contradiction this also resolves: a domain can hold findings while its own dedicated engine
    /// shows "not assessed", because another engine's evidence covered it. Left unsaid, the result and the engine matrix
    /// appear to disagree.
    /// </summary>
    private static string? Limitation(
        FrontendQualityCategory category,
        FrontendQualityDomainResultState state,
        IReadOnlyList<FrontendQualityEngineOutcome> active,
        IReadOnlyList<FrontendQualityEngineOutcome> assessed,
        IReadOnlyList<FrontendQualityEngineOutcome> errored,
        int categoryFindings)
    {
        if (state is FrontendQualityDomainResultState.NotAssessed) return null;

        if (state is FrontendQualityDomainResultState.FailedToRun)
            return $"{Names(errored)} did not complete. This is an execution problem, not a review finding.";

        var missing = active.Except(assessed).ToList();
        if (missing.Count == 0) return null;

        var contributed = assessed.Count > 0 ? Names(assessed) : null;

        // The dedicated engine of a domain is the one whose absence needs explaining, because the domain is named after
        // it: Accessibility findings from Browser Quality while the Accessibility engine is blocked reads as a fault
        // until someone says where the evidence came from.
        var dedicated = DedicatedEngine(category);
        if (dedicated is { } id && missing.Any(o => o.EngineId == id) && contributed is not null && categoryFindings > 0)
            return $"{Name(missing.First(o => o.EngineId == id))} did not run; {contributed} evidence contributed to this domain instead.";

        return $"{Names(missing)} did not contribute to this domain.";
    }

    /// <summary>The engine a domain is named after, where it has one. Its absence is the limitation worth naming first.</summary>
    private static FrontendQualityEngineId? DedicatedEngine(FrontendQualityCategory category) => category switch
    {
        FrontendQualityCategory.Accessibility => FrontendQualityEngineId.Accessibility,
        FrontendQualityCategory.Security => FrontendQualityEngineId.PassiveSecurity,
        _ => null,
    };

    /// <summary>
    /// The engine's own display name, or a readable rendering of its id when the outcome carries none. "BrowserRuntime"
    /// is an identifier; a sentence a person reads says "Browser Runtime".
    /// </summary>
    private static string Name(FrontendQualityEngineOutcome outcome) =>
        string.IsNullOrWhiteSpace(outcome.DisplayName) ? Spaced(outcome.EngineId.ToString()) : outcome.DisplayName;

    private static string Spaced(string identifier) =>
        string.Concat(identifier.Select((ch, i) => i > 0 && char.IsUpper(ch) ? " " + ch : ch.ToString()));

    private static string Names(IReadOnlyList<FrontendQualityEngineOutcome> outcomes)
    {
        var names = outcomes.Select(Name).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        return names.Count switch
        {
            0 => "An evidence source",
            1 => names[0],
            _ => string.Join(" and ", string.Join(", ", names.Take(names.Count - 1)), names[^1]),
        };
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
            WithEvidence: criteria.Count(c => c.EvidenceCount > 0),
            // ONE primary manual number, as a union: a criterion that is manual-only AND carries an explicit
            // review-required result is one criterion for a person to assess, not two. Showing "5 require manual
            // review" beside "10 of 48 manual assessment required" made them read as competing totals.
            RequireManualAssessment: criteria.Count(c =>
                c.Definition.AutomationLevel == WcagAutomation.Manual || c.Status == WcagStatus.ManualReviewRequired),
            // The subset with an explicit result, kept as detail behind the primary number.
            ExplicitReviewRequired: criteria.Count(c => c.Status == WcagStatus.ManualReviewRequired));
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
