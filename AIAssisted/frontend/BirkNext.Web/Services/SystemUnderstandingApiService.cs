using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using BirkNext.SystemUnderstanding;

namespace BirkNext.Web.Services;

/// <summary>System Understanding guides (api/system-understanding). Errors come back as a message, never as an exception.</summary>
public interface ISystemUnderstandingApiService
{
    Task<(IReadOnlyList<SystemGuideSummary>? Guides, string? Error)> ListAsync(CancellationToken ct = default);
    Task<(SystemGuideView? Guide, string? Error)> GetAsync(string guideKey, CancellationToken ct = default);
    Task<(SystemGuideRevision? Revision, string? Error)> RevisionAsync(string guideKey, int revision, CancellationToken ct = default);
    Task<(SystemGuideRevision? Revision, string? Error)> SaveDraftAsync(string guideKey, SaveGuideDraftRequest request, CancellationToken ct = default);
    /// <summary>action: submit, return-to-draft, approve, publish.</summary>
    Task<(SystemGuideRevision? Revision, string? Error)> TransitionAsync(string guideKey, int revision, string action, CancellationToken ct = default);
}

public sealed class SystemUnderstandingApiService(HttpClient http) : ISystemUnderstandingApiService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public Task<(IReadOnlyList<SystemGuideSummary>? Guides, string? Error)> ListAsync(CancellationToken ct = default) =>
        Send<IReadOnlyList<SystemGuideSummary>>(() => http.GetAsync("api/system-understanding/guides", ct), ct);

    public Task<(SystemGuideView? Guide, string? Error)> GetAsync(string guideKey, CancellationToken ct = default) =>
        Send<SystemGuideView>(() => http.GetAsync($"api/system-understanding/guides/{Uri.EscapeDataString(guideKey)}", ct), ct);

    public Task<(SystemGuideRevision? Revision, string? Error)> RevisionAsync(string guideKey, int revision, CancellationToken ct = default) =>
        Send<SystemGuideRevision>(() => http.GetAsync($"api/system-understanding/guides/{Uri.EscapeDataString(guideKey)}/revisions/{revision}", ct), ct);

    public Task<(SystemGuideRevision? Revision, string? Error)> SaveDraftAsync(string guideKey, SaveGuideDraftRequest request, CancellationToken ct = default) =>
        Send<SystemGuideRevision>(() => http.PutAsJsonAsync($"api/system-understanding/guides/{Uri.EscapeDataString(guideKey)}/draft", request, Json, ct), ct);

    public Task<(SystemGuideRevision? Revision, string? Error)> TransitionAsync(string guideKey, int revision, string action, CancellationToken ct = default) =>
        action is "submit" or "return-to-draft" or "approve" or "publish"
            ? Send<SystemGuideRevision>(() => http.PostAsync($"api/system-understanding/guides/{Uri.EscapeDataString(guideKey)}/revisions/{revision}/{action}", null, ct), ct)
            : Task.FromResult<(SystemGuideRevision?, string?)>((null, "Unknown action."));

    private static async Task<(T? Value, string? Error)> Send<T>(Func<Task<HttpResponseMessage>> call, CancellationToken ct)
    {
        try
        {
            using var response = await call();
            if (response.IsSuccessStatusCode) return (await response.Content.ReadFromJsonAsync<T>(Json, ct), null);
            if ((int)response.StatusCode is 401 or 403) return (default, "You do not have permission to change this guide.");
            string? message = null;
            try { message = (await response.Content.ReadFromJsonAsync<JsonElement>(Json, ct)).GetProperty("message").GetString(); }
            catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException) { }
            return (default, message ?? $"The request failed (HTTP {(int)response.StatusCode}).");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or NotSupportedException)
        {
            return (default, "System Understanding is not reachable.");
        }
    }
}
