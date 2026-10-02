using System.Text.Json.Serialization;

namespace BirkNext.Dependencies;

// ── Source Analysis → Dependency Review bridge ────────────────────────────────────────────────────────────────────────────────
// Source Analysis owns ingestion: it reads the uploaded archive once and keeps a small, secret-safe dependency evidence set on the
// immutable source snapshot. Dependency Review owns the interpretation (inventory, Renovate policy, registry, advisories, licenses).

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
    /// <summary>Manifests of ecosystems BirkNext does not read natively ("Maven: pom.xml"). A tool limitation, never a missing dependency
    /// policy; an SBOM import covers them. Empty in snapshots captured before this was recorded.</summary>
    public List<string> UnsupportedManifests { get; init; } = [];
}

// ── Dependency Review source scope ────────────────────────────────────────────────────────────────────────────────────────────

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

public sealed record CrossSourceValue(string Repository, string? Value, List<string> Files);

/// <summary>The same dependency declared differently across the selected sources. An observation to review — not a defect.</summary>
public sealed record CrossSourceObservation(string Kind, string Manager, string PackageName, List<CrossSourceValue> Values);

/// <summary>A selected source references a package that another selected source produces.</summary>
public sealed record SourceRelationship(string FromRepository, string PackageName, List<string> ReferencedValues, int Projects, string ToRepository, string? PublishedVersion);
