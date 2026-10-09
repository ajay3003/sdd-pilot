using System.Net.Http.Json;
using BirkNext.SourceImpact;

namespace BirkNext.Web.Services;

public sealed class SourceChangeImpactApiService(HttpClient http)
{
    public async Task<IReadOnlyList<ImpactRequirementOption>> RequirementsAsync(string projectId, CancellationToken ct = default) =>
        await http.GetFromJsonAsync<List<ImpactRequirementOption>>($"api/impact-analysis/runs/requirements?projectId={Uri.EscapeDataString(projectId)}", ct) ?? [];

    public async Task<(ImpactAnalysisRunReport? Report, string? Error)> RunUnifiedAsync(ImpactAnalysisRunRequest request, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync("api/impact-analysis/runs", request, ct);
        if (!response.IsSuccessStatusCode) return (null, await response.Content.ReadAsStringAsync(ct));
        return (await response.Content.ReadFromJsonAsync<ImpactAnalysisRunReport>(cancellationToken: ct), null);
    }

    public async Task<IReadOnlyList<ImpactAnalysisRunHistoryItem>> UnifiedHistoryAsync(string projectId, string? projectImportId, CancellationToken ct = default)
    {
        var uri = $"api/impact-analysis/runs/history?projectId={Uri.EscapeDataString(projectId)}&projectImportId={Uri.EscapeDataString(projectImportId ?? string.Empty)}";
        return await http.GetFromJsonAsync<List<ImpactAnalysisRunHistoryItem>>(uri, ct) ?? [];
    }

    public async Task<ImpactAnalysisRunReport?> UnifiedHistoryItemAsync(Guid runId, CancellationToken ct = default) =>
        await http.GetFromJsonAsync<ImpactAnalysisRunReport>($"api/impact-analysis/runs/history/{runId}", ct);

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

    public async Task<IReadOnlyList<ImpactHistorySummary>> HistoryAsync(string projectId, string? projectImportId, CancellationToken ct = default)
    {
        var uri = $"api/impact-analysis/source-change/history?projectId={Uri.EscapeDataString(projectId)}&projectImportId={Uri.EscapeDataString(projectImportId ?? string.Empty)}";
        return await http.GetFromJsonAsync<List<ImpactHistorySummary>>(uri, ct) ?? [];
    }

    public async Task<SourceChangeImpactReport?> HistoryItemAsync(Guid runId, CancellationToken ct = default) =>
        await http.GetFromJsonAsync<SourceChangeImpactReport>($"api/impact-analysis/source-change/history/{runId}", ct);
}
