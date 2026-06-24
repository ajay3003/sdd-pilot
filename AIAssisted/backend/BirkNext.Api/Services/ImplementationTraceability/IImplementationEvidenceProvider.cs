namespace BirkNext.Api.Services.ImplementationTraceability;

public interface IImplementationEvidenceProvider
{
    Task<ImplementationTraceabilityReport> FetchAsync(
        IReadOnlyList<int> workItemIds,
        string? repositoryId,
        string? branch,
        CancellationToken ct = default);
}
