using BirkNext.DeliveryReporting;

namespace BirkNext.Api.Services.DeliveryReporting;

/// <summary>
/// Deterministic, conservative management status of one feature in one snapshot, always with a plain-language reason.
/// Order: explicit Blocked → open clarification markers (unless every task is checked or an explicit later status exists) → explicit
/// ReadyForTest/Testing/Done → task checkboxes (NotStarted / InProgress / TasksComplete) → NotAssessed. Checked tasks never mean Done.
/// </summary>
public static class DeliveryStatusDerivation
{
    public static (DeliveryStatus Status, string Reason) Derive(bool hasTaskFile, int taskTotal, int taskCompleted, int untrackedTasks,
        DeliveryStatus? explicitStatus, int openClarifications)
    {
        var progress = TaskProgress(hasTaskFile, taskTotal, taskCompleted, untrackedTasks);
        if (explicitStatus == DeliveryStatus.Blocked)
            return (DeliveryStatus.Blocked, $"Marked Blocked in the feature documents; {progress}");
        if (openClarifications > 0 && explicitStatus is null && !(taskTotal > 0 && taskCompleted == taskTotal))
            return (DeliveryStatus.NeedsClarification, $"{openClarifications} open clarification marker{(openClarifications == 1 ? "" : "s")} in the specification; {progress}");
        if (explicitStatus is { } declared)
            return (declared, $"Marked {DeliveryReportingText.Status(declared)} in the feature documents; {progress}");
        if (!hasTaskFile) return (DeliveryStatus.NotAssessed, "No tasks.md: task progress is not known");
        if (taskTotal == 0) return (DeliveryStatus.NotAssessed, "tasks.md has no checkbox tasks: progress is not tracked");
        if (taskCompleted == 0) return (DeliveryStatus.NotStarted, $"0 of {taskTotal} tasks completed");
        if (taskCompleted < taskTotal) return (DeliveryStatus.InProgress, $"{taskCompleted} of {taskTotal} tasks completed");
        return (DeliveryStatus.TasksComplete, $"All {taskTotal} tasks completed (not the same as delivered or tested)");
    }

    private static string TaskProgress(bool hasTaskFile, int total, int completed, int untracked) =>
        !hasTaskFile ? "no tasks.md"
        : total == 0 ? (untracked > 0 ? $"{untracked} tasks without checkboxes" : "no checkbox tasks")
        : $"{completed} of {total} tasks completed";

    /// <summary>Rank used to decide whether a status moved forward. Blocked and NeedsClarification are off the ladder.</summary>
    public static int Rank(DeliveryStatus status) => status switch
    {
        DeliveryStatus.NotAssessed => 0,
        DeliveryStatus.NotStarted => 1,
        DeliveryStatus.InProgress => 2,
        DeliveryStatus.TasksComplete => 3,
        DeliveryStatus.ReadyForTest => 4,
        DeliveryStatus.Testing => 5,
        DeliveryStatus.Done => 6,
        _ => -1,
    };

    public static bool IsComplete(DeliveryStatus status) => status is DeliveryStatus.TasksComplete or DeliveryStatus.Done;
}
