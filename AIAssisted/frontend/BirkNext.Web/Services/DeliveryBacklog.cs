using BirkNext.DeliveryReporting;

namespace BirkNext.Web.Services;

/// <summary>Delivery status of a backlog feature. Each value needs its own evidence; none is derived from another.</summary>
public enum BacklogStatus { Planned, InProgress, Implemented, Verified, Accepted, Blocked, NeedsClarification, NotAssessed }

public sealed record BacklogItem(string FeatureKey, string Title, string Module, BacklogStatus Status, string StatusDetail, int Stories, int Criteria,
    int TasksCompleted, int TasksTotal, int Phases, DeliveryMovement? Movement);

/// <summary>Where the backlog was read from: the newest frozen weekly report, or (without one) the newest imported snapshot.</summary>
public sealed record BacklogSource(string Kind, string SnapshotLabel, DateTimeOffset EffectiveAt, DeliverySourceType SourceType, DeliveryAzureDevOpsSource? AzureDevOps,
    string? BaselineLabel, Guid? ReportId, string? WeekLabel);

public sealed record DeliveryBacklogView(BacklogSource Source, IReadOnlyList<BacklogItem> Items, IReadOnlyList<string> RemovedFeatures)
{
    public int Count(BacklogStatus status) => Items.Count(i => i.Status == status);
}

/// <summary>
/// The virtual SDD backlog (Delivery Overview): every feature of a delivery project with its user stories, acceptance criteria, phases, tasks
/// and one delivery status. Planned / In progress / Implemented / Verified / Accepted / Blocked are kept separate and come only from explicit
/// evidence in the snapshot: checked tasks make a feature Implemented, never Verified or Accepted; Accepted needs an explicit "Status: Done";
/// Verified needs verification evidence that snapshots do not carry, so no feature is Verified from snapshot data alone.
/// </summary>
public static class DeliveryBacklog
{
    public static readonly BacklogStatus[] Columns =
        [BacklogStatus.Planned, BacklogStatus.InProgress, BacklogStatus.Implemented, BacklogStatus.Verified, BacklogStatus.Accepted, BacklogStatus.Blocked];

    public const string VerifiedNotAvailable = "Not available: snapshots contain no verification evidence. A feature is never Verified because its tasks are checked.";

    public static BacklogStatus Status(DeliveryStatus? status) => status switch
    {
        DeliveryStatus.NotStarted => BacklogStatus.Planned,
        DeliveryStatus.InProgress => BacklogStatus.InProgress,
        DeliveryStatus.TasksComplete or DeliveryStatus.ReadyForTest or DeliveryStatus.Testing => BacklogStatus.Implemented,
        DeliveryStatus.Done => BacklogStatus.Accepted,
        DeliveryStatus.Blocked => BacklogStatus.Blocked,
        DeliveryStatus.NeedsClarification => BacklogStatus.NeedsClarification,
        _ => BacklogStatus.NotAssessed,
    };

    public static string Detail(DeliveryStatus? status) => status switch
    {
        DeliveryStatus.NotStarted => "Specified; no task checked yet",
        DeliveryStatus.InProgress => "Some tasks checked",
        DeliveryStatus.TasksComplete => "All tasks checked — not stated as tested or done",
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
        BacklogStatus.Implemented => "Implemented",
        BacklogStatus.Verified => "Verified",
        BacklogStatus.Accepted => "Accepted",
        BacklogStatus.Blocked => "Blocked",
        BacklogStatus.NeedsClarification => "Needs clarification",
        _ => "Not assessed",
    };

    /// <summary>From a frozen weekly report: its current snapshot's features (removed ones listed apart, never as backlog items).</summary>
    public static DeliveryBacklogView FromReport(WeeklyDeliveryReport report) => new(
        new("report", report.Current.Label, report.Current.EffectiveAt, report.Current.SourceType, report.Current.AzureDevOps, report.Baseline.Label, report.ReportId, report.WeekLabel),
        [.. report.Stories.Where(s => !s.RemovedFeature).Select(s => new BacklogItem(s.FeatureKey, s.Title, s.Module, Status(s.CurrentStatus), Detail(s.CurrentStatus),
            s.Hierarchy?.Stories.Count(u => u.Change != DeliveryItemChange.Removed) ?? 0,
            s.Hierarchy is { } h ? h.CriteriaCurrent : 0,
            s.CurrentTaskCompleted, s.CurrentTaskTotal, s.Hierarchy?.Phases.Count ?? 0, s.Movement))
            .OrderBy(i => i.Module, StringComparer.OrdinalIgnoreCase).ThenBy(i => i.Title, StringComparer.OrdinalIgnoreCase)],
        [.. report.Stories.Where(s => s.RemovedFeature).Select(s => s.Title)]);

    /// <summary>From a snapshot without a report: features and task progress only (story/criteria counts need a report).</summary>
    public static DeliveryBacklogView FromSnapshot(DeliverySnapshotDetail detail) => new(
        new("snapshot", detail.Summary.Label, detail.Summary.EffectiveAt, detail.Summary.SourceType, detail.Summary.AzureDevOps, null, null, null),
        [.. detail.Features.Select(f =>
        {
            DeliveryStatus? status = Enum.TryParse<DeliveryStatus>(f.Status, out var s) ? s : null;
            return new BacklogItem(f.Key, f.Title, f.Module, Status(status), Detail(status), 0, 0, f.TaskCompleted, f.TaskTotal, 0, null);
        }).OrderBy(i => i.Module, StringComparer.OrdinalIgnoreCase).ThenBy(i => i.Title, StringComparer.OrdinalIgnoreCase)],
        []);

    public static string Provenance(BacklogSource source) => source.AzureDevOps is { } ado
        ? $"Azure DevOps · {ado.Project} / {ado.Repository} / {ado.Branch} · {ado.ShortCommit}"
        : DeliveryReportingText.Source(source.SourceType);
}
