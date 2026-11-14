using BirkNext.Scopes;

namespace BirkNext.Web.Services;

/// <summary>The state of one Project Test Setup step, derived from real data on every render (nothing is persisted).</summary>
public enum SetupStepState { Available, Completed, Current, NeedsAttention, NotStarted, Unavailable, Error }

/// <summary>How a member of a test area came to be there, in the words a tester reads.</summary>
public enum AreaMemberState { ExplicitlySelected, SuggestedFromSource, HumanConfirmed, Candidate, Unresolved }

/// <summary>Everything the six steps and the recommended next step are derived from. <paramref name="Areas"/> are the project's test areas
/// that are not archived; <paramref name="Selected"/> is the chosen test area's detail (null while none is chosen or loaded).</summary>
public sealed record SetupFacts(
    bool HasProject,
    SystemsPageState SourceState,
    SystemsOverviewView? Overview,
    bool AreasLoaded,
    string? AreasError,
    IReadOnlyList<ScopeSummary> Areas,
    ScopeDetail? Selected,
    IReadOnlyList<ScopeMemberStatus> SelectedChecks);

/// <summary>The one recommended next step: an in-page action id or a link.</summary>
public sealed record SetupNextAction(string Id, string Label, string Explanation, string? Href = null, string? ActionId = null);

/// <summary>A module that reads <c>?scope=&lt;id&gt;</c>. <paramref name="Behaviour"/> is "filters" (the module narrows its list to the test area)
/// or "context" (the module shows the test area as context but does not filter by it).</summary>
public sealed record TestAreaUseLink(string Module, string Label, string Href, string Behaviour, string Explanation);

/// <summary>A test area proposed from evidence that exists in the project (never from a hardcoded list): what it proposes and why.</summary>
public sealed record TestAreaSuggestion(string Id, string Title, string Provenance, string Basis, IReadOnlyList<ScopeCatalogItem> Members)
{
    public string Proposes => string.Join(", ", Members.GroupBy(m => m.Kind).OrderBy(g => g.Key)
        .Select(g => $"{g.Count()} {SystemsScopesPresentation.KindLabel(g.Key, plural: g.Count() != 1).ToLowerInvariant()}"));
}

/// <summary>
/// Pure rules of Project Test Setup (the guided view of the Shared Scope Registry): step states, the recommended next step, member states,
/// suggestions from catalogs and the modules a saved test area can be used in. No calls; ids never shown as names.
/// </summary>
public static class ProjectTestSetupPresentation
{
    public const string Route = "project-scopes";
    public const string Title = "Project Test Setup";
    public const string Purpose = "Choose what you want to test and use the selected test area across BirkNext.";

    /// <summary>Provenance prefix of a suggested member a person kept (saved as confirmed membership).</summary>
    public const string SuggestedPrefix = "Suggested from ";

    public const string DeclaredJourneyModule = "Integration Journeys (declared for the project)";

    public static readonly IReadOnlyList<string> Views = ["setup", "systems", "connections", "areas", "create"];

    public static readonly IReadOnlyList<string> CreateSteps = ["choose", "select", "details", "review"];

    /// <summary>The view to show: <c>?view=</c> wins; the old <c>?tab=systems|connections|scopes</c> deep links map to the new views.</summary>
    public static string ViewOf(string? view, string? tab) =>
        view is { Length: > 0 } v && Views.Contains(v) ? v
        : tab switch { "systems" => "systems", "connections" => "connections", "scopes" => "areas", _ => "setup" };

    public static string CreateStepOf(string? step) => step is { Length: > 0 } s && CreateSteps.Contains(s) ? s : "choose";

    public static string StateWord(SetupStepState state) => state switch
    {
        SetupStepState.Available => "Available",
        SetupStepState.Completed => "Completed",
        SetupStepState.Current => "Current step",
        SetupStepState.NeedsAttention => "Needs attention",
        SetupStepState.NotStarted => "Not started",
        SetupStepState.Unavailable => "Unavailable",
        _ => "Error",
    };

    public static GuidedStepState Shared(SetupStepState state) => state switch
    {
        SetupStepState.Completed => GuidedStepState.Done,
        SetupStepState.Current => GuidedStepState.Current,
        SetupStepState.NeedsAttention or SetupStepState.Error => GuidedStepState.Blocked,
        SetupStepState.Unavailable => GuidedStepState.NotApplicable,
        _ => GuidedStepState.Waiting,
    };

