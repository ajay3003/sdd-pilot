using System.Net.Http.Json;
using System.Text.Json;
using BirkNext.ApiReview;
using BirkNext.TechnicalHistory;

namespace BirkNext.Web.Services;

/// <summary>Server-side technical run history (API Quality Review) and the follow-up of findings. Runs are stored once and never changed.</summary>
public interface ITechnicalHistoryApiService
{
    Task<(StoredApiReviewRun? Run, string? Error)> StoreApiReviewRunAsync(string? projectKey, string environmentId, ApiReviewReport report, CancellationToken ct = default);
    Task<(IReadOnlyList<StoredApiReviewRun>? Runs, string? Error)> ApiReviewRunsAsync(string? environmentId, string? projectKey, CancellationToken ct = default);
    Task<(ApiReviewReport? Report, string? Error)> ApiReviewReportAsync(Guid runId, CancellationToken ct = default);
    Task<(IReadOnlyList<TechnicalFinding>? Findings, string? Error)> FindingsAsync(string projectKey, CancellationToken ct = default);
    Task<(TechnicalFinding? Finding, string? Error)> TrackAsync(TrackTechnicalFindingRequest request, CancellationToken ct = default);
    Task<(TechnicalFinding? Finding, string? Error)> UpdateAsync(string projectKey, Guid id, UpdateTechnicalFindingRequest request, CancellationToken ct = default);
}

public sealed class TechnicalHistoryApiService(HttpClient http) : ITechnicalHistoryApiService
{
    private const string Base = "api/technical-history";
    // Web defaults: enums travel as numbers, as the API reads and writes them (a string enum here made every status change a 400).
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static string E(string v) => Uri.EscapeDataString(v);

    public Task<(StoredApiReviewRun?, string?)> StoreApiReviewRunAsync(string? projectKey, string environmentId, ApiReviewReport report, CancellationToken ct = default) =>
        Send<StoredApiReviewRun>(() => http.PostAsJsonAsync($"{Base}/api-review-runs", new StoreApiReviewRunRequest(projectKey, environmentId, report), Json, ct), "The run could not be stored", ct);

    public Task<(IReadOnlyList<StoredApiReviewRun>?, string?)> ApiReviewRunsAsync(string? environmentId, string? projectKey, CancellationToken ct = default) =>
        Send<IReadOnlyList<StoredApiReviewRun>>(() => http.GetAsync($"{Base}/api-review-runs?environmentId={E(environmentId ?? "")}&projectKey={E(projectKey ?? "")}", ct), "Stored runs could not be read", ct);

    public Task<(ApiReviewReport?, string?)> ApiReviewReportAsync(Guid runId, CancellationToken ct = default) =>
        Send<ApiReviewReport>(() => http.GetAsync($"{Base}/api-review-runs/{runId:D}", ct), "The stored run could not be read", ct);

    public Task<(IReadOnlyList<TechnicalFinding>?, string?)> FindingsAsync(string projectKey, CancellationToken ct = default) =>
        Send<IReadOnlyList<TechnicalFinding>>(() => http.GetAsync($"{Base}/findings?projectKey={E(projectKey)}", ct), "Finding follow-ups could not be read", ct);

    public Task<(TechnicalFinding?, string?)> TrackAsync(TrackTechnicalFindingRequest request, CancellationToken ct = default) =>
        Send<TechnicalFinding>(() => http.PostAsJsonAsync($"{Base}/findings", request, Json, ct), "The follow-up could not be started", ct);

    public Task<(TechnicalFinding?, string?)> UpdateAsync(string projectKey, Guid id, UpdateTechnicalFindingRequest request, CancellationToken ct = default) =>
        Send<TechnicalFinding>(() => http.PostAsJsonAsync($"{Base}/findings/{id:D}?projectKey={E(projectKey)}", request, Json, ct), "The follow-up was not saved", ct);

    private static async Task<(T?, string?)> Send<T>(Func<Task<HttpResponseMessage>> call, string prefix, CancellationToken ct)
    {
        try
        {
            using var response = await call();
            if (response.IsSuccessStatusCode) return (await response.Content.ReadFromJsonAsync<T>(Json, ct), null);
            string? message = null;
            try { message = (await response.Content.ReadFromJsonAsync<JsonElement>(Json, ct)).TryGetProperty("message", out var m) ? m.GetString() : null; } catch (JsonException) { }
            return (default, message ?? $"{prefix} (HTTP {(int)response.StatusCode}).");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException) { return (default, $"{prefix}: the backend could not be reached."); }
    }
}
