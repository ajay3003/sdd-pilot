using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BirkNext.Integrations;

namespace BirkNext.Web.Services;

/// <summary>A backend answer the panel can show as-is: the value, or why there is none (including sign-in and permission states).</summary>
public sealed record ActiveEventApiResult<T>(T? Value, string? Error, HttpStatusCode? Status = null)
{
    public bool Unauthorized => Status == HttpStatusCode.Unauthorized;
    public bool Forbidden => Status == HttpStatusCode.Forbidden;
}

public sealed record ActiveEventHistoryFilter(string? ExtensionId = null, string? ScenarioId = null, ActiveEventRunStatus? Status = null, DateTimeOffset? From = null);

/// <summary>
/// Client for IQR → Active event tests (shared by every scenario provider). It can only start a registered scenario by id; there is no
/// call that carries a payload, a namespace, a hub, a credential, an environment type or a target URL. Every safety gate is the backend's.
/// Protected calls receive the access token from the authenticated HTTP pipeline when Entra is configured (see Program).
/// </summary>
public interface IActiveEventsApiService
{
    Task<ActiveEventApiResult<ActiveEventEnvironmentTrust>> TrustAsync(string environmentId, CancellationToken ct = default);
    Task<ActiveEventApiResult<IReadOnlyList<ActiveEventProviderSummary>>> ProvidersAsync(string environmentId, CancellationToken ct = default);
    Task<ActiveEventApiResult<IReadOnlyList<ActiveEventScenarioDescriptor>>> ScenariosAsync(string environmentId, string integrationId, CancellationToken ct = default);
    Task<ActiveEventApiResult<ActiveEventReadiness>> ReadinessAsync(string environmentId, string integrationId, string extensionId, string scenarioId, Guid? snapshotId, CancellationToken ct = default);
    Task<ActiveEventApiResult<ActiveEventRunResult>> StartAsync(ActiveEventRunRequest request, CancellationToken ct = default);
    Task<ActiveEventApiResult<ActiveEventRunResult>> GetAsync(Guid runId, CancellationToken ct = default);
    Task<bool> CancelAsync(Guid runId, CancellationToken ct = default);
    Task<ActiveEventApiResult<IReadOnlyList<ActiveEventRunSummary>>> HistoryAsync(string environmentId, ActiveEventHistoryFilter filter, CancellationToken ct = default);
}

public sealed class ActiveEventsApiService(HttpClient http) : IActiveEventsApiService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static string E(string value) => Uri.EscapeDataString(value);

    public Task<ActiveEventApiResult<ActiveEventEnvironmentTrust>> TrustAsync(string environmentId, CancellationToken ct = default) =>
        GetAsync<ActiveEventEnvironmentTrust>($"api/active-events/environments/{E(environmentId)}/trust", ct);

    public async Task<ActiveEventApiResult<IReadOnlyList<ActiveEventProviderSummary>>> ProvidersAsync(string environmentId, CancellationToken ct = default) =>
        List(await GetAsync<List<ActiveEventProviderSummary>>($"api/active-events/providers?environmentId={E(environmentId)}", ct));

    public async Task<ActiveEventApiResult<IReadOnlyList<ActiveEventScenarioDescriptor>>> ScenariosAsync(string environmentId, string integrationId, CancellationToken ct = default) =>
        List(await GetAsync<List<ActiveEventScenarioDescriptor>>($"api/active-events/scenarios?environmentId={E(environmentId)}&integrationId={E(integrationId)}", ct));

    public Task<ActiveEventApiResult<ActiveEventReadiness>> ReadinessAsync(string environmentId, string integrationId, string extensionId, string scenarioId, Guid? snapshotId, CancellationToken ct = default) =>
        GetAsync<ActiveEventReadiness>($"api/active-events/readiness?environmentId={E(environmentId)}&integrationId={E(integrationId)}&extensionId={E(extensionId)}&scenarioId={E(scenarioId)}" +
            (snapshotId is { } id ? $"&snapshotId={id}" : ""), ct);

    public async Task<ActiveEventApiResult<ActiveEventRunResult>> StartAsync(ActiveEventRunRequest request, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync("api/active-events/runs", request, Json, ct);
        return await ReadAsync<ActiveEventRunResult>(response, ct);
    }

    public Task<ActiveEventApiResult<ActiveEventRunResult>> GetAsync(Guid runId, CancellationToken ct = default) => GetAsync<ActiveEventRunResult>($"api/active-events/runs/{runId}", ct);

    public async Task<bool> CancelAsync(Guid runId, CancellationToken ct = default)
    {
        using var response = await http.PostAsync($"api/active-events/runs/{runId}/cancel", null, ct);
        return response.IsSuccessStatusCode;
    }

    public async Task<ActiveEventApiResult<IReadOnlyList<ActiveEventRunSummary>>> HistoryAsync(string environmentId, ActiveEventHistoryFilter filter, CancellationToken ct = default)
    {
        var query = $"api/active-events/runs?environmentId={E(environmentId)}" +
            (filter.ExtensionId is { Length: > 0 } extension ? $"&extensionId={E(extension)}" : "") +
            (filter.ScenarioId is { Length: > 0 } scenario ? $"&scenarioId={E(scenario)}" : "") +
            (filter.Status is { } status ? $"&status={status}" : "") +
            (filter.From is { } from ? $"&from={E(from.ToString("O"))}" : "");
        return List(await GetAsync<List<ActiveEventRunSummary>>(query, ct));
    }

    private async Task<ActiveEventApiResult<T>> GetAsync<T>(string url, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, ct);
        return await ReadAsync<T>(response, ct);
    }

    private static ActiveEventApiResult<IReadOnlyList<T>> List<T>(ActiveEventApiResult<List<T>> result) => new(result.Value, result.Error, result.Status);

    private static async Task<ActiveEventApiResult<T>> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return new(await response.Content.ReadFromJsonAsync<T>(Json, ct), null, response.StatusCode);
        var message = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "Sign-in required: active event execution needs an authenticated BirkNext API token.",
            HttpStatusCode.Forbidden => "Not authorized: your account does not hold the ActiveEventExecute permission.",
            HttpStatusCode.NotFound => "Not found.",
            _ => null,
        };
        if (message is null)
        {
            try { message = (await response.Content.ReadFromJsonAsync<JsonElement>(Json, ct)).GetProperty("message").GetString(); }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or NotSupportedException) { }
        }
        return new(default, message ?? $"The backend refused the request (HTTP {(int)response.StatusCode}).", response.StatusCode);
    }
}

/// <summary>
/// Whether this deployment configured Entra for the browser. Without it no access token can be acquired: the review UI keeps working, and
/// real Active Event execution is shown as "Authentication not configured" and stays disabled (the API refuses it anyway).
/// </summary>
public sealed record ActiveEventAuthenticationState(bool Configured, string Detail)
{
    public static ActiveEventAuthenticationState NotConfigured { get; } = new(false,
        "Authentication not configured: the Entra tenant, client id and BirkNext API scope are not set for this deployment, so no access token can be requested. Real execution stays disabled.");
}