    public static SemanticStatus Tone(SetupStepState state) => state switch
    {
        SetupStepState.Completed => SemanticStatus.Completed,
        SetupStepState.Current => SemanticStatus.Ready,
        SetupStepState.Available => SemanticStatus.Available,
        SetupStepState.NeedsAttention => SemanticStatus.NeedsReview,
        SetupStepState.Unavailable => SemanticStatus.NotApplicable,
        SetupStepState.Error => SemanticStatus.Failed,
        _ => SemanticStatus.NotRun,
    };

    private static GuidedStep Step(string id, string title, SetupStepState state, string explanation, string? evidence = null, string? reason = null) =>
        new(id, title, Shared(state), StateWord(state), explanation, evidence, DisabledReason: reason, StateKey: state.ToString(), Status: Tone(state));

    // ── Counts ─────────────────────────────────────────────────────────────────────────────────────────────────────────────

    public static List<SystemConnectionView> Active(SystemsOverviewView? o) => o is null ? [] : [.. o.Connections.Where(c => c.State != SystemConnectionState.Rejected)];

    /// <summary>Connections a person may want to look at: candidates and connections with conflicting evidence (the same count the connection
    /// list labels "needs review"). Optional — never required for creating or using a test area.</summary>
    public static int MayNeedReview(SystemsOverviewView? o) => Active(o).Count(c => c.State is SystemConnectionState.Candidate or SystemConnectionState.NeedsReview);

    public static int Confirmed(SystemsOverviewView? o) => Active(o).Count(c => c.State == SystemConnectionState.ConfirmedByPerson);

    public static int FromSource(SystemsOverviewView? o) => Active(o).Count(c => c.State == SystemConnectionState.FromSourceEvidence);

    /// <summary>Only connections runtime evidence actually saw.</summary>
    public static int ObservedAtRuntime(SystemsOverviewView? o) => Active(o).Count(c => c.State == SystemConnectionState.ObservedAtRuntime);

    public static ScopeRevisionView? Newest(ScopeDetail? d) => d?.Revisions.OrderByDescending(r => r.Number).FirstOrDefault();

    public static IEnumerable<ScopeMember> Members(ScopeDetail? d) => Newest(d)?.Members.Where(m => m.Inclusion != ScopeMemberInclusion.Excluded) ?? [];

    public static int Unresolved(ScopeDetail? d, IReadOnlyList<ScopeMemberStatus> checks) =>
        Members(d).Count(m => MemberState(m, checks.FirstOrDefault(c => c.Key == m.Key)) is AreaMemberState.Unresolved or AreaMemberState.Candidate);

    // ── Steps ──────────────────────────────────────────────────────────────────────────────────────────────────────────────

    public static IReadOnlyList<GuidedStep> Steps(SetupFacts f) => [Step1(f), Step2(f), Step3(f), Step4(f), Step5(f), Step6(f)];

    public static GuidedStep Step1(SetupFacts f)
    {
        const string title = "Understand your project";
        const string text = "See which systems and services BirkNext found in the imported source. This is for orientation; nothing here is required.";
        if (!f.HasProject) return Step("1", title, SetupStepState.Unavailable, text, reason: "Import or open a project first.");
        var o = f.Overview;
        return f.SourceState switch
        {
            SystemsPageState.Loading => Step("1", title, SetupStepState.NotStarted, text, "Loading systems…"),
            SystemsPageState.NoEnvironment => Step("1", title, SetupStepState.NeedsAttention, text, "No Target Environment is active, so no imported source can be read."),
            SystemsPageState.NoSource => Step("1", title, SetupStepState.NeedsAttention, text, "No source has been imported for this Target Environment yet."),
            SystemsPageState.NotInterpreted => Step("1", title, SetupStepState.NeedsAttention, text, "Source is imported but has not been analysed yet."),
            SystemsPageState.Analysing => Step("1", title, SetupStepState.NotStarted, text, "An analysis is running. Refresh when it has finished."),
            SystemsPageState.ZeroSystems => Step("1", title, SetupStepState.Available, text, "The latest analysis finished and found no systems or services. Test areas can still be built from requirements and scenarios."),
            SystemsPageState.Ready => Step("1", title, SetupStepState.Completed, text,
                $"{Plural(o!.Systems.Count, "system or service", "systems and services")} discovered{(o.InterpretedAt is { } at ? $" · analysed {at.ToLocalTime():g}" : "")}"),
            _ => Step("1", title, SetupStepState.Error, text, "The systems overview could not be loaded."),
        };
    }

