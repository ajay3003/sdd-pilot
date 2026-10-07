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
    /// <summary>Needs changes on the current artifact revisions.</summary>
    NeedsAttention,
    /// <summary>A required artifact role is absent: the step does not apply (not counted, not recommended).</summary>
    NotApplicable,
    /// <summary>Decided on an earlier revision of the same artifacts; nothing decided on the current one.</summary>
    Stale
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

/// <summary>
/// The exact artifact a workflow step reads for one role: its stable id and the fingerprint of the content the reviewer sees.
/// Sent with every step build and review decision, so a decision is bound to that revision (backend WorkflowArtifactBinding).
/// </summary>
public sealed class ArtifactRevisionRef
{
    public string Role { get; set; } = "";
    public string ArtifactId { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public string? FileName { get; set; }
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

    /// <summary>Artifact roles the step needs (backend WorkflowDefinitions). Empty for steps without artifact prerequisites.</summary>
    public List<string> RequiredArtifacts { get; set; } = new();

    /// <summary>Every artifact role the step reads: required, then optional.</summary>
    public List<string> ArtifactRoles { get; set; } = new();

    /// <summary>Step type from the backend definitions: ArtifactLoad, Explorer, Analysis or Dashboard.</summary>
    public string StepType { get; set; } = "";

    /// <summary>The artifact revisions a decision on this step applies to now (backend binding), or null when none is identified.</summary>
    public string? ArtifactSetHash { get; set; }

    /// <summary>Those revisions for people: "Constitution: constitution.md @ 3F2A9C1B".</summary>
    public string? ArtifactReferences { get; set; }

    /// <summary>When the decision on the current revisions was recorded.</summary>
    public DateTimeOffset? DecidedAt { get; set; }

    /// <summary>For a stale step: the latest decision on an earlier revision ("Approved", "Reviewed", "Needs changes"), kept as history.</summary>
    public string? PreviousDecision { get; set; }
    public DateTimeOffset? PreviousDecisionAt { get; set; }
    public string? PreviousArtifactReferences { get; set; }

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
