using System.Net.Http.Json;
using BirkNext.Integrations;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

public interface ISddEvidenceApiService
{
    /// <summary>The stored source snapshots as summaries; the chosen one is fetched with <see cref="SourceSnapshotAsync"/>.</summary>
    Task<IReadOnlyList<IqrSourceSnapshotSummary>> SourceSnapshotsAsync(string environmentId, CancellationToken ct = default);
    Task<IqrSourceSnapshot?> SourceSnapshotAsync(Guid snapshotId, CancellationToken ct = default) => Task.FromResult<IqrSourceSnapshot?>(null);
    Task<IReadOnlyList<SddCodeLinkSourceReference>> CodeLinksAsync(string projectId, CancellationToken ct = default);
}

public sealed class SddEvidenceApiService(HttpClient http, IIntegrationCatalogApiService integrations) : ISddEvidenceApiService
{
    public Task<IReadOnlyList<IqrSourceSnapshotSummary>> SourceSnapshotsAsync(string environmentId, CancellationToken ct = default) =>
        integrations.ListSourceSnapshotSummariesAsync(environmentId, ct);

    public Task<IqrSourceSnapshot?> SourceSnapshotAsync(Guid snapshotId, CancellationToken ct = default) => integrations.GetSourceSnapshotAsync(snapshotId, ct);

    public async Task<IReadOnlyList<SddCodeLinkSourceReference>> CodeLinksAsync(string projectId, CancellationToken ct = default)
    {
        var route = $"api/code-traceability/links?projectId={Uri.EscapeDataString(projectId)}";
        return await http.GetFromJsonAsync<List<SddCodeLinkSourceReference>>(route, cancellationToken: ct) ?? [];
    }
}