    public static GuidedStep Step2(SetupFacts f)
    {
        const string title = "Explore connections";
        const string text = "Source-discovered connections are not proof that integrations work in a running environment. Reviewing them is optional and never required for creating a test area.";
        if (!f.HasProject || f.SourceState != SystemsPageState.Ready)
            return Step("2", title, SetupStepState.Unavailable, text, reason: "Available once the source has been analysed and systems were found.");
        var o = f.Overview;
        var found = Active(o).Count;
        if (found == 0) return Step("2", title, SetupStepState.Available, text, "No connections were found in the source.");
        var review = MayNeedReview(o);
        var observed = ObservedAtRuntime(o);
        var evidence = $"{Plural(found, "connection", "connections")} found · {Confirmed(o)} confirmed by a reviewer"
            + (review > 0 ? $" · {Plural(review, "connection may", "connections may")} need review (optional)" : "")
            + (observed > 0 ? $" · {observed} observed at runtime" : "");
        return Step("2", title, SetupStepState.Available, text, evidence);
    }

    public static GuidedStep Step3(SetupFacts f)
    {
        const string title = "Choose what to test";
        const string text = "A test area is a named selection of what you test together: systems, scenarios, requirements, journeys, contracts and Test Data.";
        if (!f.HasProject) return Step("3", title, SetupStepState.Unavailable, text, reason: "Import or open a project first.");
        if (!f.AreasLoaded) return Step("3", title, SetupStepState.NotStarted, text, "Loading test areas…");
        if (f.AreasError is not null && f.Areas.Count == 0) return Step("3", title, SetupStepState.Error, text, $"Test areas could not be loaded: {f.AreasError}");
        if (f.Areas.Count == 0) return Step("3", title, SetupStepState.Current, text, "No test areas have been created yet.");
        return Step("3", title, SetupStepState.Completed, text, Plural(f.Areas.Count, "test area", "test areas"));
    }

    public static GuidedStep Step4(SetupFacts f)
    {
        const string title = "Review selected coverage";
        const string text = "Check what the chosen test area covers and where each item came from.";
        if (!f.HasProject || f.Areas.Count == 0) return Step("4", title, SetupStepState.Unavailable, text, reason: "Create a test area first.");
        if (f.Selected is not { } d) return Step("4", title, SetupStepState.NotStarted, text, "Open a test area to review what it covers.");
        var members = Members(d).ToList();
        if (d.Scope.State == ScopeState.Archived) return Step("4", title, SetupStepState.Unavailable, text, reason: "This test area is archived (read-only).");
        if (members.Count == 0) return Step("4", title, SetupStepState.NeedsAttention, text, $"{d.Scope.Name} has no items yet.");
        var unresolved = Unresolved(d, f.SelectedChecks);
        var summary = $"{d.Scope.Name}: {Plural(members.Count, "item", "items")}";
        if (unresolved > 0) return Step("4", title, SetupStepState.NeedsAttention, text, $"{summary} · {Plural(unresolved, "item needs", "items need")} review");
        return Newest(d)?.State == ScopeRevisionState.Approved
            ? Step("4", title, SetupStepState.Completed, text, summary)
            : Step("4", title, SetupStepState.Available, text, $"{summary} · review the selection before approving it");
    }

    public static GuidedStep Step5(SetupFacts f)
    {
        const string title = "Save test area";
        const string text = "Saving keeps the selection as a draft revision. Approving a revision pins it so other modules can filter by it; it does not mean tests passed.";
        if (!f.HasProject || f.Areas.Count == 0) return Step("5", title, SetupStepState.NotStarted, text, "Saved at the end of Create test area.");
        if (f.Selected is not { } d) return Step("5", title, SetupStepState.NotStarted, text);
        var s = d.Scope;
        if (s.State == ScopeState.Archived) return Step("5", title, SetupStepState.Unavailable, text, reason: "This test area is archived (read-only).");
        return Step("5", title, SetupStepState.Completed, text, RevisionText(s));
    }

