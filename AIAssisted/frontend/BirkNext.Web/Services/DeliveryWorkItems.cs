using BirkNext.DeliveryReporting;

namespace BirkNext.Web.Services;

/// <summary>Group = a requirement-model group that is not a formal User Story: functional group, scenario group, shared &amp; foundational work, a story
/// phase, removed-story or unresolved references (report v4).</summary>
public enum DeliveryWorkItemType { Feature, Story, Task, Group }

/// <summary>Board columns: evidence-based change groups only — no invented workflow state ("Ready for test") imitating a tracker.</summary>
public enum DeliveryBoardColumn { NewThisPeriod, ChangedThisPeriod, Unchanged, RemovedFromScope, RequiresReview }

/// <summary>
/// One work item of the canonical Module → Feature → User Story → Task model, built only from the frozen report (so a historical report renders
/// its own evidence). A task's story link exists only through an explicit [USn] reference; an unresolved reference is kept as written.
/// </summary>
public sealed record DeliveryWorkItem(
    string Id, DeliveryWorkItemType Type, string SourceId, string Title, string Module, string FeatureKey, string? ParentId,
    string? StoryKey, string? PhaseKey, string Change, bool Attention, string Progress, DeliveryStoryRow Feature)
{
    public DeliveryUserStoryRow? Story { get; init; }
    public DeliveryTaskRow? Task { get; init; }
    public DeliveryWorkGroupRow? Group { get; init; }
}

public static class DeliveryWorkItems
{
    public static string FeatureId(string featureKey) => $"f:{featureKey}";
    public static string StoryId(string featureKey, string storyKey) => $"s:{featureKey}|{storyKey}";
    public static string TaskId(string featureKey, string taskKey) => $"t:{featureKey}|{taskKey}";
    public static string GroupId(string featureKey, DeliveryTaskRelationship kind, string key) => $"g:{featureKey}|{kind}|{key}";

    /// <summary>The badge name of a relationship — the same words in the legend, the tree, the list and the detail panel.</summary>
    public static string RelationshipLabel(DeliveryTaskRelationship kind) => BirkNext.Interpretation.WorkItemInterpreter.Label(kind);

    public static string ConfidenceLabel(DeliveryMappingConfidence confidence) => confidence switch
    {
        DeliveryMappingConfidence.Explicit => "Explicit reference",
        DeliveryMappingConfidence.PhaseTitle => "Linked by phase title",
        DeliveryMappingConfidence.GroupLevel => "Group-level",
        _ => "Not linked",
    };

    /// <summary>The legend: every work-item type and relationship, with why BirkNext shows it.</summary>
    public static readonly IReadOnlyList<(string Name, string Meaning)> Legend =
    [
        ("User Story", "Explicit story defined in spec.md; tasks reference it with [USn]."),
        ("Scenario", "Specification written as scenarios instead of user stories."),
        ("Functional group", "Derived from stable [USn] task groupings when spec.md defines no matching User Story."),
        ("Scenario group", "Tasks grouped by [USn] in a scenario-based specification; linked to a scenario only with phase-title evidence."),
        ("Story phase", "Untagged tasks inside a tasks.md phase titled for a story."),
        ("Shared & Foundational", "Setup, foundational, polish and cross-cutting work not owned by one story — normal, not a defect."),
        ("Removed story reference", "Tasks still reference a story the specification marks removed — needs review."),
        ("Unresolved reference", "Tasks reference a story the specification does not define — needs review."),
    ];

    /// <summary>"004-settings-devmode" from "frontend::004-settings-devmode".</summary>
    public static string FeatureSourceId(string featureKey) => featureKey.Contains("::", StringComparison.Ordinal) ? featureKey[(featureKey.IndexOf("::", StringComparison.Ordinal) + 2)..] : featureKey;

