using BirkNext.DeliveryReporting;

namespace BirkNext.Web.Services;

/// <summary>Semantic tone of a dashboard element: Ready (green), Progress (blue), Change (purple), Review (amber), Blocker (red, explicit blockers only),
/// Neutral (gray: not assessed / not recorded / none). Always shown with text; never colour alone.</summary>
public enum DeliveryDashboardTone { Ready, Progress, Change, Review, Blocker, Neutral }

public sealed record DeliveryKpi(string Key, string Value, string Label, string Detail, DeliveryDashboardTone Tone, string? Info = null);

/// <summary>A categorical quality dimension: a state word, never a percentage invented from a category.</summary>
public sealed record DeliveryQualityRow(string Key, string Label, string State, string Detail, DeliveryDashboardTone Tone, int? Filled = null, int? Of = null);

public sealed record DeliveryBar(string Key, string Label, int Value, DeliveryDashboardTone Tone, string Info);

public sealed record DeliveryAttentionGroupRow(DeliveryAttentionCategory Category, string Label, IReadOnlyList<DeliveryAttentionItem> Items);

/// <summary>A feature grid row: identity, checklist progress, requirement model, change badge and the strongest attention.</summary>
public sealed record DeliveryFeatureGridRow(BacklogItem Item, string Change, DeliveryDashboardTone ChangeTone, string RequirementModel, string? StrongestAttention, int Priority);

public sealed record DeliveryDashboardHeader(string Title, string? CurrentLabel, string? BaselineLabel, string Coverage, bool SameDay, bool OrderUnconfirmed);

/// <summary>
/// Delivery Overview as an executive dashboard, from the same frozen report (or newest snapshot) as Delivery Reports — never a separate calculation.
/// Current state and period change are separate; a checked checkbox is never implementation, QA, acceptance or deployment; nothing is invented
/// (no health score, velocity, confidence or workflow state).
/// </summary>
public static class DeliveryDashboard
{
    public const string ChecklistInfo = "Checked = checkbox state in tasks.md. Open = unchecked tasks. This is not QA verification, acceptance or deployment.";

    public static DeliveryDashboardHeader Header(DeliveryBacklogView view, WeeklyDeliveryReport? report, string projectName)
    {
        if (report is null)
            return new($"{projectName} — Delivery Overview", view.Source.SnapshotLabel, null, "Current snapshot only — comparison unavailable. Import or select a baseline and generate a report.", false, false);
        var sameDay = report.PeriodCoverage == DeliveryPeriodCoverage.SameDay;
        var unconfirmed = report.Direction is { State: DeliveryDirectionState.SameEffectiveDate } || sameDay && report.Direction is null;
        return new($"{projectName} — Delivery Overview", report.Current.Label, report.Baseline.Label,
            report.CoverageText.Length > 0 ? StripPrefix(report.CoverageText) : report.Coverage.ToString(), sameDay, unconfirmed);
    }

    /// <summary>The value under a "Coverage" heading does not repeat the word.</summary>
    private static string StripPrefix(string text) => text.StartsWith("Coverage: ", StringComparison.Ordinal) ? text["Coverage: ".Length..] : text;

    private static int Metric(WeeklyDeliveryReport? r, string key) => r?.Metrics.FirstOrDefault(m => m.Key == key)?.Value ?? 0;
    private static bool Has(WeeklyDeliveryReport? r, string key) => r?.Metrics.Any(m => m.Key == key) == true;

    /// <summary>Current delivery state: the current snapshot's inventory.</summary>
    public static IReadOnlyList<DeliveryKpi> Current(DeliveryBacklogView view)
    {
        var tasks = view.Items.Sum(i => i.TasksTotal);
        var done = view.Items.Sum(i => i.TasksCompleted);
        var review = view.Items.Count(i => i.Attention > 0 || i.Status == BacklogStatus.NeedsClarification);
        return
        [
            new("features", view.Items.Count.ToString(), "Features", $"{view.Items.Select(i => i.Module).Distinct().Count()} modules", DeliveryDashboardTone.Progress),
            new("checklist", tasks == 0 ? "–" : $"{100 * done / tasks}%", "Task checklist", tasks == 0 ? "No tasks.md checkboxes" : $"{done} / {tasks} checked", DeliveryDashboardTone.Progress, ChecklistInfo),
            new("open", (tasks - done).ToString(), "Open tasks", "Unchecked checkboxes in tasks.md", DeliveryDashboardTone.Neutral),
            new("in-progress", view.Count(BacklogStatus.InProgress).ToString(), "Checklist in progress", "Features with some tasks checked — not a schedule claim", DeliveryDashboardTone.Progress),
            new("review", review.ToString(), "Need review", "Features with attention items or open clarifications", review == 0 ? DeliveryDashboardTone.Neutral : DeliveryDashboardTone.Review),
            new("blocked", view.Count(BacklogStatus.Blocked).ToString(), "Blocked", "Explicitly marked Blocked in the feature documents", view.Count(BacklogStatus.Blocked) == 0 ? DeliveryDashboardTone.Neutral : DeliveryDashboardTone.Blocker),
        ];
    }

