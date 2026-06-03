namespace BirkNext.Web.Components;

public record ScenarioListItem(string Id, string Title, string? Description, string Kind, int DisplayOrder = 0);

public record ArtifactRelationshipSummary(
    int LinkedRequirements = 0,
    int LinkedTests = 0,
    int LinkedClarifications = 0,
    bool HasTraceability = false,
    string CoverageLabel = "Unmapped",
    string StateLabel = "Unmapped",
    string StateTone = "unmapped",
    bool RequiresAttention = true,
    string? RecommendedAction = null);
