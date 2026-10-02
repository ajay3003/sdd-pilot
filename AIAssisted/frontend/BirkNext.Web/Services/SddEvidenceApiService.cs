using System.Net.Http.Json;
using BirkNext.Integrations;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

public interface ISddEvidenceApiService
{
    Task<IReadOnlyList<IqrSourceSnapshot>> SourceSnapshotsAsync(string environmentId, CancellationToken ct = default);
    Task<IReadOnlyList<SddCodeLinkSourceReference>> CodeLinksAsync(string projectId, CancellationToken ct = default);
}

public sealed class SddEvidenceApiService(HttpClient http, IIntegrationCatalogApiService integrations) : ISddEvidenceApiService
{
    public Task<IReadOnlyList<IqrSourceSnapshot>> SourceSnapshotsAsync(string environmentId, CancellationToken ct = default) =>
        integrations.ListSourceSnapshotsAsync(environmentId, ct);

    public async Task<IReadOnlyList<SddCodeLinkSourceReference>> CodeLinksAsync(string projectId, CancellationToken ct = default)
    {
        var route = $"api/code-traceability/links?projectId={Uri.EscapeDataString(projectId)}";
        return await http.GetFromJsonAsync<List<SddCodeLinkSourceReference>>(route, cancellationToken: ct) ?? [];
    }
}
