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
    Task<IntegrationMappingEvidenceCheck> CheckMappingAsync(string environmentId, string integrationId, CancellationToken ct = default);
    /// <summary>The environment's application-messaging (Wolverine) evidence, or null when no source was analyzed.</summary>
    Task<ApplicationMessagingEvidenceSet?> ApplicationMessagingAsync(string environmentId, CancellationToken ct = default);
    /// <summary>Uploads source archives for read-only analysis; returns the new evidence or the reason nothing was stored.</summary>
    Task<(ApplicationMessagingEvidenceSet? Set, string? Error)> AnalyzeApplicationMessagingAsync(string environmentId, IReadOnlyList<(string FileName, Stream Content)> archives, CancellationToken ct = default);
    Task<ApplicationMessagingEvidenceSet> BindApplicationMessagingAsync(string environmentId, string applicationId, string? consumer, CancellationToken ct = default);
    /// <summary>Read-only "Test Service Bus" of one Service Bus platform (topology, code routes and — when configured — runtime metadata).</summary>
    Task<ServiceBusEvidenceCheck> CheckServiceBusAsync(string environmentId, string platformId, CancellationToken ct = default);
    Task<IntegrationCatalog> GetCatalogAsync(FrontendAnalysisProfile profile, CancellationToken ct = default);
    Task<IntegrationDefinition> CreateAsync(string environmentId, IntegrationDefinition definition, CancellationToken ct = default);
    Task<IntegrationDefinition> UpdateAsync(string environmentId, IntegrationDefinition definition, CancellationToken ct = default);
    Task<IntegrationDefinition> SetEnabledAsync(string environmentId, string id, bool enabled, CancellationToken ct = default);
    Task DeleteAsync(string environmentId, string id, CancellationToken ct = default);
    Task<IntegrationPlatform> UpdatePlatformAsync(string environmentId, IntegrationPlatform platform, CancellationToken ct = default);
    /// <summary>Imports integrations still stored in the browser Target Environment profile. The backend imports once per environment.</summary>
    Task<int> ImportLegacyAsync(FrontendAnalysisProfile profile, CancellationToken ct = default);
    Task<IReadOnlyList<IntegrationContractArtifact>> ContractsAsync(string environmentId, CancellationToken ct = default);
    /// <summary>Uploads a trusted JSON Schema for one side of an integration. Returns the stored metadata, or the validation reason.</summary>
    Task<(IntegrationContractArtifact? Artifact, string? Error)> SaveContractAsync(IntegrationContractUpload upload, CancellationToken ct = default);
    Task RemoveContractAsync(string environmentId, string integrationId, IntegrationContractRole role, CancellationToken ct = default);
    Task<IntegrationReviewReadiness> ReadinessAsync(FrontendAnalysisProfile profile, CancellationToken ct = default);
    Task<IntegrationReviewResult> RunAsync(FrontendAnalysisProfile profile, CancellationToken ct = default);
    Task<IReadOnlyList<IntegrationReviewRunSummary>> HistoryAsync(string environmentId, CancellationToken ct = default);
    Task<IntegrationReviewResult?> GetRunAsync(Guid runId, CancellationToken ct = default);

    // SCIM identity provisioning. Default members keep other implementations (test fakes) valid; the backend client overrides them.
    /// <summary>The environment's latest SCIM source analysis and stored safe-check history.</summary>
    Task<ScimEvidenceOverview> ScimOverviewAsync(string environmentId, CancellationToken ct = default) => Task.FromResult(new ScimEvidenceOverview());
    /// <summary>Uploads repository archives for read-only SCIM source analysis.</summary>
    Task<(ScimSourceEvidence? Evidence, string? Error)> AnalyzeScimSourceAsync(string environmentId, IReadOnlyList<(string FileName, Stream Content)> archives, CancellationToken ct = default) =>
        Task.FromResult<(ScimSourceEvidence?, string?)>((null, "SCIM source analysis is not available."));
    /// <summary>"Run safe SCIM checks": GET-only runtime checks plus source/configuration evidence. Never mutates or lists users.</summary>
    Task<(ScimEvidenceCheck? Check, string? Error)> RunScimChecksAsync(FrontendAnalysisProfile profile, string platformId, CancellationToken ct = default) =>
        Task.FromResult<(ScimEvidenceCheck?, string?)>((null, "SCIM checks are not available."));
    Task<ScimEvidenceCheck?> ScimCheckAsync(Guid runId, CancellationToken ct = default) => Task.FromResult<ScimEvidenceCheck?>(null);
}

public sealed class IntegrationCatalogApiService(HttpClient http) : IIntegrationCatalogApiService
{
    public async Task<IntegrationMappingEvidenceCheck> CheckMappingAsync(string environmentId, string integrationId, CancellationToken ct = default) =>
        await Read<IntegrationMappingEvidenceCheck>(await http.PostAsync($"api/integrations/{Uri.EscapeDataString(integrationId)}/mapping-evidence?{Env(environmentId)}", null, ct), ct);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<ApplicationMessagingEvidenceSet?> ApplicationMessagingAsync(string environmentId, CancellationToken ct = default)
    {
        using var response = await http.GetAsync($"api/integrations/application-messaging?{Env(environmentId)}", ct);
        response.EnsureSuccessStatusCode();
        return response.StatusCode == System.Net.HttpStatusCode.NoContent ? null : await response.Content.ReadFromJsonAsync<ApplicationMessagingEvidenceSet>(Json, ct);
    }

