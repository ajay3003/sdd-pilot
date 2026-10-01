using System.Text.Json.Serialization;

namespace BirkNext.Dependencies;

// ── Source Analysis → Dependency Review bridge ────────────────────────────────────────────────────────────────────────────────
// Source Analysis owns ingestion: it reads the uploaded archive once and keeps a small, secret-safe dependency evidence set on the
// immutable source snapshot. Dependency Review owns the interpretation (inventory, Renovate policy, registry, advisories, licenses).

/// <summary>Which repository a source snapshot belongs to — derived from the archive (root solution file, else archive name). Never merged.</summary>
public sealed record SourceRepositoryIdentity(string Key, string DisplayName, string Basis);

/// <summary>A Renovate configuration candidate as found in the snapshot. Content is redacted (secret-looking keys/values replaced);
/// <see cref="Sha256"/> is the hash of the original file so policy drift is still detected exactly.</summary>
public sealed record SourceRenovateFile(string Path, string Sha256, string Content);

public sealed record SourceManagerFiles(string Manager, List<string> Files);

/// <summary>A package the snapshot itself declares it produces (csproj PackageId, literal or $(MSBuildProjectName)).</summary>
public sealed record SourcePublishedPackage(string PackageId, string Project, string? Version);

/// <summary>A ProjectReference whose path leaves the archive root: explicit evidence of source outside this repository.</summary>
public sealed record SourceExternalProjectReference(string FromProject, string Reference, string ProjectFileName);

/// <summary>Dependency-relevant evidence of one source snapshot. Captured once at analysis time; not a review result.</summary>
public sealed record SourceDependencyEvidence
{
    public int Version { get; init; } = 1;
    public List<DeclaredDependency> Dependencies { get; init; } = [];
    public List<SourceManagerFiles> Managers { get; init; } = [];
    public List<SourceRenovateFile> RenovateFiles { get; init; } = [];
    /// <summary>Renovate pipeline definitions found in source (text summaries; never runtime success).</summary>
    public List<string> Automation { get; init; } = [];
    public List<SourcePublishedPackage> PublishedPackages { get; init; } = [];
    public List<SourceExternalProjectReference> ExternalProjectReferences { get; init; } = [];
}

// ── Dependency Review source scope ────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>One immutable source snapshot as Dependency Review sees it (for choosing a scope).</summary>
public sealed record SourceScopeSnapshot
{
    public Guid SnapshotId { get; init; }
    public string RepositoryKey { get; init; } = "";
    public string Repository { get; init; } = "";
    public string IdentityBasis { get; init; } = "";
    public string ArchiveName { get; init; } = "";
    public string Fingerprint { get; init; } = "";
    public DateTimeOffset AnalyzedAt { get; init; }
    /// <summary>The Source Analysis extraction status of the snapshot (not dependency health).</summary>
    public string SourceStatus { get; init; } = "";
    public bool HasDependencyEvidence { get; init; }
    public string? DependencyEvidenceNote { get; init; }
    public int DeclaredDependencies { get; init; }
    public int RenovateFiles { get; init; }
    /// <summary>Newest snapshot of its repository in Source Analysis.</summary>
    public bool Latest { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RelatedSourceState { SnapshotAvailable, SnapshotUnavailable, NeedsReview }

public sealed record RelatedSourceEvidence(string Kind, string Detail, int Projects, List<string> Examples);

/// <summary>A source the primary snapshot appears to depend on. Suggested only — never included without an explicit choice.</summary>
public sealed record RelatedSourceCandidate
{
    public string RepositoryKey { get; init; } = "";
    public string Repository { get; init; } = "";
    public RelatedSourceState State { get; init; }
    public string Confidence { get; init; } = "Suggested";
    public string Reason { get; init; } = "";
    public List<RelatedSourceEvidence> Evidence { get; init; } = [];
    /// <summary>Snapshots of the candidate repository that carry the matched identity, newest first.</summary>
    public List<Guid> MatchingSnapshotIds { get; init; } = [];
}

public sealed record SourceScopeOptions
{
    public List<SourceScopeSnapshot> Snapshots { get; init; } = [];
    public List<RelatedSourceCandidate> Candidates { get; init; } = [];
}

public sealed record SourceConfigOverride(string Repository, string FileName, string Content);

public sealed record SourceDependencyReviewRequest
{
    public string EnvironmentId { get; init; } = "";
    public Guid PrimarySnapshotId { get; init; }
    public List<Guid> RelatedSnapshotIds { get; init; } = [];
    /// <summary>Suggested related sources the user chose to leave out; recorded as a scope limitation.</summary>
    public List<string> ExcludedSuggestions { get; init; } = [];
    public List<SourceConfigOverride> ConfigOverrides { get; init; } = [];
    public string? Label { get; init; }
}

/// <summary>An exact snapshot in a review scope, as it was at run time.</summary>
public sealed record SourceScopeEntry
{
    public Guid SnapshotId { get; init; }
    public string RepositoryKey { get; init; } = "";
    public string Repository { get; init; } = "";
    public string ArchiveName { get; init; } = "";
    public string Fingerprint { get; init; } = "";
    public DateTimeOffset AnalyzedAt { get; init; }
    public string SourceStatus { get; init; } = "";
}

/// <summary>The immutable source scope of a Dependency Review run: one primary snapshot plus explicitly included related snapshots.</summary>
public sealed record DependencyReviewSourceScope
{
    public SourceScopeEntry Primary { get; init; } = new();
    public List<SourceScopeEntry> Related { get; init; } = [];
    public List<string> ExcludedSuggestions { get; init; } = [];
    public List<string> Limitations { get; init; } = [];
}

public sealed record CrossSourceValue(string Repository, string? Value, List<string> Files);

/// <summary>The same dependency declared differently across the selected sources. An observation to review — not a defect.</summary>
public sealed record CrossSourceObservation(string Kind, string Manager, string PackageName, List<CrossSourceValue> Values);

/// <summary>A selected source references a package that another selected source produces.</summary>
public sealed record SourceRelationship(string FromRepository, string PackageName, List<string> ReferencedValues, int Projects, string ToRepository, string? PublishedVersion);
