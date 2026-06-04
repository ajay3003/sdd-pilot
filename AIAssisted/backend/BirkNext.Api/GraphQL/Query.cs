using BirkNext.Api.Models;
using BirkNext.Api.Services;
using HotChocolate;
using HotChocolate.Types;
using HotChocolate.Types.Relay;

namespace BirkNext.Api.GraphQL;

/// <summary>Root query type.</summary>
public class Query
{
    /// <summary>Health-check; returns "pong".</summary>
    public string? Ping() => "pong";

    /// <summary>Returns all scenarios for the given project, newest first.</summary>
    public async Task<IReadOnlyList<Scenario>> ScenariosAsync(
        string projectId,
        [Service] ScenarioService scenarioService,
        CancellationToken cancellationToken)
    {
        return await scenarioService.GetAllAsync(projectId, cancellationToken);
    }

    /// <summary>Returns reviewed candidates for the given project, optionally filtered by session.</summary>
    public async Task<IReadOnlyList<ReviewedCandidate>> ReviewedCandidatesAsync(
        string projectId,
        string? sessionId,
        [Service] ReviewedCandidateService reviewedCandidateService,
        CancellationToken cancellationToken)
    {
        return await reviewedCandidateService.GetByProjectAsync(projectId, sessionId, cancellationToken);
    }

    /// <summary>Returns candidate links for the given project, optionally filtered by session.</summary>
    public async Task<IReadOnlyList<CandidateLink>> CandidateLinksAsync(
        string projectId,
        string? sessionId,
        [Service] CandidateLinkService candidateLinkService,
        CancellationToken cancellationToken)
    {
        return await candidateLinkService.GetByProjectAsync(projectId, sessionId, cancellationToken);
    }

    /// <summary>Returns all delta reviews for the given project, newest first.</summary>
    public async Task<IReadOnlyList<QaDeltaReview>> QaDeltaReviewsAsync(
        string projectId,
        [Service] QaDeltaReviewService qaDeltaReviewService,
        CancellationToken cancellationToken)
    {
        return await qaDeltaReviewService.GetAllAsync(projectId, cancellationToken);
    }

    /// <summary>Returns a single delta review by ID, or null if not found.</summary>
    public async Task<QaDeltaReview?> QaDeltaReviewAsync(
        [ID] string id,
        [Service] QaDeltaReviewService qaDeltaReviewService,
        CancellationToken cancellationToken)
    {
        return await qaDeltaReviewService.GetByIdAsync(id, cancellationToken);
    }

    /// <summary>Returns the traceability matrix for the given project. Only Requirement and Test scenarios are included.</summary>
    public async Task<IReadOnlyList<TraceabilityMatrixRow>> TraceabilityMatrixAsync(
        string projectId,
        [Service] TraceLinkService traceLinkService,
        CancellationToken cancellationToken)
    {
        return await traceLinkService.GetTraceabilityMatrixAsync(projectId, cancellationToken);
    }

    /// <summary>Returns aggregate coverage statistics for the given project.</summary>
    public async Task<CoverageSummary> CoverageSummaryAsync(
        string projectId,
        [Service] TraceLinkService traceLinkService,
        CancellationToken cancellationToken)
    {
        return await traceLinkService.GetCoverageSummaryAsync(projectId, cancellationToken);
    }
}
