using BirkNext.Api.Services;
using HotChocolate;
using Microsoft.AspNetCore.Http;

namespace BirkNext.Api.GraphQL;

public class Mutation
{
    public async Task<CreateScenarioPayload> CreateScenarioAsync(
        CreateScenarioInput input,
        [Service] ScenarioService scenarioService,
        [Service] IHttpContextAccessor httpContextAccessor,
        CancellationToken cancellationToken)
    {
        var correlationId = httpContextAccessor.HttpContext?
            .Response.Headers["X-Correlation-Id"]
            .FirstOrDefault()
            ?? Guid.NewGuid().ToString();

        var result = await scenarioService.CreateAsync(
            title: input.Title,
            description: input.Description,
            kind: input.Kind,
            projectId: input.ProjectId,
            correlationId: correlationId,
            ct: cancellationToken);

        return new CreateScenarioPayload
        {
            Scenario = result.Scenario,
            Errors = result.Errors,
            CorrelationId = correlationId,
        };
    }
}
