using System.Net.Http.Json;
using System.Text.Json;
using BirkNext.AiCodeReview;
using BirkNext.SourceEvidence;

namespace BirkNext.Web.Services;

/// <summary>AI-Generated Code Review calls: Source Analysis snapshots (no upload here), run, and run history bound to exact snapshots.</summary>
public interface IAiCodeReviewApiService
{
    Task<(ReviewSourceOptions? Options, string? Error)> SourcesAsync(CancellationToken ct = default);
    Task<(AiCodeReviewResult? Result, string? Error)> RunAsync(AiCodeReviewRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<AiCodeReviewRunSummary>> HistoryAsync(CancellationToken ct = default);
    Task<AiCodeReviewResult?> GetAsync(Guid runId, CancellationToken ct = default);
}

public sealed class AiCodeReviewApiService(HttpClient http) : IAiCodeReviewApiService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<(ReviewSourceOptions? Options, string? Error)> SourcesAsync(CancellationToken ct = default)
    {
        try { return (await http.GetFromJsonAsync<ReviewSourceOptions>("api/ai-code-review/sources", Json, ct), null); }
        catch (Exception e) when (BackendRequestClassifier.IsRequestFailure(e))
        { return (null, BackendRequestClassifier.FromException(AiCodeReviewText.Title, e, "GET api/ai-code-review/sources", ct).UserMessage); }
    }

    public async Task<(AiCodeReviewResult? Result, string? Error)> RunAsync(AiCodeReviewRequest request, CancellationToken ct = default)
    {
        try
        {
            using var response = await http.PostAsJsonAsync("api/ai-code-review/runs", request, Json, ct);
            if (response.IsSuccessStatusCode) return (await response.Content.ReadFromJsonAsync<AiCodeReviewResult>(Json, ct), null);
            var text = await response.Content.ReadAsStringAsync(ct);
            try { if (JsonDocument.Parse(text).RootElement.TryGetProperty("detail", out var d) && d.GetString() is { Length: > 0 } detail) return (null, detail); } catch (JsonException) { }
            return (null, $"The review could not be run (HTTP {(int)response.StatusCode}).");
        }
        catch (Exception e) when (BackendRequestClassifier.IsRequestFailure(e))
        { return (null, BackendRequestClassifier.FromException(AiCodeReviewText.Title, e, "POST api/ai-code-review/runs", ct).UserMessage); }
    }

    public async Task<IReadOnlyList<AiCodeReviewRunSummary>> HistoryAsync(CancellationToken ct = default)
    {
        try { return await http.GetFromJsonAsync<List<AiCodeReviewRunSummary>>("api/ai-code-review/runs", Json, ct) ?? []; }
        catch (Exception e) when (BackendRequestClassifier.IsRequestFailure(e)) { return []; }
    }

    public async Task<AiCodeReviewResult?> GetAsync(Guid runId, CancellationToken ct = default)
    {
        try { return await http.GetFromJsonAsync<AiCodeReviewResult>($"api/ai-code-review/runs/{runId}", Json, ct); }
        catch (Exception e) when (BackendRequestClassifier.IsRequestFailure(e)) { return null; }
    }
}
