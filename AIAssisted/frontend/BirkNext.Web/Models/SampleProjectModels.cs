namespace BirkNext.Web.Models;

public sealed record SampleProjectDto(
    string Slug,
    string Name,
    string Domain,
    string Description,
    string AbsolutePath,
    bool HasReadme,
    IReadOnlyList<SampleFileDto> Files);

public sealed record SampleFileDto(
    string Filename,
    bool Exists,
    string? ArtifactKind,
    string? ReviewerName,
    string? ReviewerRoute,
    bool IsSupported,
    bool IsContextOnly);
