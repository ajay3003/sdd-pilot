namespace BirkNext.Web.Models;

/// <summary>
/// Canonical semantic model for Task hierarchies.
/// Single source of truth for all Task review pages.
/// </summary>
public sealed class TaskSemanticModel
{
    // ── Metadata ────────────────────────────────────────────────────────────
    public string Title { get; init; } = string.Empty;
    public string? Description { get; init; }
    public int TotalTasks { get; init; }

    // ── Core Elements ───────────────────────────────────────────────────────
    public List<TaskPhase> Phases { get; init; } = [];
    public List<TaskItem> AllTasks { get; init; } = [];
    public List<TaskDependency> Dependencies { get; init; } = [];
    public List<TaskParallelGroup> ParallelGroups { get; init; } = [];

    // ── Aggregates ──────────────────────────────────────────────────────────
    public int TotalPhases => Phases.Count;
    public int CompletedTasks => AllTasks.Count(t => t.IsCompleted);
    public int OpenTasks => AllTasks.Count(t => !t.IsCompleted);
    public int TotalParallelTasks => AllTasks.Count(t => t.IsParallel);
    public int TotalTestingTasks => AllTasks.Count(t => t.IsTestingTask);
    public int TotalSecurityTasks => AllTasks.Count(t => t.IsSecurityTask);

    // ── Coverage Metrics ────────────────────────────────────────────────────
    public int UserStoryCount => AllTasks.Select(t => t.UserStoryId).Distinct().Count();
    public int FRLinkedTasks => AllTasks.Count(t => t.LinkedFRIds.Count > 0);
    public int SCLinkedTasks => AllTasks.Count(t => t.LinkedSCIds.Count > 0);
    public int UnlinkedTasks => AllTasks.Count(t => t.LinkedFRIds.Count == 0 && t.LinkedSCIds.Count == 0);
    public int CompletionPercentage => TotalTasks == 0 ? 0 : (CompletedTasks * 100) / TotalTasks;

    // ── Phase Progress ──────────────────────────────────────────────────────
    public Dictionary<string, TaskPhaseProgress> PhaseProgress { get; init; } = [];

    // ── Relationships ───────────────────────────────────────────────────────
    public Dictionary<string, List<string>> UserStoryToTasks { get; init; } = [];
    public Dictionary<string, List<string>> FRToTasks { get; init; } = [];
    public Dictionary<string, List<string>> SCToTasks { get; init; } = [];
    public Dictionary<string, List<string>> TaskToDependencies { get; init; } = [];
}

/// <summary>
/// Implementation phase containing tasks.
/// </summary>
public sealed class TaskPhase
{
    public string Id { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public int PhaseNumber { get; init; }
    public string? Description { get; init; }
    public List<string> TaskIds { get; init; } = [];
    public int CompletedCount { get; init; }
    public int TotalCount { get; init; }
    public int CompletionPercentage => TotalCount == 0 ? 0 : (CompletedCount * 100) / TotalCount;
}

/// <summary>
/// Individual task item.
/// </summary>
public sealed class TaskItem
{
    public string Id { get; init; } = string.Empty;  // e.g., T001, T042
    public string Title { get; init; } = string.Empty;
    public string? Description { get; init; }
    public bool IsCompleted { get; init; }
    public bool IsParallel { get; init; }
    public bool IsTestingTask { get; init; }
    public bool IsSecurityTask { get; init; }
    public string? UserStoryId { get; init; }
    public string? PhaseId { get; init; }
    public List<string> LinkedFRIds { get; init; } = [];
    public List<string> LinkedSCIds { get; init; } = [];
    public List<string> RelatedFileIds { get; init; } = [];
}

/// <summary>
/// Task dependency relationship.
/// </summary>
public sealed class TaskDependency
{
    public string SourceTaskId { get; init; } = string.Empty;
    public string DependsOnTaskId { get; init; } = string.Empty;
    public string? DependencyType { get; init; }  // e.g., "Phase", "Execution", "User Story"
    public string? Notes { get; init; }
}

/// <summary>
/// Group of parallel tasks that can run concurrently.
/// </summary>
public sealed class TaskParallelGroup
{
    public string PhaseId { get; init; } = string.Empty;
    public string PhaseName { get; init; } = string.Empty;
    public List<string> ParallelTaskIds { get; init; } = [];
}

/// <summary>
/// Progress metrics for a phase.
/// </summary>
public sealed class TaskPhaseProgress
{
    public string PhaseId { get; init; } = string.Empty;
    public string PhaseName { get; init; } = string.Empty;
    public int TotalTasks { get; init; }
    public int CompletedTasks { get; init; }
    public int OpenTasks { get; init; }
    public int CompletionPercentage { get; init; }
    public string Status { get; init; } = "NotStarted";  // NotStarted, InProgress, Complete
}
