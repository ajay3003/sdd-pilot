using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

public static class FrontendQualityDecisionSupportService
{
    private static readonly FrontendQualityEngineExecutionState[] ReviewFailureStates =
    [
        FrontendQualityEngineExecutionState.Disabled,
        FrontendQualityEngineExecutionState.Unavailable,
        FrontendQualityEngineExecutionState.SafetyBlocked,
        FrontendQualityEngineExecutionState.TimedOut,
        FrontendQualityEngineExecutionState.Cancelled,
        FrontendQualityEngineExecutionState.EngineError,
    ];

    public static List<FrontendQualityManualReviewItem> BuildManualReviewItems(
        IReadOnlyList<FrontendQualityLogicalIssue> issues,
        IReadOnlyList<FrontendQualityEngineOutcome> outcomes)
    {
        var items = issues
            .Where(issue => issue.ReviewDisposition == FrontendQualityReviewDisposition.ManualVerificationRequired)
            .Select(issue => new FrontendQualityManualReviewItem
            {
                Title = issue.CanonicalTitle,
                Reason = "Automated evidence cannot determine the final result for this issue.",
                Source = string.Join(", ", issue.Sources.Select(SourceLabel)),
                RelatedLogicalId = issue.LogicalId,
                Severity = issue.PrimarySeverity,
            }).ToList();

        foreach (var outcome in outcomes)
        {
            items.AddRange(outcome.ManualTestingObligations.Select(obligation => new FrontendQualityManualReviewItem
            {
                Title = $"{outcome.DisplayName} manual verification",
                Reason = obligation,
                Source = outcome.DisplayName,
            }));
        }

        return items
            .DistinctBy(item => (item.Title, item.Reason, item.Source, item.RelatedLogicalId))
            .OrderBy(item => item.Severity ?? FrontendQualitySeverity.Info)
            .ThenBy(item => item.Title, StringComparer.Ordinal)
            .ToList();
    }

    public static FrontendQualityReleaseDisposition EvaluateReleaseDisposition(
        FrontendQualityCoverage coverage,
        IReadOnlyList<FrontendQualityEngineOutcome> outcomes,
        IReadOnlyList<FrontendQualityLogicalIssue> issues,
        IReadOnlyList<FrontendQualityManualReviewItem> manualItems,
        FrontendQualityReleasePolicySettings policy)
    {
        // A release decision needs every REQUIRED engine to have actually assessed the target. One required engine that
        // could not run (blocked, unsupported, unreachable, timed out, failed) keeps the disposition Blocked; the engine table
        // explains which engine and why. Partial required coverage is never downgraded to "review required".
        if (coverage.RequiredCoverageState != FrontendQualityRequiredCoverageState.AllRequiredAssessed)
            return FrontendQualityReleaseDisposition.Blocked;

        var blockingIds = policy.BlockingLogicalIssueIds.ToHashSet(StringComparer.Ordinal);
        // Only actionable issues can match a release policy: a derived indicator or informational observation never blocks by itself.
        if (issues.Any(issue => issue.IsActionable && blockingIds.Contains(issue.LogicalId)))
            return FrontendQualityReleaseDisposition.Blocked;

        if (manualItems.Count > 0)
            return FrontendQualityReleaseDisposition.ReviewRequired;

        if (policy.ReviewOptionalEngineFailures && outcomes.Any(outcome =>
                outcome.Requirement == FrontendQualityEngineRequirement.Optional &&
                outcome.Enabled && ReviewFailureStates.Contains(outcome.ExecutionState)))
            return FrontendQualityReleaseDisposition.ReviewRequired;

        return FrontendQualityReleaseDisposition.NoAutomatedBlockDetected;
    }

    public const string IssuePolicyRule = "Target Environment release policy lists this logical issue as release-blocking.";
    public const string SeverityNotBlocking = "Finding severity describes the technical severity of the observation. It does not by itself determine release status.";

