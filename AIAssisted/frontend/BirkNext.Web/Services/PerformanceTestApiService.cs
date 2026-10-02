using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BirkNext.PerformanceTests;

namespace BirkNext.Web.Services;

public sealed record PerformanceApiResult<T>(T? Value, string? Error, List<string> Blockers)
{
    public static PerformanceApiResult<T> Ok(T value) => new(value, null, []);
    public static PerformanceApiResult<T> Fail(string error, List<string>? blockers = null) => new(default, error, blockers ?? []);
}

/// <summary>Performance Test Review backend client. The backend owns safety and readiness; this client never sends a script or a credential.</summary>
public interface IPerformanceTestApiService
{
    Task<PerformanceTestOverview?> OverviewAsync(string environmentId, CancellationToken ct = default);
    /// <summary>Installation-level provider availability and capabilities (System Settings → Performance Test Engines).</summary>
    Task<List<PerformanceProviderStatus>> ProvidersAsync(CancellationToken ct = default) => Task.FromResult<List<PerformanceProviderStatus>>([]);
    /// <summary>Explicit container-network check (one request from the k6 container). Null when the backend did not answer.</summary>
    Task<PerformanceApiResult<PerformanceReachability>> NetworkCheckAsync(string environmentId, string definitionId, CancellationToken ct = default) =>
        Task.FromResult(PerformanceApiResult<PerformanceReachability>.Fail("Unavailable."));
    /// <summary>Pulls the provider image when the backend policy allows it.</summary>
    Task<PerformanceApiResult<PerformanceProviderStatus>> PrepareProviderAsync(string providerId, CancellationToken ct = default) =>
        Task.FromResult(PerformanceApiResult<PerformanceProviderStatus>.Fail("Unavailable."));
    Task<PerformanceApiResult<PerformanceTestDefinition>> SaveDefinitionAsync(string environmentId, PerformanceTestDefinition definition, bool create, CancellationToken ct = default);
    Task<PerformanceApiResult<PerformanceTestDataProfile>> SaveDataProfileAsync(string environmentId, PerformanceTestDataProfile profile, CancellationToken ct = default);
    Task<PerformanceTestReadiness?> ReadinessAsync(string environmentId, string definitionId, CancellationToken ct = default);
    Task<PerformanceApiResult<PerformanceTestRun>> StartAsync(string environmentId, PerformanceRunRequest request, CancellationToken ct = default);
    Task<PerformanceTestRun?> RunAsync(string environmentId, Guid runId, CancellationToken ct = default);
    Task<List<PerformanceTestRun>> RunsAsync(string environmentId, string? definitionId, CancellationToken ct = default);
    Task<PerformanceTestRun?> CancelAsync(string environmentId, Guid runId, CancellationToken ct = default);
    Task<List<PerformanceBaseline>> BaselinesAsync(string environmentId, string? definitionId, CancellationToken ct = default);
    Task<PerformanceBaselinePromotionResult> PromoteAsync(string environmentId, PerformanceBaselinePromotion request, CancellationToken ct = default);
    Task<PerformanceRunComparison?> CompareAsync(string environmentId, Guid current, Guid? reference, string? baselineId, CancellationToken ct = default);
}

