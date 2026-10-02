namespace BirkNext.Web.Models;

/// <summary>Persisted, deterministic SDD lifecycle metadata for one workspace.</summary>
public sealed class SddLifecycleState
{
    public int SchemaVersion { get; set; } = 1;
    public List<SddArtifactRevision> Revisions { get; set; } = [];
    public List<SddClarification> Questions { get; set; } = [];
    public List<SddDecision> Decisions { get; set; } = [];
    public List<SddTraceabilityLink> Links { get; set; } = [];
    public List<SddImplementationEvidence> ImplementationEvidence { get; set; } = [];
    public List<SddTestEvidence> TestEvidence { get; set; } = [];
    public List<SddReviewRun> ReviewRuns { get; set; } = [];
    public List<SddRequirementSnapshot> RequirementSnapshots { get; set; } = [];
    public List<SddRequirementChange> RequirementChanges { get; set; } = [];
}

public sealed class SddRequirementSnapshot
{
    public string RequirementId { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public string AcceptanceCriteriaFingerprint { get; set; } = "";
    public Guid? ArtifactRevisionId { get; set; }
    public DateTimeOffset CapturedAt { get; set; } = DateTimeOffset.UtcNow;
    public bool IsCurrent { get; set; }
}

public sealed class SddRequirementChange
{
    public string RequirementId { get; set; } = "";
    public string Change { get; set; } = "NeedsReview";
    public string PreviousFingerprint { get; set; } = "";
    public string CurrentFingerprint { get; set; } = "";
    public string ChangeDetail { get; set; } = "";
    public DateTimeOffset DetectedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class SddArtifactRevision
{
    public Guid RevisionId { get; set; } = Guid.NewGuid();
    public string Role { get; set; } = "Other";
    public string FileName { get; set; } = "";
    public string? SourceReference { get; set; }
    public string Content { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public int Revision { get; set; }
    public DateTimeOffset CapturedAt { get; set; } = DateTimeOffset.UtcNow;
    public string Authority { get; set; } = "Unknown";
    public bool IsCurrentSelection { get; set; }
    public Guid? SupersedesRevisionId { get; set; }
    public Guid? SupersededByRevisionId { get; set; }
}

public sealed class SddClarification
{
    public string Id { get; set; } = "";
    public string Text { get; set; } = "";
    public string Status { get; set; } = "Open";
    public string? Owner { get; set; }
    public string? Resolution { get; set; }
    public string? ResolutionReference { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    public Guid? SourceRevisionId { get; set; }
    public List<string> RequirementIds { get; set; } = [];
}

public sealed class SddDecision
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string Status { get; set; } = "Proposed";
    public string? SourceReference { get; set; }
    public List<string> ResolvesQuestionIds { get; set; } = [];
    public List<string> AffectedRequirementIds { get; set; } = [];
    public List<string> SupersedesDecisionIds { get; set; } = [];
}

public sealed class SddTraceabilityLink
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string FromId { get; set; } = "";
    public string ToId { get; set; } = "";
    public string Relationship { get; set; } = "References";
    public string Confidence { get; set; } = "Unresolved";
    public string Provenance { get; set; } = "Manual";
    public string Currentness { get; set; } = "Current";
    public string? Reason { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastValidatedAt { get; set; }
}

public sealed class SddImplementationEvidence
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string RequirementId { get; set; } = "";
    public string EvidenceType { get; set; } = "ManualEvidence";
    public string Reference { get; set; } = "";
    public string? SourceSnapshotId { get; set; }
    public string? SourceFingerprint { get; set; }
    public string Confidence { get; set; } = "Unresolved";
    public string Provenance { get; set; } = "Manual";
    public string Currentness { get; set; } = "Current";
}

public sealed class SddTestEvidence
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string RequirementId { get; set; } = "";
    public string? AcceptanceCriterionId { get; set; }
    public string TestReference { get; set; } = "";
    public string State { get; set; } = "Unknown";
    public string? Result { get; set; }
    public DateTimeOffset? Timestamp { get; set; }
    public string Provenance { get; set; } = "Manual";
    public string Currentness { get; set; } = "Current";
}

public sealed class SddReviewRun
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTimeOffset RanAt { get; set; } = DateTimeOffset.UtcNow;
    public string SpecificationFingerprint { get; set; } = "";
    public string PlanFingerprint { get; set; } = "";
    public string TasksFingerprint { get; set; } = "";
    public string SourceSnapshotReference { get; set; } = "Not available";
    public string Currentness { get; set; } = "Current";
    public int RequirementCount { get; set; }
    public int RequirementsWithPlanEvidence { get; set; }
    public int RequirementsWithTaskEvidence { get; set; }
}
