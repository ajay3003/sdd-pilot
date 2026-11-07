using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using BirkNext.ExecutionEvidence;

namespace BirkNext.Web.Services;

/// <summary>Execution Evidence (read-only here): saved runs, calls, attempt comparisons and the redacted export link. Opening a result never executes anything.</summary>
public interface IExecutionEvidenceApiService
{
    Task<(IReadOnlyList<ExecutionRunSummary>? Runs, string? Error)> RunsAsync(CancellationToken ct = default);
    Task<(ExecutionRunDetail? Detail, string? Error)> RunAsync(Guid runId, int skip, int take, string? state, string? protocol, string? method, string? search, string sort, CancellationToken ct = default);
    Task<(ExecutionCallRecord? Call, string? Error)> CallAsync(Guid runId, Guid callId, CancellationToken ct = default);
    Task<(AttemptComparison? Comparison, string? Error)> CompareAsync(Guid runA, Guid callA, int attemptA, Guid runB, Guid callB, int attemptB, CancellationToken ct = default);
    Task<(ExecutionRunStartResult? Result, string? Error)> StartAsync(ExecutionRunRequest request, CancellationToken ct = default);
    string ExportUrl(Guid runId);
}

public sealed class ExecutionEvidenceApiService(HttpClient http) : IExecutionEvidenceApiService
{
    public const string Title = "Execution Results";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public Task<(IReadOnlyList<ExecutionRunSummary>? Runs, string? Error)> RunsAsync(CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<ExecutionRunSummary>>("api/execution-evidence/runs?take=100", ct);

    public Task<(ExecutionRunDetail? Detail, string? Error)> RunAsync(Guid runId, int skip, int take, string? state, string? protocol, string? method, string? search, string sort, CancellationToken ct = default)
    {
        var q = new List<string> { $"skip={skip}", $"take={take}", $"sort={Uri.EscapeDataString(sort)}" };
        if (!string.IsNullOrEmpty(state)) q.Add($"state={Uri.EscapeDataString(state)}");
        if (!string.IsNullOrEmpty(protocol)) q.Add($"protocol={Uri.EscapeDataString(protocol)}");
        if (!string.IsNullOrEmpty(method)) q.Add($"method={Uri.EscapeDataString(method)}");
        if (!string.IsNullOrWhiteSpace(search)) q.Add($"search={Uri.EscapeDataString(search)}");
        return GetAsync<ExecutionRunDetail>($"api/execution-evidence/runs/{runId}?{string.Join('&', q)}", ct);
    }

    public Task<(ExecutionCallRecord? Call, string? Error)> CallAsync(Guid runId, Guid callId, CancellationToken ct = default) =>
        GetAsync<ExecutionCallRecord>($"api/execution-evidence/runs/{runId}/calls/{callId}", ct);

    public Task<(AttemptComparison? Comparison, string? Error)> CompareAsync(Guid runA, Guid callA, int attemptA, Guid runB, Guid callB, int attemptB, CancellationToken ct = default) =>
        GetAsync<AttemptComparison>($"api/execution-evidence/compare?runA={runA}&callA={callA}&attemptA={attemptA}&runB={runB}&callB={callB}&attemptB={attemptB}", ct);

    public async Task<(ExecutionRunStartResult? Result, string? Error)> StartAsync(ExecutionRunRequest request, CancellationToken ct = default)
    {
        try
        {
            using var response = await http.PostAsJsonAsync("api/execution-evidence/runs", request, Json, ct);
            var result = await response.Content.ReadFromJsonAsync<ExecutionRunStartResult>(Json, ct);
            return (result, response.IsSuccessStatusCode ? null : result?.Message ?? $"The run was refused ({(int)response.StatusCode}).");
        }
        catch (Exception e) when (BackendRequestClassifier.IsRequestFailure(e))
        { return (null, BackendRequestClassifier.FromException(Title, e, "POST api/execution-evidence/runs", ct).UserMessage); }
    }

    public string ExportUrl(Guid runId) => new Uri(http.BaseAddress!, $"api/execution-evidence/runs/{runId}/export").ToString();

    private async Task<(T? Body, string? Error)> GetAsync<T>(string path, CancellationToken ct) where T : class
    {
        try
        {
            using var response = await http.GetAsync(path, ct);
            if (!response.IsSuccessStatusCode) return (null, response.StatusCode == System.Net.HttpStatusCode.NotFound ? "Not found." : $"The request failed ({(int)response.StatusCode}).");
            return (await response.Content.ReadFromJsonAsync<T>(Json, ct), null);
        }
        catch (Exception e) when (BackendRequestClassifier.IsRequestFailure(e))
        { return (null, BackendRequestClassifier.FromException(Title, e, $"GET {path}", ct).UserMessage); }
    }
}
