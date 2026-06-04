using BirkNext.Api.Models;
using BirkNext.Api.Services;

namespace BirkNext.Api.GraphQL;

/// <summary>Payload returned by the createTraceLink mutation.</summary>
public class CreateTraceLinkPayload
{
    /// <summary>The created trace link, or null if validation failed.</summary>
    public TraceLink? TraceLink { get; init; }

    /// <summary>Validation errors that prevented the link from being created.</summary>
    public IReadOnlyList<UserError> Errors { get; init; } = [];

    /// <summary>Correlation ID for tracing this request in logs.</summary>
    public string CorrelationId { get; init; } = string.Empty;
}
