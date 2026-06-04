namespace BirkNext.Api.Models;

/// <summary>
/// A link between a CodeFile and a Scenario (Requirement or Test).
/// Stored separately from TraceLinkType to keep code traceability
/// independent of the QA trace graph.
///
/// Extension points (not implemented in v1):
///   - CommitHash: pin the link to a specific code version
///   - Confidence: AI-generated relevance score
///   - CreatedBy: user who drew the link
/// </summary>
public class CodeLink
{
    public Guid Id { get; init; }
    public string ProjectId { get; init; } = string.Empty;
    public Guid CodeFileId { get; init; }
    public Guid ScenarioId { get; init; }
    /// <summary>Denormalised from Scenario.Kind for query efficiency.</summary>
    public string ScenarioKind { get; init; } = string.Empty;
    public DateTime CreatedAt { get; init; }
}
