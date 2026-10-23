using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

/// <summary>Did the planned automation run? Read from REQUIRED engines only; an optional gap is coverage, never execution.</summary>
public enum FrontendQualityAutomationState { Complete, Partial, Blocked, Failed, NotStarted }

/// <summary>How much of what the scope asked for was actually assessed. Independent of findings and of the decision.</summary>
public enum FrontendQualityCoverageLevel { Complete, Partial, NotAssessed }

/// <summary>One surface of the review: assessed, partly, not (with the stored reason), or deliberately outside the chosen scope.</summary>
public enum FrontendQualitySurfaceCoverageState { Assessed, PartiallyAssessed, NotAssessed, NotInSelectedScope }

public sealed record FrontendQualitySurfaceCoverage(
    FrontendQualityReviewSurface Surface,
    string Label,
    FrontendQualitySurfaceCoverageState State,
    string? Reason)
{
    public string StateLabel => State switch
    {
        FrontendQualitySurfaceCoverageState.Assessed => "Assessed",
        FrontendQualitySurfaceCoverageState.PartiallyAssessed => "Partially assessed",
        FrontendQualitySurfaceCoverageState.NotInSelectedScope => "Not in selected scope",
        _ => "Not assessed",
    };
}

/// <summary>
/// The one post-run model every result view reads — Summary, Coverage, History and the export — so they cannot disagree:
/// automated execution, coverage, findings, manual assessment and the decision, each its own answer. Derived from the stored report
/// alone: an opened history run shows exactly what was recorded then, and a later capability improvement never upgrades it.
/// </summary>
public sealed record FrontendQualityRunSemantics(
    FrontendQualityAutomationState Automation,
    int RequiredAssessed,
    int RequiredTotal,
    int OptionalAssessed,
    int OptionalTotal,
    IReadOnlyList<string> OptionalNotCompleted,
    FrontendReviewAccessScope? SelectedScope,
    FrontendQualityCoverageLevel Coverage,
    IReadOnlyList<string> CoverageReasons,
    IReadOnlyList<FrontendQualitySurfaceCoverage> Surfaces,
    int LogicalIssues,
    int CriticalHighIssues,
    int SourceFindings,
    int DerivedIndicators,
    int ManualWcagCriteria,
    int OtherManualItems,
    string DecisionLabel,
    IReadOnlyList<string> DecisionReasons,
    FrontendQualityAccessibilityResult? Accessibility = null)
{
    /// <summary>"Norwegian public-sector requirements — WCAG 2.1 · 48 criteria · automated evidence 35 · manual 15 · issues 6 criteria".</summary>
    public string? AccessibilityDetail => Accessibility is not { } a ? null
        : $"{a.CriteriaInScope} criteria · automated evidence available for {a.WithEvidence} (not a pass count) · manual assessment {a.RequireManualAssessment} · detected issues in {a.WithFailureEvidence}";

    public bool ManualAssessmentRequired => ManualWcagCriteria > 0 || OtherManualItems > 0;
    public int RecordedObservations => SourceFindings + DerivedIndicators;

    public string AutomationLabel => Automation switch
    {
        FrontendQualityAutomationState.Complete => "Automation complete",
        FrontendQualityAutomationState.Partial => "Automation partial",
        FrontendQualityAutomationState.Blocked => "Automation blocked",
        FrontendQualityAutomationState.Failed => "Automation failed",
        _ => "Automation not started",
    };

    public string AutomationDetail => RequiredTotal == 0
        ? $"No required engine · {OptionalAssessed} of {OptionalTotal} optional engines completed"
        : $"{RequiredAssessed} of {RequiredTotal} required engines completed"
          + (OptionalTotal > 0 ? $" · {OptionalAssessed} of {OptionalTotal} optional engines completed (optional engines do not block)" : "");

    public string CoverageLabel => Coverage switch
    {
        FrontendQualityCoverageLevel.Complete => "Coverage complete",
        FrontendQualityCoverageLevel.Partial => "Coverage partial",
        _ => "Coverage not assessed",
    };

    public string FindingsLabel => LogicalIssues == 0 ? "No logical issues identified" : "Issues identified";

    /// <summary>"27 logical issues · 7 critical/high".</summary>
    public string FindingsDetail => LogicalIssues == 0
        ? "No logical issue was identified in the evidence reviewed"
        : $"{LogicalIssues} logical issue{(LogicalIssues == 1 ? "" : "s")} · {CriticalHighIssues} critical/high";

    /// <summary>How the counts relate, stated once so nobody adds them up.</summary>
    public string CountRelationship =>
        $"{SourceFindings} source finding{(SourceFindings == 1 ? "" : "s")}"
        + (DerivedIndicators > 0 ? $" and {DerivedIndicators} derived indicator{(DerivedIndicators == 1 ? "" : "s")} were recorded ({RecordedObservations} observations)" : " were recorded")
        + $"; related evidence was grouped into {LogicalIssues} logical issue{(LogicalIssues == 1 ? "" : "s")}.";

    public string ManualLabel => ManualAssessmentRequired ? "Manual assessment required" : "No manual assessment required";

    public string ManualDetail => string.Join(" · ", new[]
    {
        ManualWcagCriteria > 0 ? $"Accessibility: {ManualWcagCriteria} WCAG criteri{(ManualWcagCriteria == 1 ? "on" : "a")}" : null,
        OtherManualItems > 0 ? $"Other manual review: {OtherManualItems} item{(OtherManualItems == 1 ? "" : "s")}" : null,
    }.Where(p => p is not null));

    /// <summary>One sentence a reader can act on. "Complete" is only said when the REQUIRED automation completed.</summary>
    public string Headline => Automation switch
    {
        FrontendQualityAutomationState.Complete when ManualAssessmentRequired => "Automated review complete — manual assessment required",
        FrontendQualityAutomationState.Complete when Coverage != FrontendQualityCoverageLevel.Complete => "Automated review complete — coverage partial, review required",
        FrontendQualityAutomationState.Complete when LogicalIssues > 0 => "Automated review complete — issues identified, review required",
        FrontendQualityAutomationState.Complete => "Automated review complete — no automated issues detected",
        FrontendQualityAutomationState.Failed => "Automated review failed to run",
        FrontendQualityAutomationState.Blocked => "Automated review blocked",
        FrontendQualityAutomationState.NotStarted => "Automated review not started",
        _ => $"Automated review incomplete — {RequiredAssessed} of {RequiredTotal} required engines completed",
    };
}

