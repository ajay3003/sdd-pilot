using System.Net.Http.Json;
using System.Text.Json;
using BirkNext.Integrations;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

/// <summary>
/// Client for the backend integration catalog (Target Environment → Integrations) and Integration Quality Review over it.
/// The catalog is persisted by the backend; this client never holds a secret — authentication is a mechanism name.
/// </summary>
public interface IIntegrationCatalogApiService
{
    Task<IntegrationCatalog> GetCatalogAsync(FrontendAnalysisProfile profile, CancellationToken ct = default);
    Task<IntegrationDefinition> CreateAsync(string environmentId, IntegrationDefinition definition, CancellationToken ct = default);
    Task<IntegrationDefinition> UpdateAsync(string environmentId, IntegrationDefinition definition, CancellationToken ct = default);
    Task<IntegrationDefinition> SetEnabledAsync(string environmentId, string id, bool enabled, CancellationToken ct = default);
    Task DeleteAsync(string environmentId, string id, CancellationToken ct = default);
    Task<IntegrationPlatform> UpdatePlatformAsync(string environmentId, IntegrationPlatform platform, CancellationToken ct = default);
    /// <summary>Imports integrations still stored in the browser Target Environment profile. The backend imports once per environment.</summary>
    Task<int> ImportLegacyAsync(FrontendAnalysisProfile profile, CancellationToken ct = default);
    Task<IntegrationReviewReadiness> ReadinessAsync(FrontendAnalysisProfile profile, CancellationToken ct = default);
    Task<IntegrationReviewResult> RunAsync(FrontendAnalysisProfile profile, CancellationToken ct = default);
    Task<IReadOnlyList<IntegrationReviewRunSummary>> HistoryAsync(string environmentId, CancellationToken ct = default);
    Task<IntegrationReviewResult?> GetRunAsync(Guid runId, CancellationToken ct = default);
}

public sealed class IntegrationCatalogApiService(HttpClient http) : IIntegrationCatalogApiService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Environment identity plus the two facts the backend uses to decide whether the known M2LB DEV seed applies.</summary>
    private static string Scope(FrontendAnalysisProfile profile) =>
        $"environmentId={Uri.EscapeDataString(profile.Id)}&environmentType={Uri.EscapeDataString(profile.EnvironmentType.ToString())}&targetUrl={Uri.EscapeDataString(profile.TargetUrl ?? "")}";

    private static string Env(string environmentId) => $"environmentId={Uri.EscapeDataString(environmentId)}";

    public async Task<IntegrationCatalog> GetCatalogAsync(FrontendAnalysisProfile profile, CancellationToken ct = default) =>
        await http.GetFromJsonAsync<IntegrationCatalog>($"api/integrations?{Scope(profile)}", Json, ct) ?? new IntegrationCatalog { EnvironmentId = profile.Id };

    public async Task<IntegrationDefinition> CreateAsync(string environmentId, IntegrationDefinition definition, CancellationToken ct = default) =>
        await Read<IntegrationDefinition>(await http.PostAsJsonAsync($"api/integrations?{Env(environmentId)}", definition, Json, ct), ct);

    public async Task<IntegrationDefinition> UpdateAsync(string environmentId, IntegrationDefinition definition, CancellationToken ct = default) =>
        await Read<IntegrationDefinition>(await http.PutAsJsonAsync($"api/integrations/{Uri.EscapeDataString(definition.Id)}?{Env(environmentId)}", definition, Json, ct), ct);

    public async Task<IntegrationDefinition> SetEnabledAsync(string environmentId, string id, bool enabled, CancellationToken ct = default) =>
        await Read<IntegrationDefinition>(await http.PostAsync($"api/integrations/{Uri.EscapeDataString(id)}/enabled?{Env(environmentId)}&enabled={(enabled ? "true" : "false")}", null, ct), ct);

    public async Task DeleteAsync(string environmentId, string id, CancellationToken ct = default) =>
        (await http.DeleteAsync($"api/integrations/{Uri.EscapeDataString(id)}?{Env(environmentId)}", ct)).EnsureSuccessStatusCode();

    public async Task<IntegrationPlatform> UpdatePlatformAsync(string environmentId, IntegrationPlatform platform, CancellationToken ct = default) =>
        await Read<IntegrationPlatform>(await http.PutAsJsonAsync($"api/integrations/platforms/{Uri.EscapeDataString(platform.Id)}?{Env(environmentId)}", platform, Json, ct), ct);

    public async Task<int> ImportLegacyAsync(FrontendAnalysisProfile profile, CancellationToken ct = default)
    {
        if (profile.Integrations.Count == 0) return 0;
        var response = await http.PostAsJsonAsync($"api/integrations/import-legacy?{Env(profile.Id)}", profile.Integrations, Json, ct);
        return await Read<int>(response, ct);
    }

    public async Task<IntegrationReviewReadiness> ReadinessAsync(FrontendAnalysisProfile profile, CancellationToken ct = default) =>
        await http.GetFromJsonAsync<IntegrationReviewReadiness>($"api/integration-review/readiness?{Scope(profile)}", Json, ct) ?? new IntegrationReviewReadiness { EnvironmentId = profile.Id };

    public async Task<IntegrationReviewResult> RunAsync(FrontendAnalysisProfile profile, CancellationToken ct = default)
    {
        var request = new IntegrationReviewRunRequest { EnvironmentId = profile.Id, EnvironmentName = profile.Name };
        var scope = $"environmentType={Uri.EscapeDataString(profile.EnvironmentType.ToString())}&targetUrl={Uri.EscapeDataString(profile.TargetUrl ?? "")}";
        return await Read<IntegrationReviewResult>(await http.PostAsJsonAsync($"api/integration-review/run?{scope}", request, Json, ct), ct);
    }

    public async Task<IReadOnlyList<IntegrationReviewRunSummary>> HistoryAsync(string environmentId, CancellationToken ct = default) =>
        await http.GetFromJsonAsync<List<IntegrationReviewRunSummary>>($"api/integration-review/runs?{Env(environmentId)}", Json, ct) ?? [];

    public async Task<IntegrationReviewResult?> GetRunAsync(Guid runId, CancellationToken ct = default) =>
        await http.GetFromJsonAsync<IntegrationReviewResult>($"api/integration-review/runs/{runId}", Json, ct);

    private static async Task<T> Read<T>(HttpResponseMessage response, CancellationToken ct)
    {
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>(Json, ct))!;
    }
}
