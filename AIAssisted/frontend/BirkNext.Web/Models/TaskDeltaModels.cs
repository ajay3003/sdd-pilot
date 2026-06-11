namespace BirkNext.Web.Models;

public enum DeltaType { Added, Removed, Modified, StatusChanged }
public enum ScopeChangeKind { None, Expansion, Reduction }
public enum DeltaSpecCoverage { Linked, NeedsReview, PossibleDeviation, NotApplicable }

public sealed record TableRelationship(string TableTitle, string RowSummary, List<string> LinkedTaskIds);

public sealed class TaskDeltaFinding
{
    public required string TaskId { get; init; }
    public required string Title { get; init; }
    public required DeltaType DeltaType { get; init; }
    public ScopeChangeKind ScopeChange { get; init; }
    public required string BeforeText { get; init; }
    public required string AfterText { get; init; }
    public required string DeltaSummary { get; init; }
    public required string RecommendedAction { get; init; }
    public List<AffectedArea> AffectedAreas { get; init; } = [];
    public ImpactLevel RiskLevel { get; init; } = ImpactLevel.Unknown;
    public DeltaSpecCoverage SpecCoverage { get; init; } = DeltaSpecCoverage.NotApplicable;
    public bool IsRegressionCandidate { get; init; }
    public List<string> RecommendedTests { get; init; } = [];
    public string RiskReason { get; init; } = string.Empty;

    // Status change
    public bool IsStatusChange { get; init; }
    public bool OldIsCompleted { get; init; }
    public bool NewIsCompleted { get; init; }

    // Task metadata extracted during parse
    public string? UserStoryTag { get; init; }
    public List<string> ReferencedFrIds { get; init; } = [];
    public List<string> ReferencedScIds { get; init; } = [];

    // Table cross-references
    public bool HasTableLinks { get; init; }
    public List<TableRelationship> TableRelationships { get; init; } = [];
}

public sealed class TaskDeltaReport
{
    public int TotalChanges { get; init; }
    public int AddedTasks { get; init; }
    public int RemovedTasks { get; init; }
    public int ModifiedTasks { get; init; }
    public int StatusChanges { get; init; }
    public int ScopeExpansions { get; init; }
    public int ScopeReductions { get; init; }
    public int HighRiskCount { get; init; }
    public int RegressionCandidates { get; init; }
    public int NeedsReview { get; init; }
    public int PossibleDeviations { get; init; }
    public int TablesDetected { get; init; }
    public int TraceabilityRows { get; init; }
    public List<TaskDeltaFinding> Findings { get; init; } = [];
}
