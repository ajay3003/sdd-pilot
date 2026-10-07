namespace BirkNext.Api.Models;

/// <summary>
/// Persisted review progress for a workflow step in a workspace.
/// Contains ONLY human review/approval decisions and audit data.
///
/// Available/Locked/Current status is COMPUTED at runtime from:
/// - Artifact availability (from WorkspaceArtifactStatusService)
/// - Workflow definition (from WorkflowDefinitions)
/// - This persisted progress state
///
/// DOES NOT persist:
/// - PrerequisiteState (computed from artifacts)
/// - Available/Locked/Current (computed from dependencies)
/// - Derived completion status
///
/// Uses ReviewState and ApprovalState enums from WorkflowStateModels.
/// </summary>
public class WorkspaceReviewProgress
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }

    // Step identification
    public string StepKey { get; set; } = "";

    /// <summary>
    /// The exact artifact revisions this decision is about (<see cref="WorkflowArtifactBinding.Hash"/>): ids and content
    /// fingerprints of the artifacts the step reads. One row per step and binding, so a decision on an earlier revision stays
    /// as history and never becomes the decision on a new one. Null for decisions recorded before bindings existed: those
    /// name no revision and are never current.
    /// </summary>
    public string? ArtifactSetHash { get; set; }

    /// <summary>
    /// Which artifacts the decision is about, regardless of content (<see cref="WorkflowArtifactBinding.IdentityHash"/>). A
    /// decision with the same identity but another <see cref="ArtifactSetHash"/> was made on an earlier revision of the same
    /// artifacts (stale); one with another identity is about different artifacts and is not carried to these.
    /// </summary>
    public string? ArtifactIdentityHash { get; set; }

    /// <summary>The bound artifacts for people: "Specification: spec.md @ 3F2A9C1B; …".</summary>
    public string? ArtifactReferences { get; set; }

    // Human decisions (persisted)
    public ReviewState ReviewState { get; set; } = ReviewState.NotStarted;
    public ApprovalState ApprovalState { get; set; } = ApprovalState.Pending;

    // Audit trail for review
    public string? ReviewedBy { get; set; }        // User ID or "Local Developer"
    public DateTimeOffset? ReviewedAt { get; set; }

    // Audit trail for approval
    public string? ApprovedBy { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }

    // Audit trail for rejection
    public string? RejectedBy { get; set; }
    public DateTimeOffset? RejectedAt { get; set; }

    // Optional comment from reviewer
    public string? Comment { get; set; }

    // Artifact state at time of review/approval (for invalidation detection)
    public string? ArtifactSetHashAtReview { get; set; }
    public string? ArtifactSetHashAtApproval { get; set; }

    // ReviewContext and workspace versions (for invalidation detection)
    public string? ReviewContextVersionAtApproval { get; set; }
    public int? WorkspaceVersionAtApproval { get; set; }

    // User engagement tracking (computed fields, not critical)
    public DateTimeOffset? LastOpenedAt { get; set; }

    // Metadata
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    // Navigation property
    public SavedWorkspace? Workspace { get; set; }
}