    public static GuidedStep Step6(SetupFacts f)
    {
        const string title = "Use your test area";
        const string text = "Open the modules that work with a test area. Some filter their lists by it; others only show it as context.";
        if (!f.HasProject || f.Areas.Count == 0 || f.Selected is null) return Step("6", title, SetupStepState.Unavailable, text, reason: "Save and approve a test area first.");
        var s = f.Selected.Scope;
        if (s.State == ScopeState.Archived) return Step("6", title, SetupStepState.Unavailable, text, reason: "This test area is archived and is no longer offered as a filter.");
        if (s.ApprovedRevision is null) return Step("6", title, SetupStepState.NotStarted, text, reason: "Approve a revision first: other modules filter only by approved revisions.");
        return Step("6", title, SetupStepState.Available, text, $"Modules filter by approved revision {s.ApprovedRevision}.");
    }

    /// <summary>"Saved — revision X" with its approval state, and a stale note when a newer draft is not approved yet.</summary>
    public static string RevisionText(ScopeSummary s) => s.State switch
    {
        ScopeState.Approved when s.ApprovedRevision != s.CurrentRevision =>
            $"Saved — revision {s.CurrentRevision} (draft). Modules still use approved revision {s.ApprovedRevision} until revision {s.CurrentRevision} is approved.",
        ScopeState.Approved => $"Saved — revision {s.CurrentRevision}, approved.",
        ScopeState.InReview => $"Saved — revision {s.CurrentRevision}, waiting for review.",
        ScopeState.Archived => $"Archived — revision {s.CurrentRevision}.",
        _ => $"Saved — revision {s.CurrentRevision} (draft, not approved yet).",
    };

    /// <summary>Setup progress (steps completed), kept separate from evidence confidence: a saved test area with unresolved connections is valid.</summary>
    public static (int Done, int Total) Progress(IReadOnlyList<GuidedStep> steps) => (steps.Count(s => s.State == GuidedStepState.Done), steps.Count);

    // ── Next action ────────────────────────────────────────────────────────────────────────────────────────────────────────

    public static SetupNextAction NextAction(SetupFacts f)
    {
        if (!f.HasProject) return new("import", "Import project", "Test areas belong to one project. Import or open a project first.", Href: "project-import");
        switch (f.SourceState)
        {
            case SystemsPageState.NoEnvironment:
                return new("environment", "Choose a Target Environment", "Systems and connections are read per Target Environment. You can still create a test area from Test Data and journeys.", Href: "target-environments");
            case SystemsPageState.NoSource:
                return new("import-source", "Import project source", "BirkNext finds systems, requirements and scenarios in the imported source.", Href: "project-import");
            case SystemsPageState.NotInterpreted:
                return new("analyze", "Analyze source", "The source is imported but not analysed yet. Run Analyze relationships once in Relationship Review.", Href: "correlation-review");
        }
        if (f.AreasLoaded && f.AreasError is not null && f.Areas.Count == 0) return new("retry", "Try again", "Test areas could not be loaded.", ActionId: "retry");
        if (f.AreasLoaded && f.Areas.Count == 0) return new("create", "Create test area", "No test areas have been created yet. Start from a suggestion or choose your own items.", ActionId: "create");
        if (f.Selected is not { } d) return new("open", "Open a test area", "Choose which test area to review and use.", ActionId: "areas");
        if (d.Scope.State == ScopeState.Archived) return new("create", "Create test area", $"{d.Scope.Name} is archived. Create a new test area or open another one.", ActionId: "create");
        var unresolved = Unresolved(d, f.SelectedChecks);
        if (unresolved > 0)
            return new("review-members", $"Review {Plural(unresolved, "unresolved item", "unresolved items")}", $"{d.Scope.Name} names items that are missing from their catalog or are still candidates.", ActionId: "review");
        if (d.Scope.ApprovedRevision is null || d.Scope.CurrentRevision != d.Scope.ApprovedRevision)
            return new("continue", "Continue editing", $"{d.Scope.Name} is a draft. Finish the selection, then approve it so other modules can filter by it.", ActionId: "edit");
        return new("plan", "Prepare test plan", $"{d.Scope.Name} is saved and approved. Prepare the test plan or review coverage for it.", Href: $"test-plan-suggestions?scope={d.Scope.Id}");
    }

    // ── Members ────────────────────────────────────────────────────────────────────────────────────────────────────────────

    public static AreaMemberState MemberState(ScopeMember m, ScopeMemberStatus? check) =>
        check is { Check: ScopeMemberCheck.Missing or ScopeMemberCheck.OtherContext } ? AreaMemberState.Unresolved
        : m.Inclusion == ScopeMemberInclusion.Candidate ? AreaMemberState.Candidate
        : m.Provenance.StartsWith(SuggestedPrefix, StringComparison.Ordinal) ? AreaMemberState.HumanConfirmed
        : AreaMemberState.ExplicitlySelected;

