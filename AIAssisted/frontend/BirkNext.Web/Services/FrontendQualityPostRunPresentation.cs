using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

/// <summary>The five result-level views of a finished Frontend Quality Review.</summary>
public enum FrontendQualityResultTab { Summary, Issues, Coverage, Evidence, History }

/// <summary>One surface of the review scope and how it ended, e.g. "Public · Completed (4 of 4 engines)".</summary>
public sealed record FrontendQualitySurfaceStatus(FrontendQualityReviewSurface Surface, string Label, string Status, string? Detail);

/// <summary>One row of the post-run engine coverage table, read from the execution plan and the engine's per-surface results.</summary>
public sealed record FrontendQualityEngineCoverageRow(
    string Engine,
    string Policy,
    string Surface,
    string Prerequisite,
    string? Provider,
    string Result,
    string? Reason,
    FrontendQualityReleaseImpact ReleaseImpact)
{
    public string ReleaseImpactLabel => FrontendQualityExecutionPlanner.ImpactLabel(ReleaseImpact);
}

/// <summary>
/// Post-run projections that every view reads: one release-impact rule per logical issue, one coverage wording, one surface status and one
/// engine-coverage row per engine. Everything is derived from the stored report alone, so an opened history run shows exactly what it showed
/// when it completed.
/// </summary>
public static class FrontendQualityPostRunPresentation
{
    public static readonly IReadOnlyList<FrontendQualityResultTab> Tabs = Enum.GetValues<FrontendQualityResultTab>();
    public const int KeyIssueCount = 5;
    public const int ActionCount = 5;

    public static string TabId(FrontendQualityResultTab tab) => tab.ToString().ToLowerInvariant();

    // ── Release impact of a logical issue ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Blocking only when the release policy named this issue (an issue-policy blocker). Severity alone never blocks.</summary>
    public static FrontendQualityReleaseImpact IssueImpact(FrontendQualityReviewReport report, FrontendQualityLogicalIssue issue) =>
        !issue.IsActionable ? FrontendQualityReleaseImpact.NotApplicable
        : report.ReleaseBlockers.Any(b => b.Type == FrontendQualityBlockerType.IssuePolicy && b.Entity == issue.CanonicalTitle)
            ? FrontendQualityReleaseImpact.Blocking
            : FrontendQualityReleaseImpact.NonBlocking;

    public static string IssueImpactLabel(FrontendQualityReleaseImpact impact) => impact switch
    {
        FrontendQualityReleaseImpact.Blocking => "Blocks release (policy)",
        FrontendQualityReleaseImpact.NonBlocking => "Does not block release",
        _ => "No release impact",
    };

    // ── Coverage wording ─────────────────────────────────────────────────────────────────────────────────────────────────────────────

    public static string CoverageCount(int assessed, int total) => $"{assessed} of {total} completed";

    /// <summary>"Satisfied" or "Blocking" — never left for the reader to infer. No required engine is stated, not called satisfied.</summary>
    public static string RequiredMeaning(FrontendQualityCompleteness completeness) =>
        completeness.RequiredTotal == 0 ? "No required engine in this review"
        : completeness.RequiredCoverage == FrontendQualityRequiredCoverageState.AllRequiredAssessed ? "Satisfied" : "Blocking";

    /// <summary>Optional coverage never blocks a release, whatever it is.</summary>
    public static string OptionalMeaning(FrontendQualityCompleteness completeness) =>
        completeness.OptionalTotal == 0 ? "No optional engine active" : "Non-blocking";

    public static bool RequiredBlocking(FrontendQualityCompleteness completeness) =>
        completeness.RequiredTotal > 0 && completeness.RequiredCoverage != FrontendQualityRequiredCoverageState.AllRequiredAssessed;

    // ── Blockers ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The blockers grouped by type in a fixed order (configuration, coverage, issue policy) — the exact causal chain.</summary>
    public static IReadOnlyList<IGrouping<FrontendQualityBlockerType, FrontendQualityReleaseBlocker>> BlockersByType(FrontendQualityReviewReport report) =>
        report.ReleaseBlockers
            .GroupBy(b => b.Type)
            .OrderBy(g => g.Key switch { FrontendQualityBlockerType.Configuration => 0, FrontendQualityBlockerType.Coverage => 1, _ => 2 })
            .ToList();

    /// <summary>The first cause a reader needs ("Required coverage incomplete"); null when nothing blocks.</summary>
    public static string? PrimaryBlocker(FrontendQualityReviewReport report) => BlockersByType(report).FirstOrDefault()?.Key switch
    {
        FrontendQualityBlockerType.Configuration => "Required engine disabled",
        FrontendQualityBlockerType.Coverage => "Required coverage incomplete",
        FrontendQualityBlockerType.IssuePolicy => "Release policy blocks a logical issue",
        _ => null,
    };

