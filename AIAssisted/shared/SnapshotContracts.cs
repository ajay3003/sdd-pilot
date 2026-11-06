using System.Text.Json.Serialization;

namespace BirkNext.Snapshots;

// Shared snapshot identity and reference vocabulary. Common infrastructure owns identity, metadata and reference/lineage mechanics; every domain keeps
// its own payload, creation rules, comparison and display (Source Analysis evidence, Delivery reports, Performance runs, …). This is a reference layer
// wrapped around the existing domain stores — not a generic payload table.

/// <summary>What kind of frozen state a snapshot is. A type label, never a status.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SnapshotType
{
    SourceImport, SourceAnalysis, DocumentAnalysis, ProjectEvidence, TargetEnvironment, TestData, EndpointDiscovery, BrowserDiscovery,
    RuntimeEvidence, Review, Delivery, GeneratedReport, Execution,
    /// <summary>A frozen canonical interpretation (canonical_interpretation.interpretation_snapshot).</summary>
    Interpretation,
}

/// <summary>How a referrer uses the snapshot it points to.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SnapshotReferenceRole
{
    /// <summary>The state a review, comparison or report was computed for.</summary>
    Current,
    /// <summary>The explicit "before" side of a comparison.</summary>
    Baseline,
    /// <summary>A live binding (e.g. an integration bound to this snapshot's evidence); it moves when re-bound.</summary>
    Bound,
    /// <summary>Data derived from the snapshot and owned by it (deleted with it, never a reason to keep it).</summary>
    Derived,
}

/// <summary>A lightweight pointer to another snapshot: consumers keep this, never a copy of the payload.</summary>
public sealed record SnapshotReference(SnapshotType Type, string Id, string? Label = null, string? Version = null);

/// <summary>One record that references a snapshot: what it is, how it uses the snapshot, which feature owns it and when it was recorded.</summary>
public sealed record SnapshotReferrer(SnapshotReference Referrer, SnapshotReferenceRole Role, string Owner, DateTimeOffset? RecordedAt);

/// <summary>
/// The answer to "what references this snapshot?" — the check that must pass before a snapshot may ever be deleted. Only stores that keep the id in a
/// queryable column are checked; <see cref="NotChecked"/> names the stores whose references live inside JSON payloads or the browser, so a caller can
/// never mistake "no tracked reference" for "unreferenced".
/// </summary>
public sealed record SnapshotReferenceReport(SnapshotReference Snapshot, bool Exists, IReadOnlyList<SnapshotReferrer> ReferencedBy, IReadOnlyList<string> NotChecked)
{
    /// <summary>References that keep the snapshot alive (derived data is deleted with it).</summary>
    public IReadOnlyList<SnapshotReferrer> Blocking => [.. ReferencedBy.Where(r => r.Role != SnapshotReferenceRole.Derived)];

    /// <summary>True when a checked store references it: deleting it would silently break history.</summary>
    public bool IsReferenced => Blocking.Count > 0;
}

// ---- Snapshot Registry (metadata, references, lineage, pointers, retention). Payloads stay in their owners' stores. ----

/// <summary>How long a snapshot is kept and what may remove it. Nothing is deleted automatically by any class.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SnapshotRetentionClass
{
    /// <summary>Staging data that is discarded when the operation ends (not persisted as a snapshot today).</summary>
    Temporary,
    /// <summary>Ordinary working evidence; may be deleted through its owner when nothing frozen depends on it.</summary>
    Working,
    /// <summary>Kept as history (review runs, interpretations); no delete path.</summary>
    Historical,
    /// <summary>Content frozen inside a generated report; the report never changes.</summary>
    FrozenReportEvidence,
    /// <summary>Set by a person: cannot be deleted through BirkNext until the protection is removed.</summary>
    Protected,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SnapshotProcessingStatus { Completed, Partial, Failed, Running, Unknown }

/// <summary>A typed lineage edge, recorded only from ids the owner stored (never guessed).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SnapshotLineageKind { ImportedFrom, DerivedFrom, ComparedAgainst, InterpretedUsing, GeneratedReportFrom, Supersedes }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SnapshotPointerKind { LatestCreated, LatestCompleted, CurrentSelected, CurrentApproved }

/// <summary>Central metadata of one snapshot. <see cref="EffectiveAt"/> and <see cref="SourceRevision"/> are null when the owner does not know them.</summary>
public sealed record SnapshotMetadata(
    SnapshotType Type, string Id, string? Project, string Module, string Label, DateTimeOffset CreatedAt, DateTimeOffset? EffectiveAt, string? SourceRevision,
    string? AnalysisVersion, string StorageReference, string? ContentFingerprint, SnapshotProcessingStatus Status, SnapshotRetentionClass Retention, bool Frozen,
    int MetadataVersion, bool PayloadAvailable = true)
{
    /// <summary>The owner's compact summary (JSON) computed once at registration, served to selectors instead of the payload. Never the payload.</summary>
    [JsonIgnore] public string? SummaryJson { get; init; }
    public SnapshotReference Reference => new(Type, Id, Label, AnalysisVersion);
}

public sealed record SnapshotLineageEdge(SnapshotReference From, SnapshotLineageKind Kind, SnapshotReference To, string RecordedBy);

/// <summary>One row of the metadata-only listing, with the pointers it currently holds in its project/type/module.</summary>
public sealed record SnapshotListItem(SnapshotMetadata Metadata, IReadOnlyList<SnapshotPointerKind> Pointers, int ReferencedBy);

public sealed record SnapshotListPage(IReadOnlyList<SnapshotListItem> Items, int Total, int Skip, int Take);

public sealed record SnapshotLineageView(SnapshotMetadata Snapshot, IReadOnlyList<SnapshotLineageEdge> Upstream, IReadOnlyList<SnapshotLineageEdge> Downstream,
    IReadOnlyList<SnapshotMetadata> Related);

/// <param name="TargetAvailable">For a stored pointer: whether its owner still holds the snapshot (false = the pointer is stale and must not be
/// trusted as current evidence). Null when there is no target.</param>
public sealed record SnapshotPointerView(string? Project, SnapshotType Type, string Module, SnapshotPointerKind Kind, SnapshotReference? Snapshot, DateTimeOffset? SetAt, string? SetBy,
    bool? TargetAvailable = null);

/// <summary>Whether a snapshot may be deleted: blocking reasons stop deletion; notes are informational (e.g. a report keeps its frozen copy).</summary>
public sealed record SnapshotDeletionCheck(SnapshotReference Snapshot, bool Allowed, IReadOnlyList<string> Blocking, IReadOnlyList<string> Notes, IReadOnlyList<string> NotChecked);

public sealed record SnapshotPointerRequest(string? Project, SnapshotType Type, string Module, SnapshotPointerKind Kind, string SnapshotId);

public sealed record SnapshotRetentionRequest(SnapshotRetentionClass Retention);

public sealed record SnapshotSyncResult(int Registered, int Updated, int Edges, IReadOnlyList<string> Sources, IReadOnlyList<string> Problems);