    public static string MemberStateLabel(AreaMemberState state) => state switch
    {
        AreaMemberState.ExplicitlySelected => "Explicitly selected",
        AreaMemberState.SuggestedFromSource => "Suggested from source",
        AreaMemberState.HumanConfirmed => "Human-confirmed",
        AreaMemberState.Candidate => "Candidate",
        _ => "Unresolved",
    };

    /// <summary>The kinds Step 4 groups by, in the order a tester reads them.</summary>
    public static readonly IReadOnlyList<ScopeMemberKind> ReviewKinds =
    [
        ScopeMemberKind.System, ScopeMemberKind.Scenario, ScopeMemberKind.Requirement, ScopeMemberKind.UserStory, ScopeMemberKind.Feature,
        ScopeMemberKind.Journey, ScopeMemberKind.Contract, ScopeMemberKind.TestDataAsset, ScopeMemberKind.CriticalE2EFlow,
    ];

    /// <summary>A member a person kept from a suggestion: confirmed membership whose provenance still says where the proposal came from.</summary>
    public static ScopeMember Keep(ScopeCatalogItem item, string suggestionProvenance) => new()
    {
        Kind = item.Kind, TargetId = item.TargetId, Label = item.Label, CatalogScope = item.CatalogScope, Inclusion = ScopeMemberInclusion.Confirmed,
        Provenance = $"{SuggestedPrefix}{suggestionProvenance}; kept by a person",
    };

    // ── Use links ──────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Only modules that read <c>?scope=&lt;id&gt;</c> (through the shared scope filter, which offers approved revisions only) are "filters";
    /// Relationship Review's impact trace and Integration Journeys show test areas as context and are never presented as a filter.
    /// </summary>
    public static IReadOnlyList<TestAreaUseLink> UseLinks(ScopeDetail d)
    {
        var id = Uri.EscapeDataString(d.Scope.Id.ToString());
        var links = new List<TestAreaUseLink>
        {
            new("test-plan-suggestions", "Test Plan Suggestions", $"test-plan-suggestions?scope={id}", "filters", "Shows the scenarios this test area names and the scenarios that verify its requirements."),
            new("test-coverage-review", "Test Coverage Review", $"test-coverage-review?scope={id}", "filters", "Narrows coverage to what this test area names."),
            new("test-data", "Test Data", $"test-data?scope={id}", "filters", "Shows the Test Data assets this test area names."),
            new("critical-e2e-regression", "Critical E2E Regression", $"critical-e2e-regression?scope={id}", "filters", "Shows the Critical E2E flows this test area names."),
            new("implementation-review", "Implementation Review", $"implementation-review?scope={id}", "filters", "Narrows feature readiness to this test area."),
            new("delivery-reporting", "Delivery — Functional Quality Scope", $"delivery-reporting?tab=scope&scope={id}", "filters", "Narrows the functional quality scope to this test area."),
            new("correlation-review", "Relationship Review (impact)", "correlation-review", "context", "An impact trace lists the test areas that name an impacted item."),
        };
        if (Members(d).Any(m => m.Kind == ScopeMemberKind.Journey))
            links.Add(new("integration-journeys", "Integration Journeys", "integration-journeys", "context", "Where the journeys this test area names are declared."));
        return links;
    }

