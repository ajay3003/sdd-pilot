namespace BirkNext.Api.Models;

/// <summary>
/// String constants used to discriminate the artifact type of a <see cref="TraceLink"/> source or target.
/// New kinds can be added here as future entity types are introduced without any schema change.
/// </summary>
public static class TraceLinkArtifactKind
{
    public const string Scenario = "Scenario";
    // Future additions: "Commit", "CodeChange", "AiSession"
}