    public static IReadOnlyList<DeliveryWorkItem> Build(WeeklyDeliveryReport report)
    {
        var items = new List<DeliveryWorkItem>();
        foreach (var f in report.Stories)
        {
            var fid = FeatureId(f.FeatureKey);
            var h = f.Hierarchy;
            items.Add(new(fid, DeliveryWorkItemType.Feature, FeatureSourceId(f.FeatureKey), f.Title, f.Module, f.FeatureKey, null, null, null,
                FeatureChange(f), f.HasActiveAttention || f.Attention.Count > 0, FeatureProgress(f), f));
            if (h is null) continue;
            foreach (var s in h.Stories)
                items.Add(new(StoryId(f.FeatureKey, s.Key), DeliveryWorkItemType.Story, s.Key, s.Title, f.Module, f.FeatureKey, fid, s.Key, s.PhaseKeys.FirstOrDefault(),
                    DeliveryReportingText.Change(s.Change), s.Change != DeliveryItemChange.Unchanged && !f.NewFeature && !f.RemovedFeature,
                    s.TaskTotal == 0 ? "No linked tasks" : $"{s.TaskCompleted}/{s.TaskTotal}", f) { Story = s });
            foreach (var g in h.Groups.Where(g => g.Kind != DeliveryTaskRelationship.ExplicitStory))
                // The id column carries the group key (US1); the title says what the group is without repeating it.
                items.Add(new(GroupId(f.FeatureKey, g.Kind, g.Key), DeliveryWorkItemType.Group, g.Kind == DeliveryTaskRelationship.SharedFoundational ? "" : g.Key,
                    g.Title.StartsWith(g.Key + " — ", StringComparison.Ordinal) ? g.Title[(g.Key.Length + 3)..] : g.Title,
                    f.Module, f.FeatureKey, fid, null, null, "", g.NeedsReview, $"{g.TaskCompleted}/{g.TaskTotal}", f) { Group = g });
            foreach (var t in h.Tasks)
            {
                // A task hangs under its group: its User Story only through an explicit reference, otherwise its requirement-model group (report v4).
                var grouped = t.Relationship is { } r && r != DeliveryTaskRelationship.ExplicitStory && h.Groups.Any(g => g.Kind == r && g.Key == (t.GroupKey ?? r.ToString()));
                items.Add(new(TaskId(f.FeatureKey, t.Key), DeliveryWorkItemType.Task, t.TaskId ?? t.Key, t.Text, f.Module, f.FeatureKey,
                    grouped ? GroupId(f.FeatureKey, t.Relationship!.Value, t.GroupKey ?? t.Relationship.Value.ToString()) : t.UserStoryKey is { } sk ? StoryId(f.FeatureKey, sk) : fid,
                    grouped ? null : t.UserStoryKey, t.PhaseKey, DeliveryReportingText.TaskChange(t.Change),
                    t.UnresolvedStoryReference is not null || t.Relationship is DeliveryTaskRelationship.RemovedStoryReference || t.Change is DeliveryTaskChange.Reopened,
                    t.Completed ? "Checked" : "Unchecked", f) { Task = t });
            }
        }
        return items;
    }

    /// <summary>Only in the baseline: a removed feature, a story the specification marks removed or that disappeared, a removed task.</summary>
    public static bool IsRemoved(DeliveryWorkItem i) => i.Feature.RemovedFeature || i.Type switch
    {
        DeliveryWorkItemType.Story => i.Story!.Removed || i.Story.Change == DeliveryItemChange.Removed,
        DeliveryWorkItemType.Task => i.Task!.Change == DeliveryTaskChange.Removed,
        _ => false,
    };

    /// <summary>
    /// A genuine review need of the current state — feature attention, an unresolved or removed-story reference, a group whose reference cannot be
    /// resolved. A change between snapshots (story changed, task reopened) is a comparison fact, not a current review need.
    /// </summary>
    public static bool NeedsReview(DeliveryWorkItem i) => i.Type switch
    {
        DeliveryWorkItemType.Feature => i.Feature.HasActiveAttention || i.Feature.Attention.Count > 0,
        DeliveryWorkItemType.Group => i.Group!.NeedsReview,
        DeliveryWorkItemType.Task => i.Task!.UnresolvedStoryReference is not null || i.Task.Relationship is DeliveryTaskRelationship.RemovedStoryReference,
        _ => false,
    };

    public static string FeatureChange(DeliveryStoryRow f) => f.RemovedFeature ? (f.RemovalUnconfirmed ? "Missing (unconfirmed removal)" : "Removed — baseline only")
        : f.NewFeature ? "New this period" : DeliveryReportingText.Movement(f.Movement);

