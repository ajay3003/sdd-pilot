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

    public IReadOnlyList<(string Key, string Label)> Views => ShowChanges
        ? [("hierarchy", "Hierarchy"), ("list", "List"), ("board", "Change Board"), ("phases", "Phases")]
        : [("hierarchy", "Hierarchy"), ("list", "List"), ("phases", "Phases")];

    /// <summary>The item is part of this mode's inventory.</summary>
    public bool Includes(DeliveryWorkItem item) => IncludeRemoved || !DeliveryWorkItems.IsRemoved(item);

    /// <summary>"Needs review" of this mode: in comparison mode a change that needs a look counts too; in current state only genuine review needs.</summary>
    public bool Attention(DeliveryWorkItem item) => ShowChanges ? item.Attention : DeliveryWorkItems.NeedsReview(item);

    /// <summary>The other page's view of the same canonical item: changes for it (Reports) or its current state (Overview).</summary>
    public string CrossPageLabel => ShowChanges ? "View current state in Delivery Overview" : "View changes in Delivery Reports";

    public static string CrossPageHref(DeliveryExplorerMode from, Guid projectId, Guid reportId, string itemId) => from == DeliveryExplorerMode.Comparison
        ? $"delivery-overview?project={projectId}&explore=items&item={Uri.EscapeDataString(itemId)}"
        : $"delivery-reporting?project={projectId}&tab=stories&report={reportId}&item={Uri.EscapeDataString(itemId)}";
}
