using BirkNext.DeliveryReporting;

namespace BirkNext.Web.Services;

/// <summary>
/// Delivery status of a backlog feature. Each value needs its own evidence; none is derived from another. ChecklistComplete is the Markdown
/// task checklist only; ImplementationVerified and QaVerified need verification evidence that snapshots do not carry.
/// </summary>
public enum BacklogStatus { Planned, InProgress, ChecklistComplete, ImplementationVerified, QaVerified, Accepted, Blocked, NeedsClarification, NotAssessed }

public sealed record BacklogItem(string FeatureKey, string Title, string Module, BacklogStatus Status, string StatusDetail, int Stories, int Criteria,
    int TasksCompleted, int TasksTotal, int Phases, DeliveryMovement? Movement)
{
    public int Attention { get; init; }
    public bool HasSpecification { get; init; }
    public int OpenTasks => TasksTotal - TasksCompleted;
}

/// <summary>Where the backlog was read from: the newest frozen weekly report, or (without one) the newest imported snapshot.</summary>
public sealed record BacklogSource(string Kind, string SnapshotLabel, DateTimeOffset EffectiveAt, DeliverySourceType SourceType, DeliveryAzureDevOpsSource? AzureDevOps,
    string? BaselineLabel, Guid? ReportId, string? WeekLabel);

public sealed record DeliveryBacklogView(BacklogSource Source, IReadOnlyList<BacklogItem> Items, IReadOnlyList<string> RemovedFeatures)
{
    public int Count(BacklogStatus status) => Items.Count(i => i.Status == status);
}

/// <summary>One delivery readiness dimension: a value only when evidence states it, otherwise why it is not available.</summary>
public sealed record DeliveryReadinessDimension(string Key, string Label, string Value, string Detail, bool Available);

/// <summary>
/// The virtual SDD backlog (Delivery Overview): every feature of a delivery project with its user stories, acceptance criteria, phases, tasks
/// and one delivery status, from explicit evidence in the snapshot only. All tasks checked is "Task checklist complete" — never implemented,
/// verified or accepted. Accepted needs an explicit "Status: Done" in the feature documents. Implementation and QA verification need evidence
/// snapshots do not carry, so no feature reaches them from snapshot data.
/// </summary>
public static class DeliveryBacklog
{
    public static readonly BacklogStatus[] Columns =
        [BacklogStatus.Planned, BacklogStatus.InProgress, BacklogStatus.ChecklistComplete, BacklogStatus.ImplementationVerified, BacklogStatus.QaVerified, BacklogStatus.Accepted, BacklogStatus.Blocked];

    /// <summary>Statuses no snapshot can establish: shown as "–" with this explanation, never as 0.</summary>
    public static bool NotFromSnapshots(BacklogStatus status) => status is BacklogStatus.ImplementationVerified or BacklogStatus.QaVerified;

    public const string VerifiedNotAvailable = "Not available: snapshots contain no verification evidence. A feature is never verified because its tasks are checked.";

    public static BacklogStatus Status(DeliveryStatus? status) => status switch
    {
        DeliveryStatus.NotStarted => BacklogStatus.Planned,
        DeliveryStatus.InProgress => BacklogStatus.InProgress,
        DeliveryStatus.TasksComplete or DeliveryStatus.ReadyForTest or DeliveryStatus.Testing => BacklogStatus.ChecklistComplete,
        DeliveryStatus.Done => BacklogStatus.Accepted,
        DeliveryStatus.Blocked => BacklogStatus.Blocked,
        DeliveryStatus.NeedsClarification => BacklogStatus.NeedsClarification,
        _ => BacklogStatus.NotAssessed,
    };

    public static string Detail(DeliveryStatus? status) => status switch
    {
        DeliveryStatus.NotStarted => "Specified; no task checked yet",
        DeliveryStatus.InProgress => "Some tasks checked",
        DeliveryStatus.TasksComplete => "All tasks checked — not verified, tested or accepted",
        DeliveryStatus.ReadyForTest => "All tasks checked · stated ready for test",
        DeliveryStatus.Testing => "All tasks checked · stated in test",
        DeliveryStatus.Done => "Stated Done in the feature documents",
        DeliveryStatus.Blocked => "Stated blocked in the feature documents",
        DeliveryStatus.NeedsClarification => "Open clarification markers",
        _ => "Not enough evidence to assess",
    };

