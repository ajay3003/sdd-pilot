namespace BirkNext.Api.GraphQL;

public sealed class SaveReviewedCandidatesPayload
{
    public int SavedCount { get; init; }
    public string CorrelationId { get; init; } = string.Empty;
}
