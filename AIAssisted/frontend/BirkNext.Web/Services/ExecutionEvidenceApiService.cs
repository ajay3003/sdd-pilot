using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using BirkNext.ExecutionEvidence;

namespace BirkNext.Web.Services;

/// <summary>Run history filters (all optional).</summary>
public sealed record ExecutionRunFilter(string? Search = null, string? State = null, DateOnly? From = null, DateOnly? To = null, bool ProtectedOnly = false, string? EnvironmentId = null);

/// <summary>
/// Execution Evidence: saved runs, calls, attempt comparisons, exports, and evidence management (storage usage, integrity, protection, deletion,
/// body removal, retention, audit). Opening or exporting a result never executes anything; deleting needs the configuration-write permission.
/// </summary>
public interface IExecutionEvidenceApiService
{
    Task<(IReadOnlyList<ExecutionRunSummary>? Runs, string? Error)> RunsAsync(ExecutionRunFilter? filter = null, CancellationToken ct = default);
    Task<(ExecutionStorageUsage? Usage, string? Error)> StorageAsync(string? environmentId = null, CancellationToken ct = default);
    Task<(ExecutionIntegrityReport? Report, string? Error)> IntegrityAsync(Guid runId, CancellationToken ct = default);
    Task<string?> ProtectAsync(Guid runId, bool isProtected, CancellationToken ct = default);
    Task<(ExecutionDeletionResult? Result, string? Error)> DeleteRunsAsync(ExecutionDeletionRequest request, CancellationToken ct = default);
    Task<(ExecutionRetentionPreview? Preview, string? Error)> DeletionPreviewAsync(string environmentId, CancellationToken ct = default);
    Task<(int Removed, string? Error)> RemoveBodiesAsync(Guid runId, Guid callId, CancellationToken ct = default);
    Task<(ExecutionRetentionSettings? Settings, string? Error)> RetentionAsync(CancellationToken ct = default);
    Task<(ExecutionRetentionSettings? Settings, string? Error)> SaveRetentionAsync(ExecutionRetentionSettings settings, CancellationToken ct = default);
    Task<(ExecutionRetentionPreview? Preview, string? Error)> RetentionPreviewAsync(CancellationToken ct = default);
    Task<(ExecutionDeletionResult? Result, string? Error)> ApplyRetentionAsync(int confirm, CancellationToken ct = default);
    Task<(IReadOnlyList<ExecutionAuditEntry>? Entries, string? Error)> AuditAsync(CancellationToken ct = default);
    Task<(byte[]? Zip, string? Error)> ExportAsync(IReadOnlyList<Guid> runIds, CancellationToken ct = default);
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

    public Task<(IReadOnlyList<ExecutionRunSummary>? Runs, string? Error)> RunsAsync(ExecutionRunFilter? filter = null, CancellationToken ct = default)
    {
        var q = new List<string> { "take=200" };
        if (filter is { } f)
        {
            if (!string.IsNullOrWhiteSpace(f.Search)) q.Add($"search={Uri.EscapeDataString(f.Search)}");
            if (!string.IsNullOrWhiteSpace(f.State)) q.Add($"state={Uri.EscapeDataString(f.State)}");
            if (f.From is { } from) q.Add($"from={Uri.EscapeDataString(from.ToDateTime(TimeOnly.MinValue, DateTimeKind.Local).ToUniversalTime().ToString("O"))}");
            if (f.To is { } to) q.Add($"to={Uri.EscapeDataString(to.ToDateTime(TimeOnly.MaxValue, DateTimeKind.Local).ToUniversalTime().ToString("O"))}");
            if (f.ProtectedOnly) q.Add("protected=true");
            if (!string.IsNullOrWhiteSpace(f.EnvironmentId)) q.Add($"environmentId={Uri.EscapeDataString(f.EnvironmentId)}");
        }
        return GetAsync<IReadOnlyList<ExecutionRunSummary>>($"api/execution-evidence/runs?{string.Join('&', q)}", ct);
    }

    public Task<(ExecutionStorageUsage? Usage, string? Error)> StorageAsync(string? environmentId = null, CancellationToken ct = default) =>
        GetAsync<ExecutionStorageUsage>("api/execution-evidence/storage" + (environmentId is { Length: > 0 } e ? $"?environmentId={Uri.EscapeDataString(e)}" : ""), ct);

    public Task<(ExecutionIntegrityReport? Report, string? Error)> IntegrityAsync(Guid runId, CancellationToken ct = default) =>
        GetAsync<ExecutionIntegrityReport>($"api/execution-evidence/runs/{runId}/integrity", ct);

