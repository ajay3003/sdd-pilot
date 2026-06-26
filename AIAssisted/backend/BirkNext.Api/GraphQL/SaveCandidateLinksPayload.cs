namespace BirkNext.Api.GraphQL;

public sealed class SaveCandidateLinksPayload
{
    public int SavedCount { get; init; }
    public string CorrelationId { get; init; } = string.Empty;
}
