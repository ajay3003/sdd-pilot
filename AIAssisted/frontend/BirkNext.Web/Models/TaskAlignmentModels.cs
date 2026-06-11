namespace BirkNext.Web.Models;

public enum AlignmentStatus
{
    PossibleDeviation,
    NeedsReview,
    TechnicalOnly,
    Linked,
}

public enum AlignmentRisk
{
    High,
    Medium,
    Low,
}

public enum SpecMatchType
{
    Requirement,
    UserStory,
    AcceptanceScenario,
    SuccessCriterion,
    Clarification,
    None,
}

public enum AffectedArea
{
    Security,
    Authorization,
    Search,
    Profile,
    AccessManagement,
    ReferenceData,
    Ingestion,
    DomainEvents,
    Audit,
    OperationRegistration,
    HealthMonitoring,
    Infrastructure,
    BusinessRules,
    Workflow,
    Validation,
    Testing,
    ExceptionHandling,
}

public enum ImpactLevel
{
    High = 0,
    Medium = 1,
    Low = 2,
    Unknown = 3,
}

public sealed class SpecMatch
{
    public required string ItemId { get; init; }
    public required string Title { get; init; }
    public required SpecMatchType MatchType { get; init; }
}

public sealed class TaskFinding
{
    public required string TaskId { get; init; }
    public required string Title { get; init; }
    public required AlignmentStatus Status { get; init; }
    public required AlignmentRisk Risk { get; init; }
    public required string Reason { get; init; }
    public required string RecommendedAction { get; init; }
    public double Confidence { get; init; }
    public List<SpecMatch> Matches { get; init; } = [];
    public List<AffectedArea> AffectedAreas { get; init; } = [];
    public List<string> RecommendedTests { get; init; } = [];
    public ImpactLevel ImpactLevel { get; init; } = ImpactLevel.Unknown;
    public string MatchReason { get; init; } = string.Empty;
    public string RiskReason { get; init; } = string.Empty;
    public bool IsRegressionCandidate { get; init; }
}

public sealed class AlignmentReport
{
    public int TotalTasks { get; init; }
    public int LinkedTasks { get; init; }
    public int TechnicalOnlyTasks { get; init; }
    public int NeedsReviewTasks { get; init; }
    public int PossibleDeviations { get; init; }
    public int HighImpactTasks { get; init; }
    public int MediumImpactTasks { get; init; }
    public int LowImpactTasks { get; init; }
    public int UnknownImpactTasks { get; init; }
    public int RegressionCandidates { get; init; }
    public List<TaskFinding> Findings { get; init; } = [];
}
