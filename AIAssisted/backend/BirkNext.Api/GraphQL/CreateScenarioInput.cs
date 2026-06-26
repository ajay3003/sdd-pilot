using BirkNext.Api.Models;
using BirkNext.Api.Services;

namespace BirkNext.Api.GraphQL;

/// <summary>Input for the createScenario mutation.</summary>
public record CreateScenarioInput(
    string Title,
    string? Description,
    ScenarioKind Kind,
    string ProjectId);

/// <summary>Payload returned by the createScenario mutation.</summary>
public class CreateScenarioPayload
{
    /// <summary>The created scenario, or null if validation failed.</summary>
    public Scenario? Scenario { get; init; }
    /// <summary>Validation errors that prevented creation.</summary>
    public IReadOnlyList<UserError> Errors { get; init; } = [];
    /// <summary>Correlation ID for tracing this request in logs.</summary>
    public string CorrelationId { get; init; } = string.Empty;
}