public static class FrontendQualityRunSemanticsProjection
{
    public static FrontendQualityRunSemantics From(FrontendQualityReviewReport report) => From(report, FrontendQualityResultPresentation.Build(report));

    public static FrontendQualityRunSemantics From(FrontendQualityReviewReport report, FrontendQualityResultView view)
    {
        var completeness = view.Completeness;
        var requiredAssessed = completeness?.RequiredAssessed ?? report.Coverage?.RequiredAssessed ?? 0;
        var requiredTotal = completeness?.RequiredTotal ?? report.Coverage?.RequiredTotal ?? 0;
        var optionalAssessed = completeness?.OptionalAssessed ?? 0;
        var optionalTotal = completeness?.OptionalTotal ?? 0;

        var automation = view.State switch
        {
            FrontendQualityResultState.FailedToRun => FrontendQualityAutomationState.Failed,
            FrontendQualityResultState.Blocked => FrontendQualityAutomationState.Blocked,
            _ when requiredTotal > 0 && requiredAssessed == 0 => FrontendQualityAutomationState.Blocked,
            _ when requiredTotal > 0 && requiredAssessed < requiredTotal => FrontendQualityAutomationState.Partial,
            _ when report.EngineOutcomes.Count == 0 && report.CoveragePasses.Count == 0 => FrontendQualityAutomationState.NotStarted,
            _ => FrontendQualityAutomationState.Complete,
        };

        var optionalMissing = FrontendQualityPostRunPresentation.EngineRows(report)
            .Where(r => r.Policy == "Optional" && r.ReleaseImpact != FrontendQualityReleaseImpact.NotApplicable && !IsCompleted(r.Result))
            .Select(r => r.Engine).ToList();

        var scope = report.ExecutionPlan?.Scope ?? report.TargetAccess?.ReviewScope;
        var surfaces = Surfaces(report, scope);

        var coverageReasons = new List<string>();
        foreach (var surface in surfaces.Where(s => s.State is FrontendQualitySurfaceCoverageState.NotAssessed or FrontendQualitySurfaceCoverageState.PartiallyAssessed))
            coverageReasons.Add($"{surface.Label} surface {surface.StateLabel.ToLowerInvariant()}" + (surface.Reason is { Length: > 0 } why ? $": {why}" : ""));
        if (optionalMissing.Count > 0)
            coverageReasons.Add($"Optional automation: {optionalMissing.Count} of {optionalTotal} optional engine{(optionalTotal == 1 ? "" : "s")} did not complete ({string.Join(", ", optionalMissing)})");
        foreach (var domain in view.Domains.Where(d => !d.Derived && d.State is FrontendQualityDomainResultState.CompletedWithLimitedEvidence or FrontendQualityDomainResultState.NoEvidence))
            coverageReasons.Add($"{domain.Title}: {FrontendQualityDomainResultStates.Label(domain.State).ToLowerInvariant()}" + (domain.Limitation is { Length: > 0 } l ? $" — {l}" : ""));

        foreach (var domain in view.Domains.Where(d => !d.Derived && d.State == FrontendQualityDomainResultState.FailedToRun))
            coverageReasons.Add($"{domain.Title}: an evidence source did not complete");
        if (automation == FrontendQualityAutomationState.Partial)
            coverageReasons.Insert(0, $"Required automation incomplete: {requiredAssessed} of {requiredTotal} required engines completed");

        // Coverage can only be complete when the automation that produces it completed; nothing run is nothing covered.
        var coverage = automation is FrontendQualityAutomationState.Failed or FrontendQualityAutomationState.Blocked or FrontendQualityAutomationState.NotStarted
                       || surfaces.Count > 0 && surfaces.All(s => s.State is FrontendQualitySurfaceCoverageState.NotAssessed or FrontendQualitySurfaceCoverageState.NotInSelectedScope)
            ? FrontendQualityCoverageLevel.NotAssessed
            : coverageReasons.Count == 0 ? FrontendQualityCoverageLevel.Complete : FrontendQualityCoverageLevel.Partial;

        var actionable = view.ActionableIssues;
        var criticalHigh = actionable.Count(i => i.PrimarySeverity is FrontendQualitySeverity.Critical or FrontendQualitySeverity.High);
        var manualWcag = view.Accessibility?.RequireManualAssessment ?? 0;

        // Why a person still has to decide — from the actual state, never inferred from severity alone.
        var decisionReasons = new List<string>();
        if (report.ReleaseBlockers.Count > 0)
            decisionReasons.Add($"Release policy: {FrontendQualityPostRunPresentation.PrimaryBlocker(report)}");
        if (criticalHigh > 0)
            decisionReasons.Add($"{criticalHigh} critical/high logical issue{(criticalHigh == 1 ? "" : "s")} need{(criticalHigh == 1 ? "s" : "")} human review (severity is not an automatic release blocker)");
        if (manualWcag > 0)
            decisionReasons.Add($"{manualWcag} WCAG criteri{(manualWcag == 1 ? "on" : "a")} in the selected profile require manual assessment");
        if (report.ManualReviewItems.Count > 0)
            decisionReasons.Add($"{report.ManualReviewItems.Count} other manual review item{(report.ManualReviewItems.Count == 1 ? "" : "s")}");
        if (coverage != FrontendQualityCoverageLevel.Complete)
            decisionReasons.Add((coverage == FrontendQualityCoverageLevel.Partial ? "Coverage is partial: " : "Coverage not assessed: ") + string.Join("; ", coverageReasons.Take(2)) + (coverageReasons.Count > 2 ? "; …" : ""));

        return new FrontendQualityRunSemantics(
            automation, requiredAssessed, requiredTotal, optionalAssessed, optionalTotal, optionalMissing,
            scope, coverage, coverageReasons, surfaces,
            actionable.Count, criticalHigh, view.SourceFindingCount, view.DerivedIndicatorCount,
            manualWcag, report.ManualReviewItems.Count,
            view.Release?.Label ?? FrontendQualityReviewHistory.DispositionLabel(report.ReleaseDisposition), decisionReasons, view.Accessibility);
    }

