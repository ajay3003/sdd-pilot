using BirkNext.Web.Models;

namespace BirkNext.Web.Models;

public enum TaskNodeType
{
    Phase,           // ## Phase N: ...
    UserStoryGroup,  // user story sub-heading (### US1 / ### User Story 1)
    TaskGroup,       // task group heading (### Tests, ### Implementation, etc.)
    DeepGroup,       // #### or deeper heading
    Task,            // - [ ] T001 / - [x] T001 / T001 bare line
    TableSection,    // markdown table block
    TableRow,        // a data row within a table
    TableTaskRef,    // a task ID referenced inside a table row
}

public enum TaskTableType
{
    Generic,
    Traceability,
    RequirementMapping,
    DependencyTable,
    ParallelExecution,
}

public sealed class TaskNode
{
    public string Id { get; } = Guid.NewGuid().ToString("N")[..10];
    public required string Title { get; init; }
    public required TaskNodeType NodeType { get; init; }
    public int HeadingLevel { get; init; }     // 1–6 for headings, 0 for tasks/rows

    // Task-specific fields
    public string? TaskId { get; init; }        // e.g. "T001", "T042"
    public bool IsCompleted { get; init; }
    public bool IsParallel { get; init; }
    public string? UserStoryTag { get; init; }  // e.g. "US1", "US2"
    public List<string> ReferencedFrIds { get; init; } = [];
    public List<string> ReferencedScIds { get; init; } = [];
    public string RawText { get; init; } = string.Empty;

    // Derived display helpers
    public string? ShortTitle { get; init; }        // brief readable title for tree (file path → class name + first clause)
    public string? PhaseTitle { get; set; }          // H2 phase name, set after parse
    public string? UserStoryTitle { get; set; }      // user story group name, set after parse
    public List<string> RelatedFiles { get; init; } = [];   // file paths extracted from task body
    public bool IsTestingTask { get; init; }         // keyword-detected testing task
    public bool IsSecurityTask { get; init; }        // keyword-detected security task

    // Architecture/context keywords
    public bool IsCritical { get; init; }            // CRITICAL note or blocking prerequisite
    public bool IsFrontendOnly { get; init; }        // frontend-only/Blazor WASM project
    public bool IsWorkerService { get; init; }       // worker/background service
    public bool IsProxy { get; init; }               // proxy/gateway role
    public bool IsNoSql { get; init; }               // no-SQL/no-database project

    // Table-specific fields
    public List<string> TableHeaders { get; init; } = [];   // column names on TableSection
    public List<string> CellValues { get; init; } = [];     // raw cells for TableRow
    public List<string> LinkedTaskIds { get; init; } = [];  // task IDs in a TableRow
    public TaskTableType TableKind { get; init; } = TaskTableType.Generic;

    // Enrichment from AlignmentReport (populated by EnrichWithReport)
    public AlignmentStatus? Status { get; set; }
    public AlignmentRisk? Risk { get; set; }
    public ImpactLevel? Impact { get; set; }
    public bool IsRegressionCandidate { get; set; }
    public bool IsUnresolved { get; set; }
    public List<string> AffectedAreas { get; set; } = [];
    public List<SpecMatch> SpecMatches { get; set; } = [];

    // Phase-level narrative metadata (only populated for Phase nodes)
    public string? PhasePurpose { get; set; }          // **Purpose**: ... (raw Markdown source)
    public string? PhaseGoal { get; set; }              // **Goal**: ... (raw Markdown source)
    public string? PhaseIndependentTest { get; set; }   // **Independent Test**: ... (raw Markdown source)
    public string? PhaseCheckpoint { get; set; }        // **Checkpoint**: ... (raw Markdown source)

    // Explicit task dependencies (only for Task nodes)
    public List<string> BlockedBy { get; init; } = [];   // task IDs that must complete before this task (predecessors)
    public List<string> Blocks { get; init; } = [];      // task IDs that depend on this task (successors)

    // Tree structure
    public List<TaskNode> Children { get; } = [];

    // Descendant counts — populated by TaskExplorerService after tree build
    public int TaskCount { get; set; }
    public int CompletedCount { get; set; }
    public int TotalDescendants { get; set; }
}

public sealed class TaskHealth
{
    public int TotalTasks { get; init; }
    public int CompletedTasks { get; init; }
    public int OpenTasks => TotalTasks - CompletedTasks;
    public int TotalPhases { get; init; }
    public int TablesDetected { get; init; }
    public int TraceabilityRows { get; init; }
    public int TasksLinkedFromTables { get; init; }
    public int UnresolvedTableRefs { get; init; }

    // Populated after enrichment
    public int SpecLinked { get; init; }
    public int TechnicalOnly { get; init; }
    public int NeedsReview { get; init; }
    public int PossibleDeviations { get; init; }
    public int HighRisk { get; init; }
    public int RegressionCandidates { get; init; }

    // QA-oriented counts (always populated from task refs and keyword detection)
    public int FrLinkedTasks { get; init; }    // tasks with ≥1 FR reference
    public int ScLinkedTasks { get; init; }    // tasks with ≥1 SC reference
    public int UnlinkedTasks { get; init; }    // tasks with no FR/SC refs or spec matches
    public int TestingTasks { get; init; }
    public int SecurityTasks { get; init; }
    public int UserStoryCount { get; init; }
    public int CriticalTasks { get; init; }    // blocking or critical tasks
    public int FrontendOnlyTasks { get; init; } // frontend-only project tasks
    public int WorkerServiceTasks { get; init; } // worker/background service tasks
    public int ProxyTasks { get; init; }        // proxy/gateway tasks
    public int NoSqlTasks { get; init; }        // no-SQL/no-database project tasks
    public int ParallelTasks { get; init; }     // tasks marked [P]
}

public sealed class TaskTree
{
    public List<TaskNode> Roots { get; init; } = [];
    public TaskHealth Health { get; init; } = new();
    public List<TaskDependency> ExplicitDependencies { get; init; } = [];
}
