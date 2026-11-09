using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using BirkNext.ImplementationGaps;

namespace BirkNext.Web.Services;

/// <summary>Implementation-gap evaluations (made by Project Import): list, read, re-evaluate with recorded test executions, review a finding.</summary>
public interface IImplementationGapApiService
{
    Task<(List<GapEvaluationSummary>? Evaluations, string? Error)> ListAsync(string? scopeKey, CancellationToken ct = default);
    Task<(GapEvaluation? Evaluation, string? Error)> GetAsync(Guid id, CancellationToken ct = default);
    Task<(GapEvaluation? Evaluation, string? Error)> ReevaluateAsync(Guid id, GapReevaluateRequest request, CancellationToken ct = default);
    Task<string?> ReviewAsync(Guid id, GapReviewRequest request, CancellationToken ct = default);
}

public sealed class ImplementationGapApiService(HttpClient http) : IImplementationGapApiService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private const string Base = "api/implementation-gaps";
    private const string Area = "Implementation gaps";

    /// <summary>The evaluation scope of a project import (matches the backend's <c>ImportScope</c>).</summary>
    public static string ImportScope(string importId) => "import:" + importId;

    public Task<(List<GapEvaluationSummary>? Evaluations, string? Error)> ListAsync(string? scopeKey, CancellationToken ct = default) =>
        GetAsync<List<GapEvaluationSummary>>(string.IsNullOrWhiteSpace(scopeKey) ? Base : $"{Base}?scopeKey={Uri.EscapeDataString(scopeKey)}", ct);

    public Task<(GapEvaluation? Evaluation, string? Error)> GetAsync(Guid id, CancellationToken ct = default) => GetAsync<GapEvaluation>($"{Base}/{id:D}", ct);

    public async Task<(GapEvaluation? Evaluation, string? Error)> ReevaluateAsync(Guid id, GapReevaluateRequest request, CancellationToken ct = default)
    {
        try
        {
            using var response = await http.PostAsJsonAsync($"{Base}/{id:D}/reevaluate", request, Json, ct);
            if (response.IsSuccessStatusCode) return (await response.Content.ReadFromJsonAsync<GapEvaluation>(Json, ct), null);
            return (null, response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized ? "You do not have permission to re-evaluate (configuration write permission)." : await Message(response, ct));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException) { return (null, BackendRequestClassifier.FromException(Area, ex).UserMessage); }
    }

    public async Task<string?> ReviewAsync(Guid id, GapReviewRequest request, CancellationToken ct = default)
    {
        try
        {
            using var response = await http.PostAsJsonAsync($"{Base}/{id:D}/reviews", request, Json, ct);
            if (response.IsSuccessStatusCode) return null;
            return response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized ? "You do not have permission to record reviews (configuration write permission)." : await Message(response, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { return BackendRequestClassifier.FromException(Area, ex).UserMessage; }
    }

    private async Task<(T? Value, string? Error)> GetAsync<T>(string url, CancellationToken ct) where T : class
    {
        try
        {
            using var response = await http.GetAsync(url, ct);
            return response.IsSuccessStatusCode ? (await response.Content.ReadFromJsonAsync<T>(Json, ct), null) : (null, await Message(response, ct));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException) { return (null, BackendRequestClassifier.FromException(Area, ex).UserMessage); }
    }

    private static async Task<string> Message(HttpResponseMessage response, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct);
        try
        {
            if (JsonDocument.Parse(text).RootElement is { ValueKind: JsonValueKind.Object } o)
                foreach (var key in new[] { "error", "message" })
                    if (o.TryGetProperty(key, out var m)) return m.GetString() ?? text;
        }
        catch (JsonException) { }
        return string.IsNullOrWhiteSpace(text) ? $"The request failed (HTTP {(int)response.StatusCode})." : text.Trim('"');
    }

    /// <summary>Executions recorded in the workspace (imported TRX) mapped to the engine's form; synthetic or real is kept in <c>Source</c>.</summary>
    public static List<GapExecutedTest> ExecutionsFrom(IEnumerable<BirkNext.Web.Models.SddTestExecutionEvidence> executions) =>
        [.. executions.Where(e => !string.IsNullOrWhiteSpace(e.FullyQualifiedTestName) && e.EvidenceKind == "Executed")
            .Select(e => new GapExecutedTest(e.FullyQualifiedTestName!, e.Result switch
            {
                "Passed" => GapExecutionOutcome.Passed,
                "Failed" => GapExecutionOutcome.Failed,
                "NotExecuted" or "Skipped" => GapExecutionOutcome.NotExecuted,
                _ => GapExecutionOutcome.Unknown,
            }, string.IsNullOrWhiteSpace(e.ResultSource) ? e.ProviderId : e.ResultSource, e.ExecutedAt ?? e.FinishedAt))];
}
