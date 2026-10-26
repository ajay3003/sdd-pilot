namespace BirkNext.Api.Services.ImplementationTraceability;

/// <summary>
/// Change Traceability without Azure DevOps: no evidence at all, and the state says so. Demo evidence is never returned here — it would read
/// as the project's own work items, pull requests and commits. (Developer demo evidence requires the explicit AzureDevOps:DemoEvidence flag.)
/// </summary>
public sealed class NotConfiguredImplementationEvidenceProvider : IImplementationEvidenceProvider
{
    public const string Message = "Azure DevOps is not configured for this project. Configure Azure DevOps to trace Work Item → Pull Request → Commit → Changed Files.";

    public Task<ImplementationTraceabilityReport> FetchAsync(IReadOnlyList<int> workItemIds, string? repositoryId, string? branch, CancellationToken ct = default) =>
        Task.FromResult(new ImplementationTraceabilityReport { Source = "NotConfigured", StatusMessage = Message });
}