    public async Task<(ApplicationMessagingEvidenceSet? Set, string? Error)> AnalyzeApplicationMessagingAsync(string environmentId, IReadOnlyList<(string FileName, Stream Content)> archives, CancellationToken ct = default)
    {
        using var form = new MultipartFormDataContent();
        foreach (var (name, content) in archives)
        {
            var part = new StreamContent(content);
            part.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/zip");
            form.Add(part, "archives", name);
        }
        using var response = await http.PostAsync($"api/integrations/application-messaging?{Env(environmentId)}", form, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.BadRequest) return (null, (await response.Content.ReadAsStringAsync(ct)).Trim('"'));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ApplicationMessagingEvidenceSet>(Json, ct), null);
    }

    public async Task<ScimEvidenceOverview> ScimOverviewAsync(string environmentId, CancellationToken ct = default) =>
        await http.GetFromJsonAsync<ScimEvidenceOverview>($"api/integrations/scim?{Env(environmentId)}", Json, ct) ?? new ScimEvidenceOverview();

    public async Task<(ScimSourceEvidence? Evidence, string? Error)> AnalyzeScimSourceAsync(string environmentId, IReadOnlyList<(string FileName, Stream Content)> archives, CancellationToken ct = default)
    {
        using var form = new MultipartFormDataContent();
        foreach (var (name, content) in archives)
        {
            var part = new StreamContent(content);
            part.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/zip");
            form.Add(part, "archives", name);
        }
        using var response = await http.PostAsync($"api/integrations/scim/source?{Env(environmentId)}", form, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.BadRequest) return (null, (await response.Content.ReadAsStringAsync(ct)).Trim('"'));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ScimSourceEvidence>(Json, ct), null);
    }

    public async Task<(ScimEvidenceCheck? Check, string? Error)> RunScimChecksAsync(FrontendAnalysisProfile profile, string platformId, CancellationToken ct = default)
    {
        using var response = await http.PostAsync($"api/integrations/scim/{Uri.EscapeDataString(platformId)}/checks?{Scope(profile)}", null, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.BadRequest) return (null, (await response.Content.ReadAsStringAsync(ct)).Trim('"'));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ScimEvidenceCheck>(Json, ct), null);
    }

    public async Task<ScimEvidenceCheck?> ScimCheckAsync(Guid runId, CancellationToken ct = default)
    {
        using var response = await http.GetAsync($"api/integrations/scim/checks/{runId}", ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ScimEvidenceCheck>(Json, ct);
    }

    public async Task<ServiceBusEvidenceCheck> CheckServiceBusAsync(string environmentId, string platformId, CancellationToken ct = default) =>
        await Read<ServiceBusEvidenceCheck>(await http.PostAsync($"api/integrations/platforms/{Uri.EscapeDataString(platformId)}/servicebus-evidence?{Env(environmentId)}", null, ct), ct);

    public async Task<ApplicationMessagingEvidenceSet> BindApplicationMessagingAsync(string environmentId, string applicationId, string? consumer, CancellationToken ct = default) =>
        await Read<ApplicationMessagingEvidenceSet>(await http.PutAsJsonAsync($"api/integrations/application-messaging/{Uri.EscapeDataString(applicationId)}/binding?{Env(environmentId)}", new { consumer }, Json, ct), ct);

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

    public async Task<IReadOnlyList<IntegrationContractArtifact>> ContractsAsync(string environmentId, CancellationToken ct = default) =>
        await http.GetFromJsonAsync<List<IntegrationContractArtifact>>($"api/integrations/contracts?{Env(environmentId)}", Json, ct) ?? [];

    public async Task<(IntegrationContractArtifact? Artifact, string? Error)> SaveContractAsync(IntegrationContractUpload upload, CancellationToken ct = default)
    {
        using var response = await http.PutAsJsonAsync("api/integrations/contracts", upload, Json, ct);
        if (response.IsSuccessStatusCode) return (await response.Content.ReadFromJsonAsync<IntegrationContractArtifact>(Json, ct), null);
        if ((int)response.StatusCode == 400)
        {
            var problem = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(Json, ct);
            return (null, problem.TryGetProperty("message", out var message) ? message.GetString() : "The contract was rejected.");
        }
        return (null, $"The contract could not be saved (HTTP {(int)response.StatusCode}).");
    }

    public async Task RemoveContractAsync(string environmentId, string integrationId, IntegrationContractRole role, CancellationToken ct = default)
    {
        using var response = await http.DeleteAsync($"api/integrations/contracts?{Env(environmentId)}&integrationId={Uri.EscapeDataString(integrationId)}&role={role}", ct);
        if (!response.IsSuccessStatusCode && (int)response.StatusCode != 404) response.EnsureSuccessStatusCode();
    }

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