    /// <summary>
    /// True when critical/high issues exist but none of them is a blocker: the release decision is then explained by coverage or by nothing,
    /// never by severity, and the note says so.
    /// </summary>
    public static bool ShowSeverityNote(FrontendQualityReviewReport report, IReadOnlyList<FrontendQualityLogicalIssue> actionable) =>
        actionable.Any(i => i.PrimarySeverity is FrontendQualitySeverity.Critical or FrontendQualitySeverity.High)
        && report.ReleaseBlockers.All(b => b.Type != FrontendQualityBlockerType.IssuePolicy);

    // ── Surfaces ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Per surface in the review scope: Completed when every engine planned to run on it completed, otherwise how many did; Not assessed
    /// when nothing completed there. An engine the plan could not run on a surface (a missing optional DOM) is not counted against it —
    /// the engine coverage table states that engine's own result. Without a plan (older stored runs), every primary result counts.
    /// </summary>
    public static IReadOnlyList<FrontendQualitySurfaceStatus> SurfaceStatuses(FrontendQualityReviewReport report)
    {
        var scope = report.ExecutionPlan?.Scope ?? report.TargetAccess?.ReviewScope;
        if (scope is null) return [];
        var statuses = new List<FrontendQualitySurfaceStatus>();
        foreach (var surface in FrontendQualityExecutionPlanner.ScopeSurfaces(scope.Value))
        {
            var label = FrontendQualityExecutionPlanner.SurfaceLabel(surface);
            var plannedIds = report.ExecutionPlan?.Engines
                .Where(e => e.Applicable && e.For(surface)?.Decision == FrontendQualityPlanDecision.Run).Select(e => e.EngineId).ToHashSet();
            var onSurface = report.EngineOutcomes
                .Where(o => plannedIds is null || plannedIds.Contains(o.EngineId))
                .SelectMany(o => o.Surfaces).Where(r => r.Surface == surface && (plannedIds is not null || r.Primary)).ToList();
            var total = Math.Max(plannedIds?.Count ?? 0, onSurface.Count);
            var completed = onSurface.Count(r => r.Completed);
            if (total == 0)
                statuses.Add(new(surface, label, "Not assessed", "No engine in this review could run on this surface."));
            else if (completed == total)
                statuses.Add(new(surface, label, "Completed", $"{completed} of {total} engine{(total == 1 ? "" : "s")} completed"));
            else if (RequiredCompletedOn(report, surface, onSurface))
                statuses.Add(new(surface, label, "Required engines completed", $"{completed} of {total} engines completed; optional engines not completed do not block"));
            else if (completed == 0)
                statuses.Add(new(surface, label, "Not assessed", onSurface.FirstOrDefault(r => !r.Completed)?.Reason));
            else
                statuses.Add(new(surface, label, "Partially completed", $"{completed} of {total} engines completed"));
        }
        return statuses;
    }

    /// <summary>Every Required engine planned on this surface completed there (and at least one was planned): what blocks is satisfied.</summary>
    private static bool RequiredCompletedOn(FrontendQualityReviewReport report, FrontendQualityReviewSurface surface, IReadOnlyList<FrontendQualitySurfaceResult> onSurface)
    {
        if (report.ExecutionPlan is not { } plan) return false;
        var required = plan.Engines.Where(e => e.Applicable && e.Policy == FrontendQualityEngineRequirement.Required
            && e.For(surface)?.Decision == FrontendQualityPlanDecision.Run).Select(e => e.EngineId).ToList();
        return required.Count > 0 && required.All(id => report.EngineOutcomes.FirstOrDefault(o => o.EngineId == id)?.Surfaces
            .Any(r => r.Surface == surface && r.Completed) == true);
    }

