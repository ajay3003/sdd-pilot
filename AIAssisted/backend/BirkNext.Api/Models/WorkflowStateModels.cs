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
/// Persisted review progress is now in WorkspaceReviewProgress.
/// See BirkNext.Api.Models.WorkspaceReviewProgress for the entity definition.
/// </summary>

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

    // Step type/requirement properties
    public bool IsOptional { get; set; } = false;
    public bool RequiresApproval { get; set; } = true;
    public bool RequiresManualReview { get; set; } = true;

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

/// <summary>
/// Workflow readiness breakdown for dashboard display.
/// Shows separate metrics for artifacts, reviews, and approvals.
/// </summary>
public class WorkflowReadinessBreakdown
{
    /// <summary>
    /// Overall readiness percentage (0-100).
    /// Calculated as: 30% ArtifactsScore + 30% ReviewScore + 40% ApprovalScore.
    /// </summary>
    public int OverallReadiness { get; set; }

    /// <summary>
    /// Artifact loading progress: percentage of required artifacts loaded.
    /// </summary>
    public int ArtifactReadiness { get; set; }

    /// <summary>
    /// Review completion: percentage of required steps reviewed.
    /// </summary>
    public int ReviewReadiness { get; set; }

    /// <summary>
    /// Approval completion: percentage of required steps approved.
    /// </summary>
    public int ApprovalReadiness { get; set; }

    /// <summary>
    /// Whether all critical requirements are met (ready for release).
    /// True when: all required artifacts loaded AND all required steps approved AND no blocking issues.
    /// </summary>
    public bool ReadyForRelease { get; set; }

    /// <summary>
    /// Number of artifacts loaded.
    /// </summary>
    public int ArtifactsLoaded { get; set; }

    /// <summary>
    /// Total number of artifacts in workflow.
    /// </summary>
    public int ArtifactTotal { get; set; }

    /// <summary>
    /// Number of steps reviewed.
    /// </summary>
    public int StepsReviewed { get; set; }

    /// <summary>
    /// Total number of required review steps.
    /// </summary>
    public int StepsRequiringReview { get; set; }

    /// <summary>
    /// Number of steps approved.
    /// </summary>
    public int StepsApproved { get; set; }

    /// <summary>
    /// Total number of required approval steps.
    /// </summary>
    public int StepsRequiringApproval { get; set; }

    /// <summary>
    /// Number of steps with blocking issues (needs attention).
    /// </summary>
    public int BlockingIssues { get; set; }
}
