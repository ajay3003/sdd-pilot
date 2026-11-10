using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using BirkNext.Integrations;
using BirkNext.IntegrationSuites;

namespace BirkNext.Web.Services;

/// <summary>Generated integration suites: analysed source snapshots to choose from, scopes, generate (draft-only), read, review, authorised run.</summary>
public interface IIntegrationSuiteApiService
{
    Task<(List<IqrSourceSnapshotSummary>? Snapshots, string? Error)> SnapshotsAsync(CancellationToken ct = default);
    Task<(List<IntegrationSuiteScope>? Scopes, string? Error)> ScopesAsync(CancellationToken ct = default);
    Task<(List<IntegrationSuiteSummary>? Suites, string? Error)> ListAsync(CancellationToken ct = default);
    Task<(IntegrationSuite? Suite, string? Error)> GetAsync(Guid id, CancellationToken ct = default);
    Task<(IntegrationSuiteGenerateResult? Result, string? Error)> GenerateAsync(IntegrationSuiteGenerateRequest request, CancellationToken ct = default);
    Task<string?> ReviewAsync(Guid id, IntegrationSuiteReviewRequest request, CancellationToken ct = default);
    Task<(IntegrationSuiteExecuteResult? Result, string? Error)> ExecuteAsync(Guid id, IntegrationSuiteExecuteRequest request, CancellationToken ct = default);

    // ── Multi-source (composite) ──
    Task<(CompositeDiscovery? Discovery, string? Error)> DiscoverAsync(string scopeId, Guid? contextId, CancellationToken ct = default) =>
        Task.FromResult<(CompositeDiscovery?, string?)>((null, "Not available."));
    Task<(List<CompositeContextSummary>? Contexts, string? Error)> ContextsAsync(CancellationToken ct = default) =>
        Task.FromResult<(List<CompositeContextSummary>?, string?)>(([], null));
    Task<(CompositeSourceContext? Context, string? Error)> ContextAsync(Guid id, CancellationToken ct = default) =>
        Task.FromResult<(CompositeSourceContext?, string?)>((null, "Not available."));
    Task<(CompositeContextSaveResult? Result, string? Error)> SaveContextAsync(CompositeContextSaveRequest request, CancellationToken ct = default) =>
        Task.FromResult<(CompositeContextSaveResult?, string?)>((null, "Not available."));
    Task<(CompositePlan? Plan, string? Error)> ApprovePlanAsync(Guid suiteId, CancellationToken ct = default) =>
        Task.FromResult<(CompositePlan?, string?)>((null, "Not available."));
    Task<(CompositePreflight? Preflight, string? Error)> PreflightAsync(Guid suiteId, CompositeRunRequest request, CancellationToken ct = default) =>
        Task.FromResult<(CompositePreflight?, string?)>((null, "Not available."));
    Task<(CompositeRunResult? Result, string? Error)> RunCompositeAsync(Guid suiteId, CompositeRunRequest request, CancellationToken ct = default) =>
        Task.FromResult<(CompositeRunResult?, string?)>((null, "Not available."));
    Task<(CompositeRunView? Run, string? Error)> CompositeRunAsync(Guid suiteId, Guid runId, CancellationToken ct = default) =>
        Task.FromResult<(CompositeRunView?, string?)>((null, "Not available."));
}

