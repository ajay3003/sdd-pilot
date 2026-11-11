using System.Net;
using System.Net.Http.Json;
using BirkNext.SourceBaselines;

namespace BirkNext.Web.Services;

/// <summary>The shared source context (logical sources, snapshots, analysis baselines) and its decisions. The backend owns the rules and enforces
/// who may change them; this client only reads and asks.</summary>
public sealed class SourceContextApiService(HttpClient http)
{
    public async Task<(SourceContextView? Context, string? Error)> GetAsync(CancellationToken ct = default)
    {
        try { return (await http.GetFromJsonAsync<SourceContextView>("api/source-context", ct), null); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or NotSupportedException)
        { return (null, "The source context could not be loaded."); }
    }

    public async Task<IReadOnlyList<SourceDecisionView>> HistoryAsync(string repositoryKey, CancellationToken ct = default)
    {
        try { return await http.GetFromJsonAsync<List<SourceDecisionView>>($"api/source-context/history?repositoryKey={Uri.EscapeDataString(repositoryKey)}", ct) ?? []; }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or NotSupportedException) { return []; }
    }

    public Task<SourceDecisionResult> SelectBaselineAsync(SourceBaselineRequest request, CancellationToken ct = default) => PostAsync("api/source-context/baseline", request, ct);

    public Task<SourceDecisionResult> DeclareBranchAsync(DeclaredBranchRequest request, CancellationToken ct = default) => PostAsync("api/source-context/declared-branch", request, ct);

    private async Task<SourceDecisionResult> PostAsync<T>(string url, T body, CancellationToken ct)
    {
        try
        {
            var response = await http.PostAsJsonAsync(url, body, ct);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return new(false, "Changing the source context is not permitted for this session (SystemConfiguration.Write). Nothing was changed.", null);
            return await response.Content.ReadFromJsonAsync<SourceDecisionResult>(cancellationToken: ct) ?? new(false, "No answer from the backend.", null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or NotSupportedException)
        { return new(false, "The backend could not be reached; nothing was changed.", null); }
    }
}
