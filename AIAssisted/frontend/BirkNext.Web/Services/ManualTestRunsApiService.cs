using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using BirkNext.TestPlanning;

namespace BirkNext.Web.Services;

/// <summary>Manual test runs (api/manual-test-runs). Errors come back as a message, never as an exception. Nothing here executes anything or talks
/// to Azure DevOps: a tester records what happened.</summary>
public interface IManualTestRunsApiService
{
    Task<(List<ManualRunSummary>? Runs, string? Error)> ListAsync(string projectKey, Guid? draftId = null, string? caseId = null, CancellationToken ct = default);
    Task<(ManualTestRun? Run, string? Error)> GetAsync(string projectKey, Guid id, CancellationToken ct = default);
    /// <summary>One page of the project's runs, newest first, with the exact total. Hosts without paging return no page (callers fall back).</summary>
    Task<(ManualRunPage? Page, string? Error)> PageAsync(string projectKey, int skip, int take, Guid? draftId = null, string? caseId = null, CancellationToken ct = default) =>
        Task.FromResult<(ManualRunPage?, string?)>((null, null));
    Task<(ManualTestRun? Run, string? Error)> StartAsync(StartManualRunRequest request, CancellationToken ct = default);
    Task<(ManualTestRun? Run, string? Error)> SaveAsync(string projectKey, Guid id, SaveManualRunRequest request, CancellationToken ct = default);
    Task<(ManualTestRun? Run, string? Error)> CompleteAsync(string projectKey, Guid id, int baseRevision, CancellationToken ct = default);
    Task<(ManualTestRun? Run, string? Error)> AbandonAsync(string projectKey, Guid id, int baseRevision, string reason, CancellationToken ct = default);
}

public sealed class ManualTestRunsApiService(HttpClient http) : IManualTestRunsApiService
{
    private const string Base = "api/manual-test-runs";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private static string P(string projectKey) => $"projectKey={Uri.EscapeDataString(projectKey)}";

    public Task<(List<ManualRunSummary>?, string?)> ListAsync(string projectKey, Guid? draftId = null, string? caseId = null, CancellationToken ct = default) =>
        Send<List<ManualRunSummary>>(() => http.GetAsync($"{Base}?{P(projectKey)}{(draftId is { } d ? $"&draftId={d}" : "")}{(caseId is { Length: > 0 } c ? $"&caseId={Uri.EscapeDataString(c)}" : "")}", ct), ct);

    public Task<(ManualRunPage?, string?)> PageAsync(string projectKey, int skip, int take, Guid? draftId = null, string? caseId = null, CancellationToken ct = default) =>
        Send<ManualRunPage>(() => http.GetAsync($"{Base}/page?{P(projectKey)}&skip={skip}&take={take}{(draftId is { } d ? $"&draftId={d}" : "")}{(caseId is { Length: > 0 } c ? $"&caseId={Uri.EscapeDataString(c)}" : "")}", ct), ct);

    public Task<(ManualTestRun?, string?)> GetAsync(string projectKey, Guid id, CancellationToken ct = default) =>
        Send<ManualTestRun>(() => http.GetAsync($"{Base}/{id}?{P(projectKey)}", ct), ct);

    public Task<(ManualTestRun?, string?)> StartAsync(StartManualRunRequest request, CancellationToken ct = default) =>
        Send<ManualTestRun>(() => http.PostAsJsonAsync(Base, request, Json, ct), ct);

    public Task<(ManualTestRun?, string?)> SaveAsync(string projectKey, Guid id, SaveManualRunRequest request, CancellationToken ct = default) =>
        Send<ManualTestRun>(() => http.PutAsJsonAsync($"{Base}/{id}?{P(projectKey)}", request, Json, ct), ct);

    public Task<(ManualTestRun?, string?)> CompleteAsync(string projectKey, Guid id, int baseRevision, CancellationToken ct = default) =>
        Send<ManualTestRun>(() => http.PostAsJsonAsync($"{Base}/{id}/complete?{P(projectKey)}", new CompleteManualRunRequest(baseRevision), Json, ct), ct);

    public Task<(ManualTestRun?, string?)> AbandonAsync(string projectKey, Guid id, int baseRevision, string reason, CancellationToken ct = default) =>
        Send<ManualTestRun>(() => http.PostAsJsonAsync($"{Base}/{id}/abandon?{P(projectKey)}", new AbandonManualRunRequest(baseRevision, reason), Json, ct), ct);

    private static async Task<(T? Value, string? Error)> Send<T>(Func<Task<HttpResponseMessage>> call, CancellationToken ct)
    {
        try
        {
            using var response = await call();
            if (response.IsSuccessStatusCode) return (await response.Content.ReadFromJsonAsync<T>(Json, ct), null);
            if ((int)response.StatusCode is 401 or 403) return (default, "You do not have permission to record test runs.");
            string? message = null;
            try { message = (await response.Content.ReadFromJsonAsync<JsonElement>(Json, ct)).GetProperty("message").GetString(); }
            catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException) { }
            return (default, message ?? $"The request failed (HTTP {(int)response.StatusCode}).");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or NotSupportedException)
        {
            return (default, "Test runs are not reachable.");
        }
    }
}
