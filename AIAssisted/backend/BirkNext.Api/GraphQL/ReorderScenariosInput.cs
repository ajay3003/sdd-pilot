using BirkNext.Api.Services;

namespace BirkNext.Api.GraphQL;

public record ReorderTestScenariosInput(string ProjectId, IReadOnlyList<string> OrderedIds);

public class ReorderTestScenariosPayload
{
    public bool Success { get; init; }
    public IReadOnlyList<UserError> Errors { get; init; } = [];
    public string CorrelationId { get; init; } = string.Empty;
}
