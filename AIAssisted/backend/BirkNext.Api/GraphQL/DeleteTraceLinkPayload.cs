using BirkNext.Api.Services;

namespace BirkNext.Api.GraphQL;

/// <summary>Payload returned by the deleteTraceLink mutation.</summary>
public class DeleteTraceLinkPayload
{
    /// <summary>The ID of the deleted trace link, or null if deletion failed.</summary>
    public string? DeletedId { get; init; }

    /// <summary>True when the trace link was successfully deleted.</summary>
    public bool Success { get; init; }

    /// <summary>Errors that prevented deletion.</summary>
    public IReadOnlyList<UserError> Errors { get; init; } = [];

    /// <summary>Correlation ID for tracing this request in logs.</summary>
    public string CorrelationId { get; init; } = string.Empty;
}