    /// <summary>Change this period. Inventory growth, matched completions and the net checked delta are separate — and never called "completed".</summary>
    public static IReadOnlyList<DeliveryBar> FeatureChanges(WeeklyDeliveryReport? report) => report is null ? [] :
    [
        new("new-features", "New features", Metric(report, "new-features"), DeliveryDashboardTone.Change, "Feature identity (module + folder) present now and absent in the baseline."),
        new("changed", "Changed features", report.Stories.Count(s => !s.NewFeature && !s.RemovedFeature && s.Changed), DeliveryDashboardTone.Change, "Retained features whose specification, tasks or status differ."),
        new("removed", "Removed features", Metric(report, "removed-features"), DeliveryDashboardTone.Review, "Removed — confirm intentional. A scope change, not a defect."),
        new("specs", "Specifications changed", Metric(report, "specifications-changed"), DeliveryDashboardTone.Change, "spec.md content differs in a retained feature."),
    ];

    public static IReadOnlyList<DeliveryBar> TaskChanges(WeeklyDeliveryReport? report)
    {
        if (report is null) return [];
        var current = report.Stories.Where(s => !s.RemovedFeature).Sum(s => s.CurrentTaskCompleted);
        var baseline = report.Stories.Where(s => !s.NewFeature).Sum(s => s.BaselineTaskCompleted);
        var addedInNewFeatures = report.Stories.Where(s => s.NewFeature).Sum(s => s.CurrentTaskTotal);
        return
        [
            new("tasks-added", "Tasks added", Metric(report, "tasks-added") + addedInNewFeatures, DeliveryDashboardTone.Change, "New task identities: in retained features plus the tasks of new features. Inventory growth, not work completed."),
            new("matched-checked", "Matched tasks checked this period", Metric(report, "tasks-completed"), DeliveryDashboardTone.Ready, "Tasks present in both snapshots that went from [ ] to [x]."),
            new("net-checked", "Net checked-task change", current - baseline, DeliveryDashboardTone.Progress, "Checked now minus checked in the baseline. Includes checked tasks of new features — not tasks completed this period."),
        ];
    }

    /// <summary>Feature distribution over statuses the source supports (only those present; never invented workflow states).</summary>
    public static IReadOnlyList<DeliveryBar> FeatureStatus(DeliveryBacklogView view) =>
    [
        .. new (BacklogStatus Status, DeliveryDashboardTone Tone, string Info)[]
        {
            (BacklogStatus.Planned, DeliveryDashboardTone.Neutral, "Specified; no task checked yet."),
            (BacklogStatus.InProgress, DeliveryDashboardTone.Progress, "Some tasks checked."),
            (BacklogStatus.ChecklistComplete, DeliveryDashboardTone.Ready, "All tasks checked — not verified, tested or accepted."),
            (BacklogStatus.NeedsClarification, DeliveryDashboardTone.Review, "Open [NEEDS CLARIFICATION] markers."),
            (BacklogStatus.Accepted, DeliveryDashboardTone.Ready, "Explicit “Status: Done” in the feature documents."),
            (BacklogStatus.Blocked, DeliveryDashboardTone.Blocker, "Explicitly marked Blocked."),
            (BacklogStatus.NotAssessed, DeliveryDashboardTone.Neutral, "No task list or no checkbox tasks."),
        }.Where(s => view.Count(s.Status) > 0).Select(s => new DeliveryBar(s.Status.ToString(), DeliveryBacklog.Label(s.Status), view.Count(s.Status), s.Tone, s.Info)),
    ];

    /// <summary>Specification · Implementation verification · QA verification · Acceptance · Blockers — states, never a 0% for unknown.</summary>
    public static IReadOnlyList<DeliveryQualityRow> Quality(DeliveryBacklogView view)
    {
        var specified = view.Items.Count(i => i.HasSpecification);
        var stated = view.Count(BacklogStatus.Accepted);
        var blocked = view.Count(BacklogStatus.Blocked);
        return
        [
            new("specification", "Specification", specified == view.Items.Count && specified > 0 ? "Present" : specified == 0 ? "Missing" : "Partial",
                $"{specified} of {view.Items.Count} features have spec.md. Existence only — not a specification approval.",
                specified == view.Items.Count && specified > 0 ? DeliveryDashboardTone.Ready : DeliveryDashboardTone.Review, specified, view.Items.Count),
            new("implementation", "Implementation verification", "Not recorded", "No implementation evidence is linked to these snapshots. Checked tasks are not verification.", DeliveryDashboardTone.Neutral),
            new("qa", "QA verification", "Not assessed", "No executed test evidence is linked to these snapshots.", DeliveryDashboardTone.Neutral),
            new("acceptance", "Acceptance", stated == 0 ? "Not recorded" : $"{stated} stated done", stated == 0 ? "No explicit acceptance state in the feature documents." : "Only an explicit “Status: Done” line counts.",
                stated == 0 ? DeliveryDashboardTone.Neutral : DeliveryDashboardTone.Ready),
            new("blockers", "Blockers", blocked == 0 ? "None" : blocked.ToString(), "Features explicitly marked Blocked.", blocked == 0 ? DeliveryDashboardTone.Ready : DeliveryDashboardTone.Blocker),
        ];
    }

