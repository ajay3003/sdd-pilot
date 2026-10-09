using System.Net.Http.Json;
using System.Text.Json;
using BirkNext.SourceEvidence;
using BirkNext.TestCoverage;

namespace BirkNext.Web.Services;

/// <summary>Test Coverage &amp; Overlap Review calls: Source Analysis snapshots (no upload), run, history bound to exact snapshots, reviewer decisions.</summary>
public interface ITestCoverageApiService
{
    Task<(ReviewSourceOptions? Options, string? Error)> SourcesAsync(CancellationToken ct = default);
    Task<(TestCoverageReviewResult? Result, string? Error)> RunAsync(TestCoverageReviewRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<TestCoverageRunSummary>> HistoryAsync(CancellationToken ct = default);
    Task<TestCoverageReviewResult?> GetAsync(Guid runId, CancellationToken ct = default);
    Task<(CoverageDecision? Decision, string? Error)> DecideAsync(CoverageDecision decision, CancellationToken ct = default);
}

public sealed class TestCoverageApiService(HttpClient http) : ITestCoverageApiService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<(ReviewSourceOptions? Options, string? Error)> SourcesAsync(CancellationToken ct = default)
    {
        try { return (await http.GetFromJsonAsync<ReviewSourceOptions>("api/test-coverage-review/sources", Json, ct), null); }
        catch (Exception e) when (BackendRequestClassifier.IsRequestFailure(e))
        { return (null, BackendRequestClassifier.FromException(TestCoverageText.Title, e, "GET api/test-coverage-review/sources", ct).UserMessage); }
    }

    public async Task<(TestCoverageReviewResult? Result, string? Error)> RunAsync(TestCoverageReviewRequest request, CancellationToken ct = default)
    {
        try
        {
            using var response = await http.PostAsJsonAsync("api/test-coverage-review/runs", request, Json, ct);
            if (response.IsSuccessStatusCode) return (await response.Content.ReadFromJsonAsync<TestCoverageReviewResult>(Json, ct), null);
            return (null, await Detail(response, ct) ?? $"The review could not be run (HTTP {(int)response.StatusCode}).");
        }
        catch (Exception e) when (BackendRequestClassifier.IsRequestFailure(e))
        { return (null, BackendRequestClassifier.FromException(TestCoverageText.Title, e, "POST api/test-coverage-review/runs", ct).UserMessage); }
    }

    public async Task<IReadOnlyList<TestCoverageRunSummary>> HistoryAsync(CancellationToken ct = default)
    {
        try { return await http.GetFromJsonAsync<List<TestCoverageRunSummary>>("api/test-coverage-review/runs", Json, ct) ?? []; }
        catch (Exception e) when (BackendRequestClassifier.IsRequestFailure(e)) { return []; }
    }

    public async Task<TestCoverageReviewResult?> GetAsync(Guid runId, CancellationToken ct = default)
    {
        try { return await http.GetFromJsonAsync<TestCoverageReviewResult>($"api/test-coverage-review/runs/{runId}", Json, ct); }
        catch (Exception e) when (BackendRequestClassifier.IsRequestFailure(e)) { return null; }
    }

    public async Task<(CoverageDecision? Decision, string? Error)> DecideAsync(CoverageDecision decision, CancellationToken ct = default)
    {
        try
        {
            using var response = await http.PostAsJsonAsync("api/test-coverage-review/decisions", decision, Json, ct);
            if (response.IsSuccessStatusCode) return (await response.Content.ReadFromJsonAsync<CoverageDecision>(Json, ct), null);
            return (null, await Detail(response, ct) ?? "The decision could not be saved.");
        }
        catch (Exception e) when (BackendRequestClassifier.IsRequestFailure(e))
        { return (null, BackendRequestClassifier.FromException(TestCoverageText.Title, e, "POST api/test-coverage-review/decisions", ct).UserMessage); }
    }

    private static async Task<string?> Detail(HttpResponseMessage response, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct);
        try { return JsonDocument.Parse(text).RootElement.TryGetProperty("detail", out var d) && d.GetString() is { Length: > 0 } detail ? detail : null; }
        catch (JsonException) { return null; }
    }
}
