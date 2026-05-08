using BirkNext.Api.Models;
using BirkNext.Api.Services;

namespace BirkNext.Api.GraphQL;

public record CreateScenarioInput(
    string Title,
    string? Description,
    ScenarioKind Kind,
    string ProjectId);

public class CreateScenarioPayload
{
    public Scenario? Scenario { get; init; }
    public IReadOnlyList<UserError> Errors { get; init; } = [];
    public string CorrelationId { get; init; } = string.Empty;
}
