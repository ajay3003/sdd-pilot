using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

/// <summary>Runtime facts from backend readiness: Node execution runtime and Chromium/CDP target, kept apart.</summary>
public static class FrontendQualityRuntime
{
    public const string NodeUnavailable = "Node execution runtime unavailable.";
    public const string CdpUnavailable = "No compatible Chromium/CDP target available.";

    public static FrontendQualityRuntimeFacts? From(FrontendQualityEngineReadinessDto? readiness) => From(readiness?.RuntimeComponents);
    public static FrontendQualityRuntimeFacts? From(FrontendQualityEngineReadinessReportDto? readiness) => From(readiness?.RuntimeComponents);

    public static FrontendQualityRuntimeFacts? From(IReadOnlyList<FrontendQualityRuntimeComponentDto>? components)
    {
        if (components is not { Count: > 0 }) return null;
        var node = components.FirstOrDefault(c => c.Kind == "NodeRuntime");
        var cdp = components.FirstOrDefault(c => c.Kind == "CdpTarget");
        // A target is only accepted when it speaks the DevTools protocol; Edge qualifies exactly like Chrome.
        var cdpAvailable = cdp?.Available is true
            ? FrontendQualityCapabilityResolver.IsChromiumCompatible(cdp.Detail ?? cdp.Provider, speaksDevToolsProtocol: true)
            : cdp?.Available;
        return new(node?.Available, node?.Detail, cdpAvailable, cdp?.Provider, cdp?.Detail);
    }

    /// <summary>The exact Lighthouse prerequisite that is missing, or null when nothing is known to be missing.</summary>
    public static string? LighthouseReason(FrontendQualityRuntimeFacts? facts) =>
        facts?.NodeRuntimeAvailable == false ? NodeUnavailable
        : facts?.CdpTargetAvailable == false ? CdpUnavailable
        : null;
}

/// <summary>
/// Merges the coverage passes into one outcome per engine and records, per surface, what happened. An engine that completed on a surface is
/// reported from that completion: a later pass that did not (or could not) run it never overrides it. An engine that never ran takes its
/// state and reason from the plan — Not applicable is never shown as Blocked, and Not assessed is never shown as "No evidence".
/// </summary>
public static class FrontendQualitySurfaceResults
{
    public static List<FrontendQualityEngineOutcome> Merge(
        FrontendQualityExecutionPlan plan,
        IReadOnlyList<(FrontendReviewAccessScope? Mode, IReadOnlyList<FrontendQualityEngineOutcome> Outcomes)> passes)
    {
        var ids = passes.SelectMany(p => p.Outcomes).Select(o => o.EngineId).Distinct().OrderBy(id => id).ToList();
        var merged = new List<FrontendQualityEngineOutcome>();
        foreach (var id in ids)
        {
            var attempts = passes.SelectMany(p => p.Outcomes.Where(o => o.EngineId == id).Select(o => (Surface: SurfaceOf(p.Mode, o), Outcome: o))).ToList();
            var ran = attempts.Where(a => Attempted(a.Outcome)).ToList();
            var chosen = ran.Where(a => a.Outcome.ExecutionState == FrontendQualityEngineExecutionState.Assessed).Select(a => a.Outcome).FirstOrDefault()
                ?? ran.Select(a => a.Outcome).FirstOrDefault()
                ?? attempts[0].Outcome;
            var enginePlan = plan.For(id);
            // Not applicable to the review scope is a planning fact: whatever a fail-fast path recorded for it, it did not run and is not counted.
            if (enginePlan is { Decision: FrontendQualityPlanDecision.NotApplicable } notApplicable && chosen.ExecutionState != FrontendQualityEngineExecutionState.Assessed)
                chosen = chosen with { ExecutionState = FrontendQualityEngineExecutionState.NotApplicable, OutcomeReason = FrontendQualityEngineOutcomeReason.NotApplicableToScope,
                    SanitizedFailureReason = notApplicable.Reason };
            var surfaces = enginePlan is null ? [] : Surfaces(enginePlan, attempts);
            merged.Add(chosen with
            {
                Surfaces = surfaces,
                Provider = surfaces.FirstOrDefault(s => s.Completed)?.Provider ?? surfaces.FirstOrDefault(s => s.Primary)?.Provider,
            });
        }
        return merged;
    }

