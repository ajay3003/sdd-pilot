using BirkNext.CriticalE2E;
using BirkNext.Scopes;

namespace BirkNext.Web.Services;

/// <summary>One classification's counts over the flows a scope selection names. Every number comes from the overview's own per-flow data.</summary>
/// <param name="Named">Flows of this classification that the selection names and that exist in this environment.</param>
/// <param name="Prepared">Definition is complete (<see cref="CriticalE2EFlowSummary.Configured"/>: steps, final outcome, environment).</param>
/// <param name="Executable">Can run now: enabled, configured and its engine is ready (the same rule as the row's Run button).</param>
/// <param name="Executed">Has a recorded result (<see cref="CriticalE2EFlowSummary.LastStatus"/> is not NotRun / Running).</param>
/// <param name="Passed">Latest result is Passed, for the current definition and, when a build is named, for that build (the row's own rule).</param>
public sealed record CriticalE2EScopeClassCounts(int Named, int Prepared, int Executable, int Executed, int Passed);

/// <param name="Named">Distinct Critical E2E flow members of the selection (a flow named by two scopes counts once).</param>
/// <param name="MissingIds">Named flow ids this environment does not have (removed, or chosen in another environment).</param>
/// <param name="SharedByScopes">Flows named by more than one selected scope (still counted once).</param>
public sealed record CriticalE2EScopeCounts(int Named, IReadOnlyList<string> MissingIds, int SharedByScopes,
    CriticalE2EScopeClassCounts Critical, CriticalE2EScopeClassCounts Diagnostic);

/// <summary>
/// The shared scope filter on Critical E2E Regression. A selected scope narrows the flow list to the flows it names by flow id
/// (<see cref="ScopeMemberKind.CriticalE2EFlow"/>); nothing is matched by name. Selecting a scope never starts, enables or authorizes a run:
/// row Run buttons keep their own readiness rule. Critical and smoke/diagnostic flows are counted separately, because a smoke/diagnostic
/// flow — including one approved from a UI Smoke or UI Functional suggestion — never counts toward Business E2E or release coverage.
/// </summary>
public static class CriticalE2EScopePresentation
{
    public const string SmokeNote = "UI Smoke and other smoke / diagnostic flows (including flows approved from UI suggestions) never count toward Business E2E or release coverage; they are counted separately.";
    public const string FilterOnlyNote = "The scope is a filter only: it never starts, enables or authorizes a run.";

    /// <summary>The flow ids the selection names, or null when no scope is selected (the page then behaves exactly as before).</summary>
    public static HashSet<string>? FlowIds(ScopeSelection? selection) => selection is null ? null
        : new HashSet<string>(selection.Members.Where(m => m.Member.Kind == ScopeMemberKind.CriticalE2EFlow).Select(m => m.Member.TargetId), StringComparer.Ordinal);

    /// <summary>How many selected scopes name the flow (1 when only one does).</summary>
    public static int Contributors(ScopeSelection? selection, string flowId) => selection?.Members
        .FirstOrDefault(m => m.Member.Kind == ScopeMemberKind.CriticalE2EFlow && m.Member.TargetId == flowId)?.ContributedBy.Select(c => c.ScopeId).Distinct().Count() ?? 0;

    public static CriticalE2EScopeCounts Counts(CriticalE2EOverview overview, ScopeSelection selection)
    {
        var members = selection.Members.Where(m => m.Member.Kind == ScopeMemberKind.CriticalE2EFlow)
            .GroupBy(m => m.Member.TargetId, StringComparer.Ordinal).Select(g => g.First()).ToList();
        var ids = members.Select(m => m.Member.TargetId).ToHashSet(StringComparer.Ordinal);
        var flows = overview.Flows.Where(f => ids.Contains(f.FlowId)).ToList();
        var runnable = CriticalE2EPresentation.Rows(overview, includeDiagnostic: true).Where(r => r.Runnable).Select(r => r.FlowId).ToHashSet(StringComparer.Ordinal);
        var buildNamed = !string.IsNullOrWhiteSpace(overview.Release.BuildId);
        CriticalE2EScopeClassCounts Of(CriticalE2EFlowKind kind)
        {
            var of = flows.Where(f => f.Kind == kind).ToList();
            return new(of.Count, of.Count(f => f.Configured), of.Count(f => runnable.Contains(f.FlowId)),
                of.Count(f => f.LastStatus is not CriticalE2EStatus.NotRun and not CriticalE2EStatus.Running),
                of.Count(f => f.LastStatus == CriticalE2EStatus.Passed && !f.LastRunStale && (!buildNamed || f.LastResultMatchesRelease)));
        }
        var present = flows.Select(f => f.FlowId).ToHashSet(StringComparer.Ordinal);
        return new(ids.Count, [.. ids.Where(id => !present.Contains(id)).Order(StringComparer.Ordinal)],
            members.Count(m => m.ContributedBy.Select(c => c.ScopeId).Distinct().Count() > 1),
            Of(CriticalE2EFlowKind.Critical), Of(CriticalE2EFlowKind.Diagnostic));
    }
}