public sealed class IntegrationSuiteApiService(HttpClient http) : IIntegrationSuiteApiService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private const string Base = "api/integration-suites";
    private const string Area = "Integration suites";

    // The Source Analysis selector list (all environments): the same list the Source Analysis page shows.
    public Task<(List<IqrSourceSnapshotSummary>? Snapshots, string? Error)> SnapshotsAsync(CancellationToken ct = default) =>
        GetAsync<List<IqrSourceSnapshotSummary>>("api/source-analysis?environmentId=", ct);

    public Task<(List<IntegrationSuiteScope>? Scopes, string? Error)> ScopesAsync(CancellationToken ct = default) => GetAsync<List<IntegrationSuiteScope>>($"{Base}/scopes", ct);

    public Task<(List<IntegrationSuiteSummary>? Suites, string? Error)> ListAsync(CancellationToken ct = default) => GetAsync<List<IntegrationSuiteSummary>>(Base, ct);

    public Task<(IntegrationSuite? Suite, string? Error)> GetAsync(Guid id, CancellationToken ct = default) => GetAsync<IntegrationSuite>($"{Base}/{id:D}", ct);

    public async Task<(IntegrationSuiteGenerateResult? Result, string? Error)> GenerateAsync(IntegrationSuiteGenerateRequest request, CancellationToken ct = default)
    {
        try
        {
            using var response = await http.PostAsJsonAsync(Base, request, Json, ct);
            if (response.IsSuccessStatusCode) return (await response.Content.ReadFromJsonAsync<IntegrationSuiteGenerateResult>(Json, ct), null);
            return (null, response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized ? "You do not have permission to generate suites (integration configuration write permission)." : await Message(response, ct));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException) { return (null, BackendRequestClassifier.FromException(Area, ex).UserMessage); }
    }

    public async Task<string?> ReviewAsync(Guid id, IntegrationSuiteReviewRequest request, CancellationToken ct = default)
    {
        try
        {
            using var response = await http.PostAsJsonAsync($"{Base}/{id:D}/reviews", request, Json, ct);
            if (response.IsSuccessStatusCode) return null;
            return response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized ? "You do not have permission to record reviews (integration configuration write permission)." : await Message(response, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { return BackendRequestClassifier.FromException(Area, ex).UserMessage; }
    }

    public async Task<(IntegrationSuiteExecuteResult? Result, string? Error)> ExecuteAsync(Guid id, IntegrationSuiteExecuteRequest request, CancellationToken ct = default)
    {
        try
        {
            using var response = await http.PostAsJsonAsync($"{Base}/{id:D}/runs", request, Json, ct);
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized) return (null, "You do not have permission to execute (active execution permission).");
            if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.BadRequest or HttpStatusCode.NotFound)
            {
                try { if (await response.Content.ReadFromJsonAsync<IntegrationSuiteExecuteResult>(Json, ct) is { } r) return (r, null); }
                catch (JsonException) { }
            }
            return (null, await Message(response, ct));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException) { return (null, BackendRequestClassifier.FromException(Area, ex).UserMessage); }
    }

    // ── Multi-source (composite) ──

    public Task<(CompositeDiscovery? Discovery, string? Error)> DiscoverAsync(string scopeId, Guid? contextId, CancellationToken ct = default) =>
        GetAsync<CompositeDiscovery>($"{Base}/composite/discovery?scopeId={Uri.EscapeDataString(scopeId)}{(contextId is { } c ? $"&contextId={c:D}" : "")}", ct);

    public Task<(List<CompositeContextSummary>? Contexts, string? Error)> ContextsAsync(CancellationToken ct = default) => GetAsync<List<CompositeContextSummary>>($"{Base}/composite/contexts", ct);

    public Task<(CompositeSourceContext? Context, string? Error)> ContextAsync(Guid id, CancellationToken ct = default) => GetAsync<CompositeSourceContext>($"{Base}/composite/contexts/{id:D}", ct);

    public async Task<(CompositeContextSaveResult? Result, string? Error)> SaveContextAsync(CompositeContextSaveRequest request, CancellationToken ct = default)
    {
        try
        {
            using var response = await http.PostAsJsonAsync($"{Base}/composite/contexts", request, Json, ct);
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized) return (null, "You do not have permission to associate sources (integration configuration write permission).");
            if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.BadRequest or HttpStatusCode.Conflict)
            {
                try { if (await response.Content.ReadFromJsonAsync<CompositeContextSaveResult>(Json, ct) is { } r) return (r, null); }
                catch (JsonException) { }
            }
            return (null, await Message(response, ct));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException) { return (null, BackendRequestClassifier.FromException(Area, ex).UserMessage); }
    }

    public async Task<(CompositePlan? Plan, string? Error)> ApprovePlanAsync(Guid suiteId, CancellationToken ct = default)
    {
        try
        {
            using var response = await http.PostAsync($"{Base}/{suiteId:D}/plan/approve", null, ct);
            if (response.IsSuccessStatusCode) return (await response.Content.ReadFromJsonAsync<CompositePlan>(Json, ct), null);
            return (null, response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized ? "You do not have permission to approve the plan (integration configuration write permission)." : await Message(response, ct));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException) { return (null, BackendRequestClassifier.FromException(Area, ex).UserMessage); }
    }

    public async Task<(CompositePreflight? Preflight, string? Error)> PreflightAsync(Guid suiteId, CompositeRunRequest request, CancellationToken ct = default)
    {
        try
        {
            using var response = await http.PostAsJsonAsync($"{Base}/{suiteId:D}/composite-runs/preflight", request, Json, ct);
            return response.IsSuccessStatusCode ? (await response.Content.ReadFromJsonAsync<CompositePreflight>(Json, ct), null) : (null, await Message(response, ct));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException) { return (null, BackendRequestClassifier.FromException(Area, ex).UserMessage); }
    }

    public async Task<(CompositeRunResult? Result, string? Error)> RunCompositeAsync(Guid suiteId, CompositeRunRequest request, CancellationToken ct = default)
    {
        try
        {
            using var response = await http.PostAsJsonAsync($"{Base}/{suiteId:D}/composite-runs", request, Json, ct);
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized) return (null, "You do not have permission to execute (active execution permission).");
            if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.BadRequest or HttpStatusCode.NotFound)
            {
                try { if (await response.Content.ReadFromJsonAsync<CompositeRunResult>(Json, ct) is { } r) return (r, null); }
                catch (JsonException) { }
            }
            return (null, await Message(response, ct));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException) { return (null, BackendRequestClassifier.FromException(Area, ex).UserMessage); }
    }

    public Task<(CompositeRunView? Run, string? Error)> CompositeRunAsync(Guid suiteId, Guid runId, CancellationToken ct = default) =>
        GetAsync<CompositeRunView>($"{Base}/{suiteId:D}/composite-runs/{runId:D}", ct);

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
                    if (o.TryGetProperty(key, out var m) && m.ValueKind == JsonValueKind.String) return m.GetString() ?? text;
        }
        catch (JsonException) { }
        return string.IsNullOrWhiteSpace(text) ? $"The request failed (HTTP {(int)response.StatusCode})." : text.Trim('"');
    }
}