    public async Task<string?> ProtectAsync(Guid runId, bool isProtected, CancellationToken ct = default) =>
        (await SendAsync<JsonElement?>(HttpMethod.Put, $"api/execution-evidence/runs/{runId}/protection?protected={(isProtected ? "true" : "false")}", null, ct)).Error;

    public Task<(ExecutionDeletionResult? Result, string? Error)> DeleteRunsAsync(ExecutionDeletionRequest request, CancellationToken ct = default) =>
        SendAsync<ExecutionDeletionResult>(HttpMethod.Post, "api/execution-evidence/runs/delete", request, ct);

    public Task<(ExecutionRetentionPreview? Preview, string? Error)> DeletionPreviewAsync(string environmentId, CancellationToken ct = default) =>
        GetAsync<ExecutionRetentionPreview>($"api/execution-evidence/deletion-preview?environmentId={Uri.EscapeDataString(environmentId)}", ct);

    public async Task<(int Removed, string? Error)> RemoveBodiesAsync(Guid runId, Guid callId, CancellationToken ct = default)
    {
        var (body, error) = await SendAsync<JsonElement?>(HttpMethod.Delete, $"api/execution-evidence/runs/{runId}/calls/{callId}/bodies", null, ct);
        return (body is { } b && b.TryGetProperty("removed", out var n) ? n.GetInt32() : 0, error);
    }

    public Task<(ExecutionRetentionSettings? Settings, string? Error)> RetentionAsync(CancellationToken ct = default) =>
        GetAsync<ExecutionRetentionSettings>("api/execution-evidence/retention", ct);

    public Task<(ExecutionRetentionSettings? Settings, string? Error)> SaveRetentionAsync(ExecutionRetentionSettings settings, CancellationToken ct = default) =>
        SendAsync<ExecutionRetentionSettings>(HttpMethod.Put, "api/execution-evidence/retention", settings, ct);

    public Task<(ExecutionRetentionPreview? Preview, string? Error)> RetentionPreviewAsync(CancellationToken ct = default) =>
        GetAsync<ExecutionRetentionPreview>("api/execution-evidence/retention/preview", ct);

    public Task<(ExecutionDeletionResult? Result, string? Error)> ApplyRetentionAsync(int confirm, CancellationToken ct = default) =>
        SendAsync<ExecutionDeletionResult>(HttpMethod.Post, $"api/execution-evidence/retention/apply?confirm={confirm}", null, ct);

    public Task<(IReadOnlyList<ExecutionAuditEntry>? Entries, string? Error)> AuditAsync(CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<ExecutionAuditEntry>>("api/execution-evidence/audit?take=20", ct);

    public async Task<(byte[]? Zip, string? Error)> ExportAsync(IReadOnlyList<Guid> runIds, CancellationToken ct = default)
    {
        try
        {
            using var response = await http.PostAsJsonAsync("api/execution-evidence/runs/export", new ExecutionExportRequest { RunIds = [.. runIds] }, Json, ct);
            if (response.IsSuccessStatusCode) return (await response.Content.ReadAsByteArrayAsync(ct), null);
            return (null, await ErrorAsync(response, ct));
        }
        catch (Exception e) when (BackendRequestClassifier.IsRequestFailure(e))
        { return (null, BackendRequestClassifier.FromException(Title, e, "POST api/execution-evidence/runs/export", ct).UserMessage); }
    }

    /// <summary>A write request; a refusal's <c>{ "message": … }</c> is shown as is, 401/403 as a permission message.</summary>
    private async Task<(T? Body, string? Error)> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body, options: Json) };
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return (default, await ErrorAsync(response, ct));
            return response.StatusCode == System.Net.HttpStatusCode.NoContent || response.Content.Headers.ContentLength == 0 ? (default, null) : (await response.Content.ReadFromJsonAsync<T>(Json, ct), null);
        }
        catch (Exception e) when (BackendRequestClassifier.IsRequestFailure(e))
        { return (default, BackendRequestClassifier.FromException(Title, e, $"{method} {path}", ct).UserMessage); }
    }

    private static async Task<string> ErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            return $"{Title}: you do not have permission to manage stored evidence (HTTP {(int)response.StatusCode}).";
        try
        {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String) return m.GetString()!;
        }
        catch (JsonException) { }
        return response.StatusCode == System.Net.HttpStatusCode.NotFound ? "Not found." : $"{Title}: the request was refused (HTTP {(int)response.StatusCode}).";
    }

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
            if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
                return (null, $"{Title}: you do not have permission to run tests against a target (HTTP {(int)response.StatusCode}).");
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
