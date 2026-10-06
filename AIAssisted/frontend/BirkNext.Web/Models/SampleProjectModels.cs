namespace BirkNext.Web.Models;

/// <summary>
/// One Sample Project. <see cref="Files"/> is the bounded recursive file inventory (no fixed filenames, no mandatory
/// artifact set); artifact roles are classified from document content on the client.
/// </summary>
public sealed record SampleProjectDto(
    string Slug,
    string Name,
    string Domain,
    string Description,
    string AbsolutePath,
    bool HasReadme,
    IReadOnlyList<SampleFileDto> Files,
    SampleDiscoveryStatsDto? Discovery = null);

/// <summary>
/// One file of the inventory. <see cref="IsSupported"/> marks a readable candidate document (Markdown) — it says nothing
/// about the artifact role. <see cref="ArtifactKind"/>/<see cref="ReviewerName"/>/<see cref="ReviewerRoute"/> are not set by
/// the inventory; they remain for callers that construct their own file lists.
/// </summary>
public sealed record SampleFileDto(
    string Filename,
    bool Exists,
    string? ArtifactKind,
    string? ReviewerName,
    string? ReviewerRoute,
    bool IsSupported,
    bool IsContextOnly,
    string? RelativePath = null,
    long SizeBytes = 0,
    DateTime? LastModifiedUtc = null,
    string? SkipReason = null);

public sealed record SampleDiscoveryStatsDto(
    int FilesScanned,
    int DocumentCount,
    IReadOnlyList<string> IgnoredDirectories,
    int SkippedLinks,
    bool Truncated);

public sealed record SampleDocumentContentDto(string RelativePath, string? Content, string? Error);

public sealed record SampleProjectsMetaDto(
    string? ResolvedPath,
    string Source,
    bool Exists);
