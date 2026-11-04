namespace BirkNext.Api.Data.CanonicalInterpretation;

// Entities of the canonical_interpretation schema. Canonical identity (entity), source occurrence (occurrence) and display alias (alias) are three
// separate tables; a relation addresses entity ids, never names. Interpretation snapshots freeze membership rows, so a later reinterpretation never
// changes what an earlier snapshot said. Enum-valued columns are stored as text. No foreign key reaches into any other schema.

public sealed class CanonicalEntityRecord
{
    public Guid Id { get; set; }
    public string Scope { get; set; } = "";
    public string EntityType { get; set; } = "";
    /// <summary>The per-type identity key (content hash, declared namespace, route, explicit id). Never a display label.</summary>
    public string IdentityKey { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string SourceIdentity { get; set; } = "";
    public string ResolutionMethod { get; set; } = "";
    public string? Version { get; set; }
    public string AttributesJson { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid? LastInterpretationId { get; set; }
}

/// <summary>One sighting of an entity in one source snapshot (path, symbol, line): the occurrence, never merged into the identity.</summary>
public sealed class CanonicalOccurrenceRecord
{
    public Guid Id { get; set; }
    public Guid EntityId { get; set; }
    public string Scope { get; set; } = "";
    public string SourceSnapshotId { get; set; } = "";
    public string OccurrenceKey { get; set; } = "";
    public string Kind { get; set; } = "";
    public string? Detail { get; set; }
    public DateTimeOffset FirstSeenAt { get; set; }
}

public sealed class CanonicalAliasRecord
{
    public Guid Id { get; set; }
    public string Scope { get; set; } = "";
    public Guid EntityId { get; set; }
    public string Alias { get; set; } = "";
    public string NormalizedAlias { get; set; } = "";
    public string State { get; set; } = "";
    public string Source { get; set; } = "";
    public string RuleId { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class CanonicalRelationRecord
{
    public Guid Id { get; set; }
    public string Scope { get; set; } = "";
    public Guid SourceEntityId { get; set; }
    public Guid TargetEntityId { get; set; }
    public string RelationType { get; set; } = "";
    /// <summary>Stable key: source entity key → type → target entity key (what review decisions address).</summary>
    public string RelationKey { get; set; } = "";
    /// <summary>What the evidence supports (Explicit / Derived / Candidate / Ambiguous / Unresolved); human decisions live in <see cref="ReviewState"/>.</summary>
    public string Resolution { get; set; } = "";
    public string Confidence { get; set; } = "";
    public string RuleId { get; set; } = "";
    public string RuleSetVersion { get; set; } = "";
    public string EvidenceJson { get; set; } = "[]";
    public string EvidenceFingerprint { get; set; } = "";
    public string Explanation { get; set; } = "";
    public string ReviewState { get; set; } = "NotReviewed";
    public Guid? ReviewDecisionId { get; set; }
    /// <summary>False when the latest interpretation no longer produced it (kept for history and for decisions that refer to it).</summary>
    public bool Active { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>An append-only human decision. A correction inserts a new row and links the old one through <see cref="SupersededById"/>.</summary>
public sealed class ReviewDecisionRecord
{
    public Guid Id { get; set; }
    public long Sequence { get; set; }
    public string Scope { get; set; } = "";
    public string TargetKind { get; set; } = "";
    public string TargetKey { get; set; } = "";
    public Guid? TargetRelationId { get; set; }
    public Guid? TargetAliasId { get; set; }
    public string ProposedJson { get; set; } = "{}";
    public string Outcome { get; set; } = "";
    public string Reviewer { get; set; } = "";
    public DateTimeOffset DecidedAt { get; set; }
    public string? Notes { get; set; }
    public string EvidenceFingerprint { get; set; } = "";
    public string RuleSetVersion { get; set; } = "";
    public Guid? InterpretationId { get; set; }
    public Guid? SupersedesId { get; set; }
    public Guid? SupersededById { get; set; }
}

public sealed class InterpretationSnapshotRecord
{
    public Guid Id { get; set; }
    public string Scope { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    /// <summary>"correlation/1;work-items/1" — every rule set that produced the membership.</summary>
    public string RuleSetVersions { get; set; } = "";
    /// <summary>[SnapshotReference] of the source snapshots read (Source Analysis, Delivery).</summary>
    public string SourceSnapshotsJson { get; set; } = "[]";
    public string SourceSetFingerprint { get; set; } = "";
    /// <summary>Highest review decision sequence applied (decision context of the interpretation).</summary>
    public long DecisionSequence { get; set; }
    public int EntityCount { get; set; }
    public int RelationCount { get; set; }
    public string ContentFingerprint { get; set; } = "";
    public string LimitationsJson { get; set; } = "[]";
    public string Trigger { get; set; } = "";
    public Guid? PreviousId { get; set; }
}

public sealed class InterpretationSnapshotEntityRecord
{
    public Guid SnapshotId { get; set; }
    public Guid EntityId { get; set; }
    public string DisplayName { get; set; } = "";
    public string? Version { get; set; }
}

public sealed class InterpretationSnapshotRelationRecord
{
    public Guid SnapshotId { get; set; }
    public Guid RelationId { get; set; }
    public string Resolution { get; set; } = "";
    public string Confidence { get; set; } = "";
    public string RuleId { get; set; } = "";
    public string ReviewState { get; set; } = "";
    public string EvidenceFingerprint { get; set; } = "";
}
