using BirkNext.Api.Models;

namespace BirkNext.Api.GraphQL;

public record SaveReviewedCandidateItemInput(
    string Title,
    ScenarioKind Classification,
    CandidateReviewStatus ReviewStatus,
    string? SourceDocument,
    string? SourceSection,
    string ProjectId,
    string? ReviewedBy,
    DateTimeOffset? ReviewedAt,
    Guid? CandidateId = null);

public record SaveReviewedCandidatesInput(
    IReadOnlyList<SaveReviewedCandidateItemInput> Items,
    string? SessionId);
