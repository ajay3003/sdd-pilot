namespace BirkNext.Api.Models;

/// <summary>
/// A source-code file registered in the Code Traceability system.
/// Stores a file path so requirements and tests can be linked to code.
///
/// Extension points (not implemented in v1):
///   - CommitHash: tie a code file snapshot to a specific git commit
///   - RepositoryUrl: support multi-repo projects
///   - LastScannedAt: for future repository scanning
/// </summary>
public class CodeFile
{
    public Guid Id { get; init; }
    public string ProjectId { get; init; } = string.Empty;
    /// <summary>Relative or absolute path, e.g. "backend/Services/ScenarioService.cs".</summary>
    public string FilePath { get; init; } = string.Empty;
    /// <summary>Filename segment derived from FilePath on write, e.g. "ScenarioService.cs".</summary>
    public string FileName { get; init; } = string.Empty;
    public string? Description { get; init; }
    public DateTime CreatedAt { get; init; }
}