    /// <summary>Measured progress only: "No task list found" / "No checkbox tasks" are said in words, never 0/0.</summary>
    public static string FeatureProgress(DeliveryStoryRow f)
    {
        var hasList = f.Hierarchy?.HasTaskList ?? f.Evidence.TasksPath is not null;
        var (done, total) = f.RemovedFeature ? (f.BaselineTaskCompleted, f.BaselineTaskTotal) : (f.CurrentTaskCompleted, f.CurrentTaskTotal);
        return !hasList ? "No task list found" : total == 0 ? "No checkbox tasks" : $"{done}/{total}";
    }

    public static DeliveryBoardColumn Column(DeliveryStoryRow f) =>
        f.RemovedFeature ? (f.RemovalUnconfirmed ? DeliveryBoardColumn.RequiresReview : DeliveryBoardColumn.RemovedFromScope)
        : f.NewFeature ? DeliveryBoardColumn.NewThisPeriod
        : f.HasActiveAttention || f.Movement is DeliveryMovement.NeedsReview or DeliveryMovement.Regressed ? DeliveryBoardColumn.RequiresReview
        : f.Changed ? DeliveryBoardColumn.ChangedThisPeriod
        : DeliveryBoardColumn.Unchanged;

    public static string ColumnLabel(DeliveryBoardColumn column) => column switch
    {
        DeliveryBoardColumn.NewThisPeriod => "New this period",
        DeliveryBoardColumn.ChangedThisPeriod => "Changed this period",
        DeliveryBoardColumn.RemovedFromScope => "Removed from current scope (baseline only)",
        DeliveryBoardColumn.RequiresReview => "Requires review",
        _ => "Unchanged",
    };

    /// <summary>Status provenance: an explicit status line in the feature documents, or derived from tasks.md checkboxes (never assumed).</summary>
    public static string StatusProvenance(DeliveryStatus? status) => status is not { } s ? "No status (feature not present)"
        : DeliveryReportingText.IsExplicitOnly(s) ? $"{DeliveryReportingText.Status(s)} — explicit status line in the feature documents"
        : $"{DeliveryReportingText.Status(s)} — derived from task checkboxes (not a delivery decision)";

    /// <summary>Why a task sits where it does, in source terms. Claims only what the source supports.</summary>
    public static IReadOnlyList<string> TaskExplanation(DeliveryWorkItem item, IReadOnlyList<DeliveryPhaseRow> phases)
    {
        var t = item.Task!;
        var lines = new List<string> { $"Belongs to feature {FeatureSourceId(item.FeatureKey)} because it is listed in {t.SourcePath ?? item.Feature.Evidence.TasksPath ?? "the feature's tasks.md"}." };
        lines.Add(t.UserStoryKey is { } us ? $"Linked to {us} through the explicit [{us}] reference in the task line."
            : t.UnresolvedStoryReference is { } raw ? $"Parent story unresolved: the task references [{raw}], which is not a user story in the specification. Not re-pointed."
            : "User Story: Not assigned — the task line has no [USn] reference.");
        lines.Add(t.PhaseKey is { } pk && phases.FirstOrDefault(p => p.Key == pk) is { } phase ? $"In phase \"{phase.Title}\" (source heading)." : "Not under a \"Phase N\" heading.");
        lines.Add(t.BaselineCompleted switch
        {
            null when t.Change == DeliveryTaskChange.New => $"New this period ({(t.Completed ? "already checked — inventory, not completed this period" : "unchecked")}).",
            null => $"{(t.Completed ? "Checked" : "Unchecked")} in this snapshot (no baseline comparison for this feature).",
            false when t.Completed => "Completion transition: [ ] in baseline → [x] in current — completed this period.",
            true when !t.Completed => "Reopened: [x] in baseline → [ ] in current.",
            var was => $"No completion transition: {(was == true ? "[x]" : "[ ]")} in both snapshots.",
        });
        if (t.Change == DeliveryTaskChange.Removed) lines.Add("Removed: present in the baseline only (never counted as reopened).");
        if (t.Parallel) lines.Add("Marked [P] (parallelisable) in the source.");
        return lines;
    }
}

/// <summary>
/// The shared work-item explorer serves two pages with different questions. Current state (Delivery Overview): what the current snapshot holds —
/// removed items and period-change classifications are not shown. Comparison (Delivery Reports → Stories): what changed between the baseline and the
/// current snapshot — the Change Board, change filter and change badges apply. Checklist state, change classification and review need stay
/// separate dimensions in both modes.
/// </summary>
public enum DeliveryExplorerMode { CurrentState, Comparison }

/// <summary>Typed mode options: every mode difference of the explorer is decided here, not by conditionals scattered through the markup.</summary>
public sealed record DeliveryExplorerOptions(DeliveryExplorerMode Mode)
{
    public static readonly DeliveryExplorerOptions CurrentState = new(DeliveryExplorerMode.CurrentState);
    public static readonly DeliveryExplorerOptions Comparison = new(DeliveryExplorerMode.Comparison);

