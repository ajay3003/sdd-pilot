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