    // ── Suggestions ────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Test areas proposed only from evidence the project's catalogs hold: requirements, user stories, scenarios and features grouped by the
    /// module/feature path of their source identity (a requirement's identity is <c>module/feature/id</c>), and each journey declared for this
    /// project (with the analysed systems its component chain names exactly). Nothing is proposed without evidence; no names are built in.
    /// </summary>
    public static IReadOnlyList<TestAreaSuggestion> Suggest(string projectKey, IReadOnlyDictionary<ScopeMemberKind, IReadOnlyList<ScopeCatalogItem>> catalogs,
        IReadOnlyList<SystemOverviewItem> systems, string? catalogScope)
    {
        var suggestions = new List<TestAreaSuggestion>();
        var groups = new Dictionary<string, List<ScopeCatalogItem>>(StringComparer.OrdinalIgnoreCase);
        var titles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kind in new[] { ScopeMemberKind.Requirement, ScopeMemberKind.UserStory, ScopeMemberKind.Scenario, ScopeMemberKind.Feature })
        {
            foreach (var item in catalogs.GetValueOrDefault(kind) ?? [])
            {
                if (GroupKey(item) is not { } key) continue;
                if (!groups.TryGetValue(key, out var list)) groups[key] = list = [];
                if (list.All(m => m.Kind != item.Kind || m.TargetId != item.TargetId)) list.Add(item);
                if (kind == ScopeMemberKind.Feature && !titles.ContainsKey(key) && item.Label.Length > 0) titles[key] = item.Label;
            }
        }
        foreach (var (key, items) in groups)
        {
            var path = string.Join(" / ", key.Split('/'));
            var kinds = items.Select(i => i.Kind).Distinct().OrderBy(k => k).Select(k => SystemsScopesPresentation.KindLabel(k, plural: true).ToLowerInvariant());
            suggestions.Add(new($"path:{key.ToLowerInvariant()}", titles.GetValueOrDefault(key) ?? path,
                $"the specification and source analysis ({string.Join(", ", kinds)} under {path})",
                $"Items whose source identity shares the module and feature path “{path}”.", [.. items.OrderBy(i => i.Kind).ThenBy(i => i.Label, StringComparer.OrdinalIgnoreCase)]));
        }
        var byName = systems.GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var prefix = $"Project {projectKey} ·";
        foreach (var journey in catalogs.GetValueOrDefault(ScopeMemberKind.Journey) ?? [])
        {
            if (journey.SourceModule != DeclaredJourneyModule || journey.Description is not { } description || !description.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var chain = description[prefix.Length..].Trim();
            var components = chain.Split('→', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var members = new List<ScopeCatalogItem> { journey };
            foreach (var c in components)
                if (byName.TryGetValue(c, out var s) && members.All(m => m.TargetId != s.Key))
                    members.Add(new ScopeCatalogItem { Kind = ScopeMemberKind.System, TargetId = s.Key, Label = s.Name, SourceModule = "Canonical interpretation (Source Analysis)", CatalogScope = catalogScope });
            suggestions.Add(new($"journey:{journey.TargetId}", journey.Label, "an integration journey declared for this project",
                chain.Length > 0 ? $"Its component chain: {chain}. Systems are proposed only when an analysed system has exactly that name." : "A journey declared for this project.", members));
        }
        return [.. suggestions.OrderByDescending(s => s.Members.Count).ThenBy(s => s.Title, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>The module/feature path of an item's source identity: the first two segments of a three-or-more segment path (the last one is
    /// the item's own id), or the first of two. Features name their own path. Null when the identity has no path, so nothing is grouped by a guess.</summary>
    public static string? GroupKey(ScopeCatalogItem item)
    {
        var raw = item.Description is { } d && d.Contains('/') ? d : item.TargetId[(item.TargetId.IndexOf('|') + 1)..];
        var parts = raw.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (item.Kind == ScopeMemberKind.Feature) return parts.Length >= 2 ? $"{parts[0]}/{parts[1]}" : null;
        return parts.Length >= 3 ? $"{parts[0]}/{parts[1]}" : parts.Length == 2 ? parts[0] : null;
    }

    public static string Plural(int n, string one, string many) => $"{n} {(n == 1 ? one : many)}";
}

/// <summary>The in-progress "Create test area" selection (held by the page so Back/Forward between create steps keeps it). Suggested items are
/// not members: only <see cref="Kept"/> is saved, as confirmed membership.</summary>
public sealed class TestAreaDraft
{
    public bool Started { get; set; }
    public TestAreaSuggestion? Suggestion { get; set; }
    /// <summary>Proposed by the chosen suggestion and not decided yet.</summary>
    public List<ScopeCatalogItem> Suggested { get; set; } = [];
    /// <summary>Explicitly selected or kept by a person: the members that will be saved.</summary>
    public List<ScopeMember> Kept { get; set; } = [];
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public ScopePurpose Purpose { get; set; } = ScopePurpose.Test;
    public IReadOnlyList<ScopeMemberStatus> Checks { get; set; } = [];

    public bool IsKept(ScopeCatalogItem item) => Kept.Any(m => m.Kind == item.Kind && m.TargetId == item.TargetId);

    /// <summary>Suggestions still waiting for a decision (kept ones are members now).</summary>
    public IEnumerable<ScopeCatalogItem> Pending => Suggested.Where(s => !IsKept(s));
}
