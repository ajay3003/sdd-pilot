namespace BirkNext.Web.Models;

public enum DeltaType { Added, Removed, Modified }
public enum ScopeChangeKind { None, Expansion, Reduction }
public enum DeltaSpecCoverage { Linked, NeedsReview, PossibleDeviation, NotApplicable }

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
}

public sealed class TaskDeltaReport
{
    public int TotalChanges { get; init; }
    public int AddedTasks { get; init; }
    public int RemovedTasks { get; init; }
    public int ModifiedTasks { get; init; }
    public int ScopeExpansions { get; init; }
    public int ScopeReductions { get; init; }
    public List<TaskDeltaFinding> Findings { get; init; } = [];
}