public sealed class PerformanceTestApiService(HttpClient http) : IPerformanceTestApiService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string Base = "api/performance-tests";
    private static string Env(string environmentId) => "environmentId=" + Uri.EscapeDataString(environmentId);

    public async Task<PerformanceTestOverview?> OverviewAsync(string environmentId, CancellationToken ct = default) =>
        await Get<PerformanceTestOverview>($"{Base}?{Env(environmentId)}", ct);

    public async Task<PerformanceApiResult<PerformanceTestDefinition>> SaveDefinitionAsync(string environmentId, PerformanceTestDefinition definition, bool create, CancellationToken ct = default)
    {
        try
        {
            using var response = create
                ? await http.PostAsJsonAsync($"{Base}/definitions?{Env(environmentId)}", definition, Json, ct)
                : await http.PutAsJsonAsync($"{Base}/definitions/{Uri.EscapeDataString(definition.Id)}?{Env(environmentId)}", definition, Json, ct);
            return response.IsSuccessStatusCode ? PerformanceApiResult<PerformanceTestDefinition>.Ok((await response.Content.ReadFromJsonAsync<PerformanceTestDefinition>(Json, ct))!)
                : PerformanceApiResult<PerformanceTestDefinition>.Fail(await Message(response, ct));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException) { return PerformanceApiResult<PerformanceTestDefinition>.Fail("The BirkNext backend did not answer."); }
    }

    public async Task<PerformanceApiResult<PerformanceTestDataProfile>> SaveDataProfileAsync(string environmentId, PerformanceTestDataProfile profile, CancellationToken ct = default)
    {
        try
        {
            using var response = await http.PostAsJsonAsync($"{Base}/data-profiles?{Env(environmentId)}", profile, Json, ct);
            return response.IsSuccessStatusCode ? PerformanceApiResult<PerformanceTestDataProfile>.Ok((await response.Content.ReadFromJsonAsync<PerformanceTestDataProfile>(Json, ct))!)
                : PerformanceApiResult<PerformanceTestDataProfile>.Fail(await Message(response, ct));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException) { return PerformanceApiResult<PerformanceTestDataProfile>.Fail("The BirkNext backend did not answer."); }
    }

    public async Task<List<PerformanceProviderStatus>> ProvidersAsync(CancellationToken ct = default) => await Get<List<PerformanceProviderStatus>>($"{Base}/providers", ct) ?? [];

    public Task<PerformanceApiResult<PerformanceReachability>> NetworkCheckAsync(string environmentId, string definitionId, CancellationToken ct = default) =>
        Post<PerformanceReachability>($"{Base}/definitions/{Uri.EscapeDataString(definitionId)}/network-check?{Env(environmentId)}", ct);

    public Task<PerformanceApiResult<PerformanceProviderStatus>> PrepareProviderAsync(string providerId, CancellationToken ct = default) =>
        Post<PerformanceProviderStatus>($"{Base}/providers/{Uri.EscapeDataString(providerId)}/prepare", ct);

    private async Task<PerformanceApiResult<T>> Post<T>(string url, CancellationToken ct)
    {
        try
        {
            using var response = await http.PostAsync(url, null, ct);
            return response.IsSuccessStatusCode ? PerformanceApiResult<T>.Ok((await response.Content.ReadFromJsonAsync<T>(Json, ct))!) : PerformanceApiResult<T>.Fail(await Message(response, ct));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException) { return PerformanceApiResult<T>.Fail("The BirkNext backend did not answer."); }
    }

    public Task<PerformanceTestReadiness?> ReadinessAsync(string environmentId, string definitionId, CancellationToken ct = default) =>
        Get<PerformanceTestReadiness>($"{Base}/definitions/{Uri.EscapeDataString(definitionId)}/readiness?{Env(environmentId)}", ct);

    public async Task<PerformanceApiResult<PerformanceTestRun>> StartAsync(string environmentId, PerformanceRunRequest request, CancellationToken ct = default)
    {
        try
        {
            using var response = await http.PostAsJsonAsync($"{Base}/runs?{Env(environmentId)}", request, Json, ct);
            if (response.IsSuccessStatusCode) return PerformanceApiResult<PerformanceTestRun>.Ok((await response.Content.ReadFromJsonAsync<PerformanceTestRun>(Json, ct))!);
            if (response.StatusCode == HttpStatusCode.UnprocessableEntity && await response.Content.ReadFromJsonAsync<BlockedBody>(Json, ct) is { } blocked)
                return PerformanceApiResult<PerformanceTestRun>.Fail(blocked.Message ?? "The performance test cannot run.", blocked.Blockers ?? []);
            return PerformanceApiResult<PerformanceTestRun>.Fail(await Message(response, ct));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException) { return PerformanceApiResult<PerformanceTestRun>.Fail("The BirkNext backend did not answer."); }
    }

    public Task<PerformanceTestRun?> RunAsync(string environmentId, Guid runId, CancellationToken ct = default) => Get<PerformanceTestRun>($"{Base}/runs/{runId}?{Env(environmentId)}", ct);

    public async Task<List<PerformanceTestRun>> RunsAsync(string environmentId, string? definitionId, CancellationToken ct = default) =>
        await Get<List<PerformanceTestRun>>($"{Base}/runs?{Env(environmentId)}{(definitionId is null ? "" : "&definitionId=" + Uri.EscapeDataString(definitionId))}", ct) ?? [];

    public async Task<PerformanceTestRun?> CancelAsync(string environmentId, Guid runId, CancellationToken ct = default)
    {
        try
        {
            using var response = await http.PostAsync($"{Base}/runs/{runId}/cancel?{Env(environmentId)}", null, ct);
            return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<PerformanceTestRun>(Json, ct) : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException) { return null; }
    }

    public async Task<List<PerformanceBaseline>> BaselinesAsync(string environmentId, string? definitionId, CancellationToken ct = default) =>
        await Get<List<PerformanceBaseline>>($"{Base}/baselines?{Env(environmentId)}{(definitionId is null ? "" : "&definitionId=" + Uri.EscapeDataString(definitionId))}", ct) ?? [];

    public async Task<PerformanceBaselinePromotionResult> PromoteAsync(string environmentId, PerformanceBaselinePromotion request, CancellationToken ct = default)
    {
        try
        {
            using var response = await http.PostAsJsonAsync($"{Base}/baselines?{Env(environmentId)}", request, Json, ct);
            return await response.Content.ReadFromJsonAsync<PerformanceBaselinePromotionResult>(Json, ct) ?? new(null, null, "No answer.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException) { return new(null, null, "The BirkNext backend did not answer."); }
    }

    public Task<PerformanceRunComparison?> CompareAsync(string environmentId, Guid current, Guid? reference, string? baselineId, CancellationToken ct = default) =>
        Get<PerformanceRunComparison>($"{Base}/compare?{Env(environmentId)}&current={current}{(reference is { } r ? "&reference=" + r : "")}{(baselineId is null ? "" : "&baselineId=" + Uri.EscapeDataString(baselineId))}", ct);

    private async Task<T?> Get<T>(string url, CancellationToken ct)
    {
        try { return await http.GetFromJsonAsync<T>(url, Json, ct); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or NotSupportedException) { return default; }
    }

    private static async Task<string> Message(HttpResponseMessage response, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct);
        try { if (JsonDocument.Parse(text).RootElement is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty("message", out var m)) return m.GetString() ?? text; }
        catch (JsonException) { }
        return string.IsNullOrWhiteSpace(text) ? $"The request failed (HTTP {(int)response.StatusCode})." : text.Trim('"');
    }

    private sealed record BlockedBody(string? Message, List<string>? Blockers);
}
