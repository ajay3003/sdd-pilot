namespace BirkNext.Api.Models;

/// <summary>
/// Prerequisite state: whether required artifacts are available.
/// </summary>
public enum PrerequisiteState
{
    Missing,      // Required artifacts not loaded
    Available     // Required artifacts loaded
}

/// <summary>
/// Review state: whether the step has been reviewed by the user.
/// </summary>
public enum ReviewState
{
    NotStarted,   // User has not opened/reviewed this step
    InProgress,   // User has opened and is currently reviewing
    Reviewed      // User has completed review
}

/// <summary>
/// Approval state: whether the step is approved, rejected, or pending.
/// </summary>
public enum ApprovalState
{
    Pending,           // Awaiting user approval
    Approved,          // User explicitly approved
    NeedsChanges,      // User marked as needs changes
    InvalidatedByArtifactChange  // Artifact changed after approval; approval invalidated
}

/// <summary>
/// Complete workflow step status combining prerequisites, review, and approval.
/// </summary>
public enum WorkflowStepStatus
{
    Locked,           // Prerequisites not met
    Available,        // Prerequisites met, not started
    InProgress,       // User is reviewing
    Reviewed,         // Review complete, pending approval
    Approved,         // Explicitly approved (green state)
    NeedsAttention    // Artifact changed or needs changes
}

/// <summary>
/// Persisted review and approval state for a workflow step in a workspace.
/// Allows workflow progress to survive workspace reload.
/// </summary>
public class WorkspaceReviewStep
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }

    // Step identification
    public string StepKey { get; set; } = "";  // e.g., "SpecificationReview", "ArtifactTraceability"
    public string StepTitle { get; set; } = "";

    // Prerequisites (JSON array of artifact type names)
    public string? RequiredArtifactTypesJson { get; set; }

    // State tracking
    public PrerequisiteState PrerequisiteState { get; set; } = PrerequisiteState.Missing;
    public ReviewState ReviewState { get; set; } = ReviewState.NotStarted;
    public ApprovalState ApprovalState { get; set; } = ApprovalState.Pending;

    // User audit trail
    public string? ReviewedBy { get; set; }        // User ID or "Local Developer"
    public DateTimeOffset? ReviewedAt { get; set; }

    public string? ApprovedBy { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }

    public string? RejectedBy { get; set; }
    public DateTimeOffset? RejectedAt { get; set; }

    // Optional comment from reviewer
    public string? Comment { get; set; }

    // When user last opened this step's page
    public DateTimeOffset? LastOpenedAt { get; set; }

    // Artifact content hash at time of approval (for invalidation detection)
    public string? ArtifactSetHashAtApproval { get; set; }

    // Metadata
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    // Navigation property
    public SavedWorkspace? Workspace { get; set; }
}

/// <summary>
/// Step definition for workflow building.
/// </summary>
public class WorkflowStepDefinition
{
    public string Key { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string Route { get; set; } = "";
    public string ActionLabel { get; set; } = "";
    public string Color { get; set; } = "";
    public bool IsVisible { get; set; }
    public PrerequisiteState PrerequisiteState { get; set; } = PrerequisiteState.Missing;
}

/// <summary>
/// Workflow step view model for UI.
/// </summary>
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

    // State indicators
    public WorkflowStepStatus Status { get; set; } = WorkflowStepStatus.Locked;
    public PrerequisiteState Prerequisites { get; set; } = PrerequisiteState.Missing;
    public ReviewState ReviewState { get; set; } = ReviewState.NotStarted;
    public ApprovalState ApprovalState { get; set; } = ApprovalState.Pending;

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