    private static bool IsCompleted(string result) =>
        result.StartsWith("Completed", StringComparison.OrdinalIgnoreCase) || result.StartsWith("Assessed", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Public and authenticated surfaces, from the stored surface results and plan. A surface outside the chosen scope is "Not in selected
    /// scope", never "Not assessed". An authenticated surface whose browser pass did not complete but whose authenticated API checks did
    /// execute is "Partially assessed". Reasons are the stored ones; none is invented.
    /// </summary>
    public static IReadOnlyList<FrontendQualitySurfaceCoverage> Surfaces(FrontendQualityReviewReport report, FrontendReviewAccessScope? scope)
    {
        if (scope is null) return [];
        var recorded = FrontendQualityPostRunPresentation.SurfaceStatuses(report).ToDictionary(s => s.Surface);
        var result = new List<FrontendQualitySurfaceCoverage>();
        foreach (var surface in new[] { FrontendQualityReviewSurface.Public, FrontendQualityReviewSurface.Authenticated })
        {
            var label = FrontendQualityExecutionPlanner.SurfaceLabel(surface);
            if (!FrontendQualityExecutionPlanner.ScopeSurfaces(scope.Value).Contains(surface))
            {
                result.Add(new(surface, label, FrontendQualitySurfaceCoverageState.NotInSelectedScope,
                    $"The run's scope was {FrontendQualityReviewScopes.Label(scope.Value)}."));
                continue;
            }
            if (!recorded.TryGetValue(surface, out var status))
            {
                result.Add(new(surface, label, FrontendQualitySurfaceCoverageState.NotAssessed, "No surface result was recorded for this run."));
                continue;
            }
            var apiExecuted = surface == FrontendQualityReviewSurface.Authenticated && report.AuthenticatedApiSurface is { ExecutedCount: > 0 };
            var state = status.Status switch
            {
                "Completed" or "Required engines completed" => FrontendQualitySurfaceCoverageState.Assessed,
                "Partially completed" => FrontendQualitySurfaceCoverageState.PartiallyAssessed,
                _ when apiExecuted => FrontendQualitySurfaceCoverageState.PartiallyAssessed,
                _ => FrontendQualitySurfaceCoverageState.NotAssessed,
            };
            var reason = state == FrontendQualitySurfaceCoverageState.PartiallyAssessed && apiExecuted && status.Status == "Not assessed"
                ? $"Authenticated API checks executed ({report.AuthenticatedApiSurface!.ExecutedCount}); signed-in pages were not assessed"
                  + (status.Detail is { Length: > 0 } d ? $" — {d}" : ".")
                // A not-assessed surface states the plan's stored reason (e.g. "authenticated browser DOM unavailable") before the
                // generic "no engine could run here", which said that nothing happened but never why.
                : state == FrontendQualitySurfaceCoverageState.NotAssessed && NotAssessedReason(report, surface) is { } planned ? planned
                : status.Detail is { Length: > 0 } detail ? detail : NotAssessedReason(report, surface);
            result.Add(new(surface, label, state, reason));
        }
        return result;
    }

    /// <summary>The plan's own reason an engine could not run on the surface, when the surface result carries none.</summary>
    private static string? NotAssessedReason(FrontendQualityReviewReport report, FrontendQualityReviewSurface surface) =>
        report.ExecutionPlan?.Engines.Select(e => e.For(surface)).FirstOrDefault(p => p is { Decision: not FrontendQualityPlanDecision.Run } && !string.IsNullOrWhiteSpace(p.Reason))?.Reason;
}