    public bool ShowChanges => Mode == DeliveryExplorerMode.Comparison;
    /// <summary>Removed features, stories and tasks exist only in the baseline: the current inventory leaves them out.</summary>
    public bool IncludeRemoved => Mode == DeliveryExplorerMode.Comparison;

    /// <summary>
    /// Feature-level backlog facts (Delivery Overview): each feature's backlog status, checklist, requirement model, attention and priority, keyed by
    /// feature key. When set the explorer offers the Features view — the backlog list of the Overview, now one view of the shared explorer.
    /// </summary>
    public IReadOnlyDictionary<string, DeliveryFeatureGridRow>? Backlog { get; init; }
    /// <summary>Why a feature is in the selected Source / Functional scope (feature key → reason), shown with the feature.</summary>
    public IReadOnlyDictionary<string, string>? Why { get; init; }
    /// <summary>The page's own filters (scope, state, attention): only these features and their work items are in the inventory. Null = all.</summary>
    public IReadOnlySet<string>? FeatureKeys { get; init; }

    public IReadOnlyList<(string Key, string Label)> Views => ShowChanges
        ? [("hierarchy", "Hierarchy"), ("list", "List"), ("board", "Change Board"), ("phases", "Phases")]
        : Backlog is null ? [("hierarchy", "Hierarchy"), ("list", "List"), ("phases", "Phases")]
        : [("features", "Features"), ("hierarchy", "Hierarchy"), ("list", "List"), ("phases", "Phases")];

    /// <summary>The view a fresh explorer opens with: the backlog's Features view when there is one, else the hierarchy.</summary>
    public string DefaultView => Backlog is null ? "hierarchy" : "features";

    /// <summary>The item is part of this mode's inventory (and of the page's feature filter).</summary>
    public bool Includes(DeliveryWorkItem item) => (IncludeRemoved || !DeliveryWorkItems.IsRemoved(item)) && (FeatureKeys is null || FeatureKeys.Contains(item.FeatureKey));

    /// <summary>"Needs review" of this mode: in comparison mode a change that needs a look counts too; in current state only genuine review needs.</summary>
    public bool Attention(DeliveryWorkItem item) => ShowChanges ? item.Attention : DeliveryWorkItems.NeedsReview(item);

    /// <summary>The other page's view of the same canonical item: changes for it (Reports) or its current state (Overview).</summary>
    public string CrossPageLabel => ShowChanges ? "View current state in Delivery Overview" : "View changes in Delivery Reports";

    public static string CrossPageHref(DeliveryExplorerMode from, Guid projectId, Guid reportId, string itemId) => from == DeliveryExplorerMode.Comparison
        ? $"delivery-overview?project={projectId}&item={Uri.EscapeDataString(itemId)}"
        : $"delivery-reporting?project={projectId}&tab=stories&report={reportId}&item={Uri.EscapeDataString(itemId)}";
}

/// <summary>The explorer's filter, view and sort — the state that lives in the URL (wi-* keys) and is remembered per project, report and mode.</summary>
public sealed record DeliveryExplorerFilter(string View = "", string Search = "", string Module = "", string Type = "", string Change = "", bool Review = false,
    string Sort = "id", bool Descending = false);

/// <summary>
/// Round-trip of the explorer state through the URL: compact keys, values that are view keys, enum names, module names and plain search text — never
/// ids of other projects, never serialized objects. <see cref="Validate"/> drops what does not fit the current report and mode (a module this report
/// does not have, a change filter in current-state mode), so a stale link never shows an empty explorer without saying why.
/// </summary>
public static class DeliveryExplorerState
{
    public static readonly IReadOnlyList<string> QueryKeys = ["wi-view", "wi-q", "wi-module", "wi-type", "wi-change", "wi-review", "wi-sort", "wi-desc"];
    public static readonly IReadOnlyList<string> Types = ["Feature", "Story", "Group", "Task"];
    public static readonly IReadOnlyList<string> Sorts = ["id", "type", "title", "parent", "module", "phase", "progress", "change", "attention", "status"];

