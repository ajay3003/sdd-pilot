using BirkNext.Api.Models;
using BirkNext.Api.Services;

namespace BirkNext.Api.GraphQL;

public record SaveQaDeltaReviewInput(
    string Title,
    string ProjectId,
    string? OldSpecFileName,
    string? NewSpecFileName,
    string? OldSpecHash,
    string? NewSpecHash,
    int? OldSpecSize,
    int? NewSpecSize,
    string AnalysisProfile,
    string SummaryJson,
    string DeltaItemsJson);

public class SaveQaDeltaReviewPayload
{
    public QaDeltaReview? Review { get; init; }
    public IReadOnlyList<UserError> Errors { get; init; } = [];
    public string CorrelationId { get; init; } = string.Empty;
}

public class DeleteQaDeltaReviewPayload
{
    public string? DeletedId { get; init; }
    public bool Success { get; init; }
    public IReadOnlyList<UserError> Errors { get; init; } = [];
    public string CorrelationId { get; init; } = string.Empty;
}
