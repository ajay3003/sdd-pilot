namespace BirkNext.Api.Models;

/// <summary>
/// The trusted GraphQL schema (SDL) configured for one API target in one Target Environment. Key: (EnvironmentId, TargetId) — one
/// current artifact per target; replacing it overwrites the row, and results of earlier runs keep their own snapshot of what they used.
/// Metadata is the typed <c>GraphQlSchemaArtifact</c> as JSON; the SDL text is kept separately and never sent to the browser.
/// </summary>
public class GraphQlSchemaArtifactRecord
{
    public string EnvironmentId { get; set; } = string.Empty;
    public string TargetId { get; set; } = string.Empty;
    public string Id { get; set; } = string.Empty;
    public string ContentHash { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string DocumentJson { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
