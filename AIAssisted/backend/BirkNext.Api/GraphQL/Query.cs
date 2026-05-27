using BirkNext.Api.Models;
using BirkNext.Api.Services;
using HotChocolate;
using HotChocolate.Types;

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
}
