using BirkNext.Api.Models;
using BirkNext.Api.Services;

namespace BirkNext.Api.GraphQL;

public record AnalyzeChangeInput(string ProjectId, string ChangeDescription);

public sealed class AnalyzeChangePayload
{
    public ChangeAuditReport? Report { get; init; }
    public IReadOnlyList<UserError> Errors { get; init; } = [];
}
