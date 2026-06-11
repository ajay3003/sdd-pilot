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

public sealed class TaskNode
{
    public string Id { get; } = Guid.NewGuid().ToString("N")[..10];
    public required string Title { get; init; }
    public required TaskNodeType NodeType { get; init; }
    public int HeadingLevel { get; init; }     // 1–6 for headings, 0 for tasks/rows

    // Task-specific fields
    public string? TaskId { get; init; }       // e.g. "T001", "T042"
    public bool IsCompleted { get; init; }
    public bool IsParallel { get; init; }
    public string? UserStoryTag { get; init; } // e.g. "US1", "US2"
    public List<string> ReferencedFrIds { get; init; } = [];
    public List<string> ReferencedScIds { get; init; } = [];
    public string RawText { get; init; } = string.Empty;

    // Table-specific fields
    public List<string> TableHeaders { get; init; } = [];   // column names on TableSection
    public List<string> CellValues { get; init; } = [];     // raw cells for TableRow
    public List<string> LinkedTaskIds { get; init; } = [];  // task IDs in a TableRow

    // Enrichment from AlignmentReport (populated by EnrichWithReport)
    public AlignmentStatus? Status { get; set; }
    public AlignmentRisk? Risk { get; set; }
    public ImpactLevel? Impact { get; set; }
    public bool IsRegressionCandidate { get; set; }
    public List<string> AffectedAreas { get; set; } = [];
    public List<SpecMatch> SpecMatches { get; set; } = [];

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

    // Populated after enrichment
    public int SpecLinked { get; init; }
    public int TechnicalOnly { get; init; }
    public int NeedsReview { get; init; }
    public int PossibleDeviations { get; init; }
    public int HighRisk { get; init; }
    public int RegressionCandidates { get; init; }
}

public sealed class TaskTree
{
    public List<TaskNode> Roots { get; init; } = [];
    public TaskHealth Health { get; init; } = new();
}