    public static string Label(BacklogStatus status) => status switch
    {
        BacklogStatus.Planned => "Planned",
        BacklogStatus.InProgress => "In progress",
        BacklogStatus.ChecklistComplete => "Task checklist complete",
        BacklogStatus.ImplementationVerified => "Implementation verified",
        BacklogStatus.QaVerified => "QA verified",
        BacklogStatus.Accepted => "Accepted",
        BacklogStatus.Blocked => "Blocked",
        BacklogStatus.NeedsClarification => "Needs clarification",
        _ => "Unknown",
    };

    /// <summary>From a frozen weekly report: its current snapshot's features (removed ones listed apart, never as backlog items).</summary>
    public static DeliveryBacklogView FromReport(WeeklyDeliveryReport report) => new(
        new("report", report.Current.Label, report.Current.EffectiveAt, report.Current.SourceType, report.Current.AzureDevOps, report.Baseline.Label, report.ReportId, report.WeekLabel),
        [.. report.Stories.Where(s => !s.RemovedFeature).Select(s => new BacklogItem(s.FeatureKey, s.Title, s.Module, Status(s.CurrentStatus), Detail(s.CurrentStatus),
            s.Hierarchy?.Stories.Count(u => u.Change != DeliveryItemChange.Removed) ?? 0,
            s.Hierarchy is { } h ? h.CriteriaCurrent : 0,
            s.CurrentTaskCompleted, s.CurrentTaskTotal, s.Hierarchy?.Phases.Count(p => p.Change != DeliveryItemChange.Removed) ?? 0, s.Movement)
            { Attention = s.Attention.Count, HasSpecification = s.Evidence.SpecPath is not null })
            .OrderBy(i => i.Module, StringComparer.OrdinalIgnoreCase).ThenBy(i => i.Title, StringComparer.OrdinalIgnoreCase)],
        [.. report.Stories.Where(s => s.RemovedFeature).Select(s => s.Title)]);

    /// <summary>From a snapshot without a report: features and task progress only (story/criteria counts need a report).</summary>
    public static DeliveryBacklogView FromSnapshot(DeliverySnapshotDetail detail) => new(
        new("snapshot", detail.Summary.Label, detail.Summary.EffectiveAt, detail.Summary.SourceType, detail.Summary.AzureDevOps, null, null, null),
        [.. detail.Features.Select(f =>
        {
            DeliveryStatus? status = Enum.TryParse<DeliveryStatus>(f.Status, out var s) ? s : null;
            return new BacklogItem(f.Key, f.Title, f.Module, Status(status), Detail(status), 0, 0, f.TaskCompleted, f.TaskTotal, 0, null) { HasSpecification = f.SpecPath is not null };
        }).OrderBy(i => i.Module, StringComparer.OrdinalIgnoreCase).ThenBy(i => i.Title, StringComparer.OrdinalIgnoreCase)],
        []);

    /// <summary>
    /// The compact delivery readiness: specification, checklist, implementation verification, QA verification, acceptance and blockers. Each is
    /// computed from the backlog items only; verification dimensions say why they are not available instead of showing a number.
    /// </summary>
    public static IReadOnlyList<DeliveryReadinessDimension> Readiness(IReadOnlyList<BacklogItem> items)
    {
        var tasks = items.Sum(i => i.TasksTotal);
        var done = items.Sum(i => i.TasksCompleted);
        var specified = items.Count(i => i.HasSpecification);
        return
        [
            new("specification", "Specification", $"{specified} / {items.Count}", "Features with a spec.md. Existence only — specification review is a separate check.", items.Count > 0),
            new("checklist", "Task checklist", tasks == 0 ? "No tasks" : $"{done} / {tasks} checked ({100 * done / tasks}%)", $"{tasks - done} open. Markdown checkboxes only — not delivery progress.", tasks > 0),
            new("implementation", "Implementation verification", "Not available", "No implementation evidence is linked to these snapshots.", false),
            new("qa", "QA verification", "Not available", "No executed test evidence is linked to these snapshots.", false),
            new("acceptance", "Acceptance", $"{items.Count(i => i.Status == BacklogStatus.Accepted)} stated Done", "Only an explicit “Status: Done” in the feature documents counts.", true),
            new("blockers", "Blockers", $"{items.Count(i => i.Status == BacklogStatus.Blocked)}", "Features explicitly marked Blocked in their documents.", true),
        ];
    }

    public static string Provenance(BacklogSource source) => source.AzureDevOps is { } ado
        ? $"Azure DevOps · {ado.Project} / {ado.Repository} / {ado.Branch} · {ado.ShortCommit}"
        : DeliveryReportingText.Source(source.SourceType);
}