    // ── Engine coverage table ────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>One row per enabled engine in the plan, required first. Without a plan (older stored runs) the engine outcomes are used.</summary>
    public static IReadOnlyList<FrontendQualityEngineCoverageRow> EngineRows(FrontendQualityReviewReport report)
    {
        var outcomes = report.EngineOutcomes;
        if (report.ExecutionPlan is not { } plan)
            return outcomes.Where(o => o.Enabled && o.ExecutionState != FrontendQualityEngineExecutionState.Disabled)
                .OrderBy(o => o.Requirement)
                .Select(o => new FrontendQualityEngineCoverageRow(
                    FrontendQualityActiveEngines.DisplayName(o.EngineId), PolicyLabel(o.Requirement), "—", "—", o.Provider,
                    FrontendQualitySurfaceResults.ResultLabel(o), o.SanitizedFailureReason,
                    Impact(o.Requirement, o.ExecutionState == FrontendQualityEngineExecutionState.Assessed, applicable: !FrontendQualityCoverage.IsInactive(o))))
                .ToList();

        return plan.Engines.Where(e => e.Enabled)
            .OrderBy(e => e.Policy)
            .Select(engine =>
            {
                var outcome = outcomes.FirstOrDefault(o => o.EngineId == engine.EngineId);
                var primary = outcome?.Surfaces.FirstOrDefault(s => s.Primary) ?? outcome?.Surfaces.FirstOrDefault();
                var completed = outcome is not null && (outcome.Surfaces.Count > 0
                    ? outcome.Surfaces.Any(s => s.Completed && s.Primary)
                    : outcome.ExecutionState == FrontendQualityEngineExecutionState.Assessed);
                var surfaces = engine.RunSurfaces.Count > 0 ? engine.RunSurfaces : engine.Surfaces.Where(s => s.Primary).Select(s => s.Surface).ToList();
                var result = primary?.Result ?? (outcome is null ? FrontendQualityExecutionPlanner.DecisionLabel(engine.Decision) : FrontendQualitySurfaceResults.ResultLabel(outcome));
                var primarySurface = primary?.Surface ?? engine.Surfaces.FirstOrDefault(s => s.Primary)?.Surface;
                var surfacePlan = primarySurface is { } planned ? engine.For(planned) : null;
                // The first reason that says something: an empty surface reason must not hide the engine's own.
                var reason = completed ? null : new[] { primary?.Reason, outcome?.SanitizedFailureReason, surfacePlan?.Reason, engine.Reason,
                        outcome is { OutcomeReason: not FrontendQualityEngineOutcomeReason.None } o ? FrontendQualityEngineOutcomePresentation.GetLabel(o.OutcomeReason) : null }
                    .FirstOrDefault(r => !string.IsNullOrWhiteSpace(r));
                // The prerequisite of the surface the engine ran (or was meant to run) on: "Authenticated HTTP/API context", not the public label.
                var prerequisite = surfacePlan?.Prerequisite is { Length: > 0 } p ? p : engine.Prerequisite;
                return new FrontendQualityEngineCoverageRow(
                    FrontendQualityActiveEngines.DisplayName(engine.EngineId),
                    PolicyLabel(engine.Policy),
                    surfaces.Count == 0 ? "—" : string.Join(", ", surfaces.Select(FrontendQualityExecutionPlanner.SurfaceLabel)),
                    prerequisite,
                    primary?.Provider ?? outcome?.Provider,
                    result,
                    string.IsNullOrWhiteSpace(reason) ? null : reason,
                    Impact(engine.Policy, completed, engine.Applicable));
            })
            .ToList();
    }

    public static string PolicyLabel(FrontendQualityEngineRequirement policy) =>
        policy == FrontendQualityEngineRequirement.Required ? "Required" : "Optional";

    private static FrontendQualityReleaseImpact Impact(FrontendQualityEngineRequirement policy, bool completed, bool applicable) =>
        !applicable ? FrontendQualityReleaseImpact.NotApplicable
        : policy != FrontendQualityEngineRequirement.Required ? FrontendQualityReleaseImpact.NonBlocking
        : completed ? FrontendQualityReleaseImpact.Satisfied : FrontendQualityReleaseImpact.Blocking;

    // ── Evidence ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Source finding id → the logical issue it was grouped into. Explains why N source findings became M logical issues.</summary>
    public static IReadOnlyDictionary<string, FrontendQualityLogicalIssue> GroupedInto(IReadOnlyList<FrontendQualityLogicalIssue> issues)
    {
        var map = new Dictionary<string, FrontendQualityLogicalIssue>(StringComparer.Ordinal);
        foreach (var issue in issues)
            foreach (var instance in issue.FindingInstances)
                map.TryAdd(instance.SourceFindingId, issue);
        return map;
    }

    public static FrontendQualityLogicalIssue? IssueFor(IReadOnlyDictionary<string, FrontendQualityLogicalIssue> map, FrontendQualityFinding finding) =>
        map.TryGetValue(ReportExportService.SanitizePassive(finding.Id), out var issue) ? issue : null;

    // ── Recommended actions ──────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>"3 high priority · 4 medium priority": the action summary, counted per remediation, not per issue.</summary>
    public static string ActionPriorities(IReadOnlyList<FrontendQualityRecommendationTheme> themes) =>
        string.Join(" · ", themes.GroupBy(t => t.Priority).OrderBy(g => g.Key)
            .Select(g => $"{g.Count()} {PriorityWord(g.Key)} priority"));

    private static string PriorityWord(FrontendQualitySeverity severity) => severity switch
    {
        FrontendQualitySeverity.Critical => "critical",
        FrontendQualitySeverity.High => "high",
        FrontendQualitySeverity.Medium => "medium",
        FrontendQualitySeverity.Low => "low",
        _ => "informational",
    };
}