    public static IReadOnlyDictionary<string, object?> ToQuery(DeliveryExplorerFilter f, string defaultView) => new Dictionary<string, object?>
    {
        ["wi-view"] = f.View.Length == 0 || f.View == defaultView ? null : f.View,
        ["wi-q"] = f.Search.Trim().Length == 0 ? null : f.Search.Trim(),
        ["wi-module"] = f.Module.Length == 0 ? null : f.Module,
        ["wi-type"] = f.Type.Length == 0 ? null : f.Type,
        ["wi-change"] = f.Change.Length == 0 ? null : f.Change,
        ["wi-review"] = f.Review ? "1" : null,
        ["wi-sort"] = f.Sort is "" or "id" ? null : f.Sort,
        ["wi-desc"] = f.Descending ? "1" : null,
    };

    public static DeliveryExplorerFilter FromQuery(IReadOnlyDictionary<string, string?> q)
    {
        string S(string key) => q.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : "";
        return new(S("wi-view"), S("wi-q"), S("wi-module"), S("wi-type"), S("wi-change"), S("wi-review") == "1", S("wi-sort") is { Length: > 0 } sort ? sort : "id", S("wi-desc") == "1");
    }

    public static bool HasAny(IReadOnlyDictionary<string, string?> q) => QueryKeys.Any(k => q.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v));

    /// <summary>The filter as this report and mode can apply it, and a note for every part that was dropped.</summary>
    public static (DeliveryExplorerFilter Filter, IReadOnlyList<string> Dropped) Validate(DeliveryExplorerFilter f, DeliveryExplorerOptions options, IReadOnlyCollection<string> modules)
    {
        var dropped = new List<string>();
        var view = options.Views.Any(v => v.Key == f.View) ? f.View : options.DefaultView;
        if (f.View.Length > 0 && view != f.View) dropped.Add($"The view “{f.View}” is not available here.");
        var module = f.Module;
        if (module.Length > 0 && !modules.Contains(module)) { dropped.Add($"Module “{module}” is not in this report — the module filter was cleared."); module = ""; }
        var type = Types.Contains(f.Type) ? f.Type : "";
        if (f.Type.Length > 0 && type.Length == 0) dropped.Add($"Unknown work-item type “{f.Type}” — the type filter was cleared.");
        var change = options.ShowChanges && f.Change is "changed" or "unchanged" ? f.Change : "";
        if (f.Change.Length > 0 && change.Length == 0) dropped.Add(options.ShowChanges ? $"Unknown change filter “{f.Change}”." : "Change filters apply to comparisons only (Delivery Reports).");
        var sort = Sorts.Contains(f.Sort) ? f.Sort : "id";
        return (new(view, f.Search, module, type, change, f.Review, sort, f.Descending), dropped);
    }

    /// <summary>The query of a URL as decoded key → value (the last value of a repeated key wins).</summary>
    public static IReadOnlyDictionary<string, string?> Parse(string uri)
    {
        var query = uri.IndexOf('?') is var i and >= 0 ? uri[(i + 1)..] : "";
        if (query.IndexOf('#') is var h and >= 0) query = query[..h];
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            result[Uri.UnescapeDataString(kv[0].Replace('+', ' '))] = kv.Length > 1 ? Uri.UnescapeDataString(kv[1].Replace('+', ' ')) : null;
        }
        return result;
    }
}

/// <summary>
/// Per-session memory of explorer state (one instance per browser tab): the query an explorer last had for one key (project + report + mode, or a
/// Functional Quality Scope project), so leaving a tab or page and coming back through a link without that state restores it. The URL wins when it
/// carries state. Never stored beyond the session; never holds secrets (only the same keys the URL shows).
/// </summary>
public sealed class ExplorerViewStateStore
{
    private readonly Dictionary<string, IReadOnlyDictionary<string, string?>> _state = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, string?>? Get(string key) => _state.GetValueOrDefault(key);

    public void Set(string key, IReadOnlyDictionary<string, object?> query)
    {
        var values = query.Where(kv => kv.Value is not null && kv.Value.ToString() is { Length: > 0 })
            .ToDictionary(kv => kv.Key, kv => (string?)kv.Value!.ToString(), StringComparer.Ordinal);
        if (values.Count == 0) _state.Remove(key);
        else _state[key] = values;
    }
}
