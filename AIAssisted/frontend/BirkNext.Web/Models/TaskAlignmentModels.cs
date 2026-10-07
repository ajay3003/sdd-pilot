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

/// <summary>
/// One task's analysis result. Every task gets one; only <see cref="IsFinding"/> results (Needs review,
/// Possible deviation) are findings. A direct spec link is a resolved reference, not coverage or implementation.
/// </summary>
public sealed class TaskFinding
{
    public required string TaskId { get; init; }
    public required string Title { get; init; }
    /// <summary>The task's full line in the Task artifact (the title may be shortened).</summary>
    public string? TaskText { get; init; }
    public required AlignmentStatus Status { get; init; }
    public required AlignmentRisk Risk { get; init; }
    public required string Reason { get; init; }
    public required string RecommendedAction { get; init; }
    /// <summary>The deterministic rule that produced <see cref="Status"/>, e.g. "Explicit FR/SC reference resolved in the Specification".</summary>
    public string ClassificationBasis { get; init; } = string.Empty;
    /// <summary>The keyword or file that triggered a Technical-only or behavior classification, when one did.</summary>
    public string? ClassificationSignal { get; init; }
    public List<SpecMatch> Matches { get; init; } = [];
    /// <summary>FR/SC/user-story references in the task that do not resolve to an item in the Specification.</summary>
    public List<string> UnresolvedReferences { get; init; } = [];
    public List<AffectedArea> AffectedAreas { get; init; } = [];
    public List<string> RecommendedTests { get; init; } = [];
    /// <summary>Test priority from the task's topics (see TaskTopicTaxonomy) — a testing hint, not a measured risk.</summary>
    public ImpactLevel ImpactLevel { get; init; } = ImpactLevel.Unknown;
    public string MatchReason { get; init; } = string.Empty;
    public string RiskReason { get; init; } = string.Empty;
    public bool IsRegressionCandidate { get; init; }

    public bool IsFinding => Status is AlignmentStatus.NeedsReview or AlignmentStatus.PossibleDeviation;
}

/// <summary>Task-to-spec analysis of one Specification + Task artifact pair. <see cref="Findings"/> holds one result per task.</summary>
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
    /// <summary>One analysis result per task (name kept for compatibility); filter with <see cref="TaskFinding.IsFinding"/> for actual findings.</summary>
    public List<TaskFinding> Findings { get; init; } = [];

    /// <summary>Actual findings: tasks that need review or may deviate from the specification.</summary>
    public int FindingCount => NeedsReviewTasks + PossibleDeviations;
}
