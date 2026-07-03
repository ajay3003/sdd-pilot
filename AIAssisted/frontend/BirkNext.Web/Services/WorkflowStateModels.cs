namespace BirkNext.Web.Services;

/// <summary>
/// Workflow state enums and view models for frontend.
/// Matches backend WorkflowStateModels.cs
/// </summary>

public enum WorkflowStepStatus
{
    Locked,
    Available,
    InProgress,
    Reviewed,
    Approved,
    NeedsAttention
}

public enum ReviewState
{
    NotStarted,
    InProgress,
    Reviewed
}

public enum ApprovalState
{
    Pending,
    Approved,
    NeedsChanges,
    InvalidatedByArtifactChange
}

public enum PrerequisiteState
{
    Missing,
    Available
}

public class WorkflowStepViewModel
{
    public int Number { get; set; }
    public string Key { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string Route { get; set; } = "";
    public string ActionLabel { get; set; } = "";
    public string Color { get; set; } = "";
    public bool CanOpen { get; set; }
    public string DisabledReason { get; set; } = "";
    public bool IsCurrent { get; set; }
    public bool IsFuture { get; set; }

    // Step type/requirement properties
    public bool IsOptional { get; set; } = false;
    public bool RequiresApproval { get; set; } = true;
    public bool RequiresManualReview { get; set; } = true;

    // State indicators
    public WorkflowStepStatus Status { get; set; }
    public PrerequisiteState Prerequisites { get; set; }
    public ReviewState ReviewState { get; set; }
    public ApprovalState ApprovalState { get; set; }

    public string StatusText => Status switch
    {
        WorkflowStepStatus.Locked => "Locked",
        WorkflowStepStatus.Available => "Available",
        WorkflowStepStatus.InProgress => "In Progress",
        WorkflowStepStatus.Reviewed => "Reviewed",
        WorkflowStepStatus.Approved => "Approved ✓",
        WorkflowStepStatus.NeedsAttention => "Needs Attention",
        _ => "Unknown"
    };

    public string BadgeClass => Status switch
    {
        WorkflowStepStatus.Approved => "badge-success",
        WorkflowStepStatus.NeedsAttention => "badge-warning",
        WorkflowStepStatus.InProgress => "badge-info",
        WorkflowStepStatus.Reviewed => "badge-secondary",
        WorkflowStepStatus.Locked => "badge-dark",
        _ => "badge-secondary"
    };

    public string StatusClass => Status switch
    {
        WorkflowStepStatus.Approved => "is-approved",
        WorkflowStepStatus.NeedsAttention => "is-attention",
        WorkflowStepStatus.InProgress => "is-current",
        WorkflowStepStatus.Reviewed => "is-reviewed",
        WorkflowStepStatus.Locked => "is-disabled",
        _ => ""
    };
}

public class WorkflowReadinessBreakdown
{
    public int OverallReadiness { get; set; }
    public int ArtifactReadiness { get; set; }
    public int ReviewReadiness { get; set; }
    public int ApprovalReadiness { get; set; }
}
