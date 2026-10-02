using System.Net.Http.Json;
using BirkNext.SourceImpact;

namespace BirkNext.Web.Services;

public sealed class SourceChangeImpactApiService(HttpClient http)
{
    public async Task<ImpactSnapshotList> SnapshotsAsync(string environmentId, CancellationToken ct = default) =>
        await http.GetFromJsonAsync<ImpactSnapshotList>($"api/impact-analysis/source-change/snapshots?environmentId={Uri.EscapeDataString(environmentId)}", ct)
        ?? new(false, []);

    public async Task<(SourceChangeImpactReport? Report, string? Error)> AnalyzeAsync(SourceChangeImpactRequest request, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync("api/impact-analysis/source-change", request, ct);
        if (!response.IsSuccessStatusCode)
            return (null, await response.Content.ReadAsStringAsync(ct));
        return (await response.Content.ReadFromJsonAsync<SourceChangeImpactReport>(cancellationToken: ct), null);
    }
}