    /// <summary>The surface an outcome covered: the pass mode, except that a Browser Companion access is always its own surface.</summary>
    public static FrontendQualityReviewSurface SurfaceOf(FrontendReviewAccessScope? mode, FrontendQualityEngineOutcome outcome) =>
        outcome.AccessKind == FrontendQualityEngineAccessKind.BrowserCompanion ? FrontendQualityReviewSurface.CompanionPage
        : mode switch
        {
            FrontendReviewAccessScope.PublicOnly => FrontendQualityReviewSurface.Public,
            FrontendReviewAccessScope.AuthenticatedOnly => FrontendQualityReviewSurface.Authenticated,
            _ => outcome.AccessKind is FrontendQualityEngineAccessKind.AuthenticatedHttp or FrontendQualityEngineAccessKind.AuthenticatedBrowserSession
                ? FrontendQualityReviewSurface.Authenticated : FrontendQualityReviewSurface.Public,
        };

    /// <summary>The engine took part in the pass (it may still have failed). Disabled and not-selected engines did not.</summary>
    public static bool Attempted(FrontendQualityEngineOutcome outcome) =>
        outcome.Enabled && outcome.ExecutionState != FrontendQualityEngineExecutionState.Disabled
        && outcome.OutcomeReason is not (FrontendQualityEngineOutcomeReason.NotSelected or FrontendQualityEngineOutcomeReason.DisabledInSystemSettings
            or FrontendQualityEngineOutcomeReason.DisabledInTargetEnvironment or FrontendQualityEngineOutcomeReason.BlockedByDeploymentPolicy);

    private static List<FrontendQualitySurfaceResult> Surfaces(FrontendQualityEnginePlan plan,
        IReadOnlyList<(FrontendQualityReviewSurface Surface, FrontendQualityEngineOutcome Outcome)> attempts)
    {
        var results = new List<FrontendQualitySurfaceResult>();
        foreach (var planned in plan.Surfaces)
        {
            // A secondary surface the plan did not run on is explained by the plan, never by a pass that had nothing to do there.
            if (!planned.Primary && planned.Decision != FrontendQualityPlanDecision.Run)
            {
                results.Add(new(planned.Surface, FrontendQualityExecutionPlanner.DecisionLabel(planned.Decision), false, planned.Provider, planned.Reason, false));
                continue;
            }
            var onSurface = attempts.Where(a => a.Surface == planned.Surface && Attempted(a.Outcome)).Select(a => a.Outcome).ToList();
            var assessed = onSurface.FirstOrDefault(o => o.ExecutionState == FrontendQualityEngineExecutionState.Assessed);
            if (assessed is not null)
                results.Add(new(planned.Surface, assessed.EvidenceCount == 0 && (assessed.FindingCount ?? 0) == 0 ? "Completed — no evidence produced" : "Completed",
                    true, planned.Provider ?? assessed.AccessLabel, "", planned.Primary));
            else if (onSurface.FirstOrDefault() is { } failed)
                results.Add(new(planned.Surface, ResultLabel(failed), false, planned.Provider ?? failed.AccessLabel,
                    failed.SanitizedFailureReason ?? failed.ReadinessReason ?? planned.Reason, planned.Primary));
            else
                results.Add(new(planned.Surface, FrontendQualityExecutionPlanner.DecisionLabel(planned.Decision == FrontendQualityPlanDecision.Run
                        ? FrontendQualityPlanDecision.NotAssessed : planned.Decision), false, planned.Provider,
                    planned.Decision == FrontendQualityPlanDecision.Run ? "The engine did not report a result for this surface." : planned.Reason, planned.Primary));
        }
        return results;
    }

    /// <summary>Result words for an engine that ran and did not complete. Never "No evidence": that is reserved for a completed run.</summary>
    public static string ResultLabel(FrontendQualityEngineOutcome outcome) => outcome.ExecutionState switch
    {
        FrontendQualityEngineExecutionState.Assessed => "Completed",
        _ when FrontendQualityEngineOutcomePresentation.IsUnsupported(outcome.OutcomeReason) => "Unsupported",
        FrontendQualityEngineExecutionState.NotApplicable => "Not applicable",
        FrontendQualityEngineExecutionState.TimedOut => "Timed out",
        FrontendQualityEngineExecutionState.EngineError => "Engine error",
        FrontendQualityEngineExecutionState.Cancelled => "Cancelled",
        FrontendQualityEngineExecutionState.SafetyBlocked => "Safety blocked",
        _ => FrontendQualityEngineOutcomePresentation.IsUnsupported(outcome.OutcomeReason) ? "Unsupported" : "Not assessed",
    };
}