    /// <summary>Genuine issues only, grouped: scope changes, traceability, quality gaps, snapshot/comparison warnings. Shared work is never here.</summary>
    public static IReadOnlyList<DeliveryAttentionGroupRow> Attention(WeeklyDeliveryReport? report)
    {
        if (report is null) return [];
        var groups = report.Attention.GroupBy(a => DeliveryAttentionCategories.Of(a.Kind))
            .Select(g => new DeliveryAttentionGroupRow(g.Key, DeliveryAttentionCategories.Label(g.Key), [.. g])).ToList();
        if (report.ComparisonWarnings.Count > 0)
            groups.Add(new(DeliveryAttentionCategory.DeliverySignal, "Snapshot and comparison warnings",
                [.. report.ComparisonWarnings.Select(w => new DeliveryAttentionItem(DeliveryAttentionKind.UnmappedArtifact, DeliveryAttentionSeverity.Review, "Comparison", w))]));
        return groups;
    }

    public static (string Label, DeliveryDashboardTone Tone) Change(DeliveryStoryRow? row) => row switch
    {
        null => ("—", DeliveryDashboardTone.Neutral),
        { RemovedFeature: true, RemovalUnconfirmed: true } => ("NEEDS CONFIRMATION", DeliveryDashboardTone.Review),
        { RemovedFeature: true } => ("REMOVED", DeliveryDashboardTone.Review),
        { NewFeature: true } => ("NEW", DeliveryDashboardTone.Change),
        { Changed: true } => ("CHANGED", DeliveryDashboardTone.Change),
        _ => ("UNCHANGED", DeliveryDashboardTone.Neutral),
    };

    /// <summary>Grid rows in priority order: blocked, needs review, new, changed, checklist in progress, then the rest.</summary>
    public static IReadOnlyList<DeliveryFeatureGridRow> Rows(DeliveryBacklogView view, WeeklyDeliveryReport? report)
    {
        var byKey = report?.Stories.ToDictionary(s => s.FeatureKey, StringComparer.Ordinal) ?? [];
        return
        [
            .. view.Items.Select(i =>
            {
                var row = byKey.GetValueOrDefault(i.FeatureKey);
                var (change, tone) = Change(row);
                var priority = i.Status == BacklogStatus.Blocked ? 0 : i.Attention > 0 || i.Status == BacklogStatus.NeedsClarification ? 1 : row?.NewFeature == true ? 2
                    : row?.Changed == true ? 3 : i.Status == BacklogStatus.InProgress ? 4 : 5;
                return new DeliveryFeatureGridRow(i, change, tone, RequirementModel(row, i), row?.Attention.FirstOrDefault(), priority);
            }).OrderBy(r => r.Priority).ThenBy(r => r.Item.Module, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Item.Title, StringComparer.OrdinalIgnoreCase),
        ];
    }

    /// <summary>"3 User Stories · 13 criteria", "9 functional groups · 61 tasks", "Scenario-based requirements", "No specification found" — the actual model.</summary>
    public static string RequirementModel(DeliveryStoryRow? row, BacklogItem item)
    {
        if (row?.Hierarchy is not { } h) return item.HasSpecification ? "Specification" : "No specification found";
        if (!h.HasTaskList && h.Stories.Count == 0) return "No task list found";
        var functional = h.Groups.Where(g => g.Kind == DeliveryTaskRelationship.FunctionalGroup).ToList();
        if (functional.Count > 0 && h.Stories.Count == 0) return $"{functional.Count} functional group{(functional.Count == 1 ? "" : "s")} · {functional.Sum(g => g.TaskTotal)} tasks";
        var scenarios = h.Stories.Count(s => s.Kind == "Scenario");
        if (scenarios > 0 && scenarios == h.Stories.Count) return $"Scenario-based requirements · {scenarios} scenario{(scenarios == 1 ? "" : "s")}";
        var stories = h.Stories.Count(s => s.Kind != "Scenario" && !s.Removed && s.Change != DeliveryItemChange.Removed);
        if (stories == 0) return item.HasSpecification ? "No user stories in spec.md" : "No specification found";
        return $"{stories} User Stor{(stories == 1 ? "y" : "ies")} · {h.CriteriaCurrent} criteria";
    }

    public static string ToneClass(DeliveryDashboardTone tone) => tone.ToString().ToLowerInvariant();
}
