namespace BirkNext.Web.Services.SampleProjects;

/// <summary>One readable candidate document of a Sample Project with its classified role.</summary>
public sealed record DiscoveredDocument(
    string RelativePath,
    string FileName,
    ArtifactDiscoveryStatus Status,
    WorkspaceArtifactType? Role,
    ArtifactConfidence Confidence,
    IReadOnlyList<string> Reasons,
    IReadOnlyList<ArtifactRoleCandidate> Candidates,
    string? Fingerprint,
    string? DuplicateOf);

public enum SampleRoleState
{
    /// <summary>Exactly one detected document: explorers use it.</summary>
    Detected,
    /// <summary>Several detected documents: all are kept; explorers need an explicit choice unless one was chosen.</summary>
    Multiple,
    /// <summary>No document detected for the role. Neutral: roles are optional.</summary>
    NotFound,
}

public sealed record SampleRoleSummary(
    WorkspaceArtifactType Role,
    IReadOnlyList<DiscoveredDocument> Documents,
    string? ChosenPath)
{
    public SampleRoleState State => Documents.Count switch { 0 => SampleRoleState.NotFound, 1 => SampleRoleState.Detected, _ => SampleRoleState.Multiple };

    /// <summary>The document explorers open: the only one, or the explicit choice among several. Never picked arbitrarily.</summary>
    public DiscoveredDocument? Primary => Documents.Count == 1
        ? Documents[0]
        : Documents.FirstOrDefault(d => string.Equals(d.RelativePath, ChosenPath, StringComparison.Ordinal));
}
