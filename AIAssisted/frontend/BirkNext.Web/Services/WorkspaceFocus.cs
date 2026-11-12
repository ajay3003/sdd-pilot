using BirkNext.SourceBaselines;

namespace BirkNext.Web.Services;

/// <summary>Immediate actionability of a capability for the person now (not its lifecycle): derived from readiness and decision only.</summary>
public enum FocusActionState { CanDoNow, Blocked, Completed }

/// <summary>One next step: the existing capability (same <see cref="Id"/> and route in every view), its actionability and why.</summary>
public sealed record FocusAction(string Id, string Title, string Why, string Route, FocusActionState State, string StateLabel, string? Blocker);

/// <summary>A risk no workspace view may hide (e.g. no analysis baseline). The wording is plain; the code is stable.</summary>
public sealed record FocusRisk(string Code, string Text, string Route, string ActionLabel);

/// <summary>The view-aware projection: the relevant capabilities of the selected workspace view, at most three primary next steps, honest counts
/// and the risks that stay visible in every view.</summary>
public sealed record WorkspaceFocusView(WorkspaceViewDefinition View, IReadOnlyList<FocusAction> Primary, int Relevant, int ReadyNow, int Blocked, int Completed,
    IReadOnlyList<FocusRisk> Risks);

/// <summary>
/// One presentation over the shared facts — never a second recommendation engine. Input: the capabilities Recommended Workflow and the Dashboard
/// already share (<see cref="ReviewCapability"/>: identity, route, readiness, decision) and the shared source context. The workspace view only
/// decides relevance and order (its modules); it never changes a capability, a decision, a severity or the source baseline, and it is not a
/// permission. Risks are computed from the source context alone, so they are identical in every view.
/// </summary>
public static class WorkspaceFocus
{
    public const string BaselineNotSelected = "BASELINE_NOT_SELECTED", BaselineUnavailable = "BASELINE_UNAVAILABLE";

    public static WorkspaceFocusView Of(WorkspaceView view, IReadOnlyList<ReviewCapability> capabilities, SourceContextView? sourceContext)
    {
        var definition = WorkspaceViews.Of(view);
        var relevant = capabilities.Where(c => c.Applicability != ReviewApplicabilityState.NotApplicable && InView(definition, c.Route)).ToList();
        var actions = relevant.Select(Action).ToList();
        var primary = actions.Where(a => a.State != FocusActionState.Completed)
            .OrderBy(a => a.State == FocusActionState.CanDoNow ? 0 : 1)
            .ThenBy(a => Order(relevant.First(c => c.Id == a.Id), definition))
            .Take(3).ToList();
        return new(definition, primary, relevant.Count, actions.Count(a => a.State == FocusActionState.CanDoNow), actions.Count(a => a.State == FocusActionState.Blocked),
            actions.Count(a => a.State == FocusActionState.Completed), Risks(sourceContext, view));
    }

    /// <summary>The risks every view shows, from the shared source context: a source whose analysed version is not chosen or no longer exists.</summary>
    /// <remarks>Same risk identity (code), severity and destination in every view; only the leading sentence speaks to the view's audience.</remarks>
    public static IReadOnlyList<FocusRisk> Risks(SourceContextView? sourceContext, WorkspaceView view = WorkspaceView.AllModules) =>
    [
        .. (sourceContext?.Sources ?? []).Where(s => s.State == SourceBaselineState.NotSelected).Select(s => new FocusRisk(BaselineNotSelected,
            $"{AudienceLead(view)}Choose which {s.DisplayName} version to analyse: {s.DistinctContents} different versions are imported and none is selected, so no implementation-specific result is drawn from {s.DisplayName} yet.",
            "sources#scp-title", "Choose the version")),
        .. (sourceContext?.Sources ?? []).Where(s => s.State == SourceBaselineState.SelectedUnavailable).Select(s => new FocusRisk(BaselineUnavailable,
            $"The chosen {s.DisplayName} version is no longer available; {s.DisplayName} is left out until another version is chosen.", "sources#scp-title", "Choose the version")),
    ];

    /// <summary>Why the baseline risk matters to the selected audience (wording only; the risk and its severity are the same in every view).</summary>
    public static string AudienceLead(WorkspaceView view) => view switch
    {
        WorkspaceView.ManualTesting => "Select the correct project version before preparing tests. ",
        WorkspaceView.TechnicalTester => "Confirm the source and environment baseline before integration verification. ",
        WorkspaceView.TestLead => "Resolve the baseline ambiguity before approving test readiness. ",
        WorkspaceView.Architect => "Verify the authoritative application/component version mapping. ",
        WorkspaceView.Developer => "Pick the source version your change is reviewed against. ",
        WorkspaceView.ProjectLeader or WorkspaceView.ScrumMaster => "Release and delivery status depend on the chosen project version. ",
        _ => "",
    };

    /// <summary>A capability belongs to the view when the view holds the sidebar module of its route (All Modules: every capability).</summary>
    public static bool InView(WorkspaceViewDefinition view, string route)
    {
        if (view.IsAllModules) return true;
        var path = route.Split('?', '#')[0].Trim('/');
        return NavigationCatalog.Sections.SelectMany(s => s.Items)
            .Any(i => view.Contains(i) && (string.Equals(i.Route, path, StringComparison.OrdinalIgnoreCase) || path.StartsWith(i.Route + "/", StringComparison.OrdinalIgnoreCase)));
    }

    private static FocusAction Action(ReviewCapability c) => c.Decision switch
    {
        ReviewDecisionState.Reviewed => new(c.Id, c.DisplayName, c.Reason, c.Route, FocusActionState.Completed, "Done: reviewed for the current evidence", null),
        ReviewDecisionState.EvidenceChanged when c.IsActionable => new(c.Id, c.DisplayName, "The evidence changed since the last review.", c.Route, FocusActionState.CanDoNow, "Review again", null),
        _ when c.IsActionable => new(c.Id, c.DisplayName, c.Reason, c.Route, FocusActionState.CanDoNow, "Ready now", null),
        // The capability's own status text says what is missing ("Needs setup: Target Environment"); it is shown once, as the reason.
        _ => new(c.Id, c.DisplayName, c.Reason, c.Route, FocusActionState.Blocked, "Blocked", c.StatusLabel),
    };

    /// <summary>Workflow order of the view's modules (a capability's position in the view), then required before optional.</summary>
    private static (int, int) Order(ReviewCapability c, WorkspaceViewDefinition view)
    {
        var path = c.Route.Split('?', '#')[0].Trim('/');
        var index = view.Modules.ToList().FindIndex(m => string.Equals(m, path, StringComparison.OrdinalIgnoreCase) || path.StartsWith(m + "/", StringComparison.OrdinalIgnoreCase));
        return (index < 0 ? int.MaxValue : index, c.Requiredness is ReviewRequiredness.Optional ? 1 : 0);
    }
}
