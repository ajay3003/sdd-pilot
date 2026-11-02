using BirkNext.DeliveryReporting;

namespace BirkNext.Web.Services;

public enum DeliveryWorkItemType { Feature, Story, Task }

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
}

public static class DeliveryWorkItems
{
    public static string FeatureId(string featureKey) => $"f:{featureKey}";
    public static string StoryId(string featureKey, string storyKey) => $"s:{featureKey}|{storyKey}";
    public static string TaskId(string featureKey, string taskKey) => $"t:{featureKey}|{taskKey}";

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
            foreach (var t in h.Tasks)
                items.Add(new(TaskId(f.FeatureKey, t.Key), DeliveryWorkItemType.Task, t.TaskId ?? t.Key, t.Text, f.Module, f.FeatureKey,
                    t.UserStoryKey is { } sk ? StoryId(f.FeatureKey, sk) : fid, t.UserStoryKey, t.PhaseKey, DeliveryReportingText.TaskChange(t.Change),
                    t.UnresolvedStoryReference is not null || t.Change is DeliveryTaskChange.Reopened, t.Completed ? "Checked" : "Unchecked", f) { Task = t });
        }
        return items;
    }

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
