using BirkNext.Api.Services;

namespace BirkNext.Api.GraphQL;

/// <summary>Payload returned by the deleteScenario mutation.</summary>
public class DeleteScenarioPayload
{
    /// <summary>The ID of the deleted scenario on success; null on failure.</summary>
    public string? DeletedId { get; init; }
    /// <summary>True when the scenario was successfully deleted.</summary>
    public bool Success { get; init; }
    /// <summary>Business errors that prevented deletion.</summary>
    public IReadOnlyList<UserError> Errors { get; init; } = [];
    /// <summary>Correlation ID for tracing this request in logs.</summary>
    public string CorrelationId { get; init; } = string.Empty;
}
