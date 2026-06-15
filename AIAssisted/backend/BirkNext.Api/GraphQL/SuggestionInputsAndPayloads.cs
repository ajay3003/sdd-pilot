using BirkNext.Api.Models;
using BirkNext.Api.Services;
using HotChocolate.Types;

namespace BirkNext.Api.GraphQL;

// ── Inputs ────────────────────────────────────────────────────────────────────

public sealed class ConfirmSuggestionInput
{
    [ID] public string Id { get; init; } = string.Empty;
    public string ProjectId { get; init; } = string.Empty;
}

public sealed class RejectSuggestionInput
{
    [ID] public string Id { get; init; } = string.Empty;
    public string ProjectId { get; init; } = string.Empty;
}

public sealed class ConfirmHighConfidenceSuggestionsInput
{
    public string ProjectId { get; init; } = string.Empty;
}

public sealed class GenerateTraceabilitySuggestionsInput
{
    public string ProjectId { get; init; } = string.Empty;
}

// ── Payloads ──────────────────────────────────────────────────────────────────

public sealed class ConfirmSuggestionPayload
{
    public TraceLink? TraceLink { get; init; }
    public IReadOnlyList<UserError> Errors { get; init; } = [];
    public string CorrelationId { get; init; } = string.Empty;
}

public sealed class RejectSuggestionPayload
{
    public bool Success { get; init; }
    public IReadOnlyList<UserError> Errors { get; init; } = [];
    public string CorrelationId { get; init; } = string.Empty;
}

public sealed class ConfirmHighConfidenceSuggestionsPayload
{
    public int ConfirmedCount { get; init; }
    public string CorrelationId { get; init; } = string.Empty;
}

public sealed class GenerateTraceabilitySuggestionsPayload
{
    public SuggestionGenerationResult? Result { get; init; }
    public IReadOnlyList<UserError> Errors { get; init; } = [];
    public string CorrelationId { get; init; } = string.Empty;
}