    /// <summary>
    /// Every exact cause of a Blocked decision, by type: a CONFIGURATION blocker (a Required engine that is disabled), a COVERAGE blocker per
    /// required, applicable engine that did not complete (with its reason), and an ISSUE-POLICY blocker per logical issue the release policy
    /// names. Severity alone never blocks: only the configured policy does.
    /// </summary>
    public static List<FrontendQualityReleaseBlocker> ReleaseBlockers(
        FrontendQualityCoverage coverage,
        IReadOnlyList<FrontendQualityEngineOutcome> outcomes,
        IReadOnlyList<FrontendQualityLogicalIssue> issues,
        FrontendQualityReleasePolicySettings policy,
        FrontendQualityActiveEngineSnapshot? active,
        FrontendQualityExecutionPlan? plan)
    {
        var blockers = new List<FrontendQualityReleaseBlocker>();
        foreach (var engine in active?.RequiredButDisabled ?? [])
            blockers.Add(new(FrontendQualityBlockerType.Configuration, engine.DisplayName,
                $"{engine.DisplayName} is Required but disabled in Target Environment → Frontend Review Engines, so required coverage can never be complete.",
                "Required engines must be enabled."));

        if (plan is not null)
        {
            foreach (var engine in plan.Engines.Where(e => e.Applicable && e.Policy == FrontendQualityEngineRequirement.Required))
            {
                var outcome = outcomes.FirstOrDefault(o => o.EngineId == engine.EngineId);
                var completed = outcome is not null && (outcome.Surfaces.Count > 0
                    ? outcome.Surfaces.Any(s => s.Completed && s.Primary)
                    : outcome.ExecutionState == FrontendQualityEngineExecutionState.Assessed);
                if (completed) continue;
                var primary = outcome?.Surfaces.FirstOrDefault(s => s.Primary && !s.Completed);
                var surface = primary is null ? "" : $" ({FrontendQualityExecutionPlanner.SurfaceLabel(primary.Surface)})";
                blockers.Add(new(FrontendQualityBlockerType.Coverage, engine.DisplayName + surface,
                    primary?.Reason is { Length: > 0 } r ? r : outcome?.SanitizedFailureReason ?? engine.Reason, "Required engines must complete."));
            }
            if (plan.Engines.All(e => !e.Applicable || e.Policy != FrontendQualityEngineRequirement.Required)
                && !outcomes.Any(o => o.ExecutionState == FrontendQualityEngineExecutionState.Assessed))
                blockers.Add(new(FrontendQualityBlockerType.Coverage, "Review", "No engine completed an assessment in this run."));
        }
        else if (coverage.RequiredCoverageState != FrontendQualityRequiredCoverageState.AllRequiredAssessed)
        {
            foreach (var outcome in outcomes.Where(o => o.Requirement == FrontendQualityEngineRequirement.Required
                         && o.ExecutionState != FrontendQualityEngineExecutionState.Assessed && !FrontendQualityCoverage.IsInactive(o)))
                blockers.Add(new(FrontendQualityBlockerType.Coverage, outcome.DisplayName,
                    outcome.SanitizedFailureReason ?? FrontendQualityEngineOutcomePresentation.StateLabel(outcome), "Required engines must complete."));
            if (blockers.All(b => b.Type != FrontendQualityBlockerType.Coverage) && blockers.All(b => b.Type != FrontendQualityBlockerType.Configuration))
                blockers.Add(new(FrontendQualityBlockerType.Coverage, "Required coverage",
                    $"{coverage.RequiredAssessed} of {coverage.RequiredTotal} required engines completed.", "Required engines must complete."));
        }

        var blockingIds = policy.BlockingLogicalIssueIds.ToHashSet(StringComparer.Ordinal);
        foreach (var issue in issues.Where(issue => issue.IsActionable && blockingIds.Contains(issue.LogicalId)))
            blockers.Add(new(FrontendQualityBlockerType.IssuePolicy, issue.CanonicalTitle,
                $"{issue.PrimarySeverity} logical issue matched the release policy.", IssuePolicyRule));
        return blockers;
    }

    /// <summary>The disposition from the exact blockers; any blocker blocks, then manual review, then optional-engine review policy.</summary>
    public static FrontendQualityReleaseDisposition EvaluateReleaseDisposition(
        FrontendQualityCoverage coverage,
        IReadOnlyList<FrontendQualityEngineOutcome> outcomes,
        IReadOnlyList<FrontendQualityLogicalIssue> issues,
        IReadOnlyList<FrontendQualityManualReviewItem> manualItems,
        FrontendQualityReleasePolicySettings policy,
        IReadOnlyList<FrontendQualityReleaseBlocker> blockers)
    {
        if (blockers.Count > 0) return FrontendQualityReleaseDisposition.Blocked;
        if (manualItems.Count > 0) return FrontendQualityReleaseDisposition.ReviewRequired;
        if (policy.ReviewOptionalEngineFailures && outcomes.Any(outcome =>
                outcome.Requirement == FrontendQualityEngineRequirement.Optional &&
                outcome.Enabled && ReviewFailureStates.Contains(outcome.ExecutionState)))
            return FrontendQualityReleaseDisposition.ReviewRequired;
        return FrontendQualityReleaseDisposition.NoAutomatedBlockDetected;
    }

    public static string BlockerTypeLabel(FrontendQualityBlockerType type) => type switch
    {
        FrontendQualityBlockerType.Coverage => "Coverage blocker",
        FrontendQualityBlockerType.IssuePolicy => "Issue-policy blocker",
        _ => "Configuration blocker",
    };

    public static string SourceLabel(FrontendQualityEngineId id) => id switch
    {
        FrontendQualityEngineId.StaticSecurity => "HTTP Security",
        FrontendQualityEngineId.PassivePerformance => "HTTP Performance",
        FrontendQualityEngineId.BrowserRuntime => "Browser Runtime",
        FrontendQualityEngineId.Accessibility => "Accessibility / axe-core",
        FrontendQualityEngineId.Lighthouse => "Lighthouse",
        FrontendQualityEngineId.PassiveSecurity => "Passive Web Security / ZAP",
        FrontendQualityEngineId.BrowserQuality => "Browser Quality / Browser Companion",
        FrontendQualityEngineId.PerformanceQuality => "Browser Performance Quality / Browser Companion + Local HTTPS Proxy",
        _ => id.ToString(),
    };

    public static string ExecutionStateLabel(FrontendQualityEngineExecutionState state) => state switch
    {
        FrontendQualityEngineExecutionState.SafetyBlocked => "Safety blocked",
        FrontendQualityEngineExecutionState.TimedOut => "Timed out",
        FrontendQualityEngineExecutionState.EngineError => "Engine error",
        FrontendQualityEngineExecutionState.NotApplicable => "Not applicable",
        _ => state.ToString(),
    };
}
