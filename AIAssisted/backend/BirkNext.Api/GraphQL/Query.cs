using BirkNext.Api.Models;
using BirkNext.Api.Services;
using HotChocolate;

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
}
