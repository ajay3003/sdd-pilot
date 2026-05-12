using BirkNext.Api.Models;
using BirkNext.Api.Services;
using HotChocolate;

namespace BirkNext.Api.GraphQL;

public class Query
{
    public string? Ping() => "pong";

    public async Task<IReadOnlyList<Scenario>> ScenariosAsync(
        string projectId,
        [Service] ScenarioService scenarioService,
        CancellationToken cancellationToken)
    {
        return await scenarioService.GetAllAsync(projectId, cancellationToken);
    }
}
