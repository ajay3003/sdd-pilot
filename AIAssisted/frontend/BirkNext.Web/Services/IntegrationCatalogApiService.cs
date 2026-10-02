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
    Task<IReadOnlyList<IqrSourceSnapshot>> ListSourceSnapshotsAsync(string environmentId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<IqrSourceSnapshot>>([]);
    /// <summary>Read-only source suggestions for one configured field (Source Analysis Infrastructure evidence). Null when unavailable.</summary>
    Task<BirkNext.SourceDomains.SourceInfrastructureSuggestion?> InfrastructureSuggestionAsync(string environmentId, BirkNext.SourceDomains.InfrastructureResourceKind kind, string field, string? configured, string? targetEnvironment, string? parent, CancellationToken ct = default) => Task.FromResult<BirkNext.SourceDomains.SourceInfrastructureSuggestion?>(null);
    Task<(IqrSourceSnapshot? Snapshot, string? Error)> AnalyzeSourceSnapshotAsync(string environmentId, string fileName, Stream content, CancellationToken ct = default) => Task.FromResult<(IqrSourceSnapshot?, string?)>((null, "Source analysis is unavailable."));
    /// <summary>Source Analysis snapshots for binding one to an integration (read-only metadata; no upload).</summary>
    Task<ReviewSourceOptions> IqrSourceScopeAsync(string environmentId, Guid? primary, CancellationToken ct = default) => Task.FromResult(new ReviewSourceOptions());
    Task<IntegrationReviewResult> RunWithSourceAsync(FrontendAnalysisProfile profile, IReadOnlyList<IqrSourceSelection> selections, CancellationToken ct = default) =>
        selections.Count == 0 ? RunAsync(profile, ct) : throw new InvalidOperationException("Source snapshot selection is unavailable.");
    Task<IntegrationMappingEvidenceCheck> CheckMappingAsync(string environmentId, string integrationId, CancellationToken ct = default);
    /// <summary>The environment's application-messaging (Wolverine) evidence, or null when no source was analyzed.</summary>
    Task<ApplicationMessagingEvidenceSet?> ApplicationMessagingAsync(string environmentId, CancellationToken ct = default);
    /// <summary>Uploads source archives for read-only analysis; returns the new evidence or the reason nothing was stored.</summary>
    /// <summary>Source Analysis snapshots for application messaging (read-only metadata).</summary>
    Task<ReviewSourceOptions> ApplicationMessagingSourceScopeAsync(string environmentId, ReviewSourceScopeRequest? scope, CancellationToken ct = default) => Task.FromResult(new ReviewSourceOptions());
    /// <summary>Builds the application-messaging evidence from exactly these Source Analysis snapshots (no upload).</summary>
    Task<(ApplicationMessagingEvidenceSet? Set, string? Error)> UseApplicationMessagingSourceAsync(string environmentId, ReviewSourceScopeRequest scope, CancellationToken ct = default) =>
        Task.FromResult<(ApplicationMessagingEvidenceSet?, string?)>((null, "Application messaging is unavailable."));
    Task<ApplicationMessagingEvidenceSet> BindApplicationMessagingAsync(string environmentId, string applicationId, string? consumer, CancellationToken ct = default);
    /// <summary>Read-only "Test Service Bus" of one Service Bus platform (topology, code routes and — when configured — runtime metadata).</summary>
    Task<ServiceBusEvidenceCheck> CheckServiceBusAsync(string environmentId, string platformId, CancellationToken ct = default);
    Task<IntegrationCatalog> GetCatalogAsync(FrontendAnalysisProfile profile, CancellationToken ct = default);
    /// <summary>Explicitly applies a project integration template (add-missing only). The only way template records reach an environment.</summary>
    Task<IntegrationCatalog> ApplyTemplateAsync(string environmentId, string templateId, CancellationToken ct = default) =>
        Task.FromException<IntegrationCatalog>(new NotSupportedException("Integration templates are unavailable."));
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
    Task<IntegrationMessageFlowPackage> GetMessageFlowReviewAsync(string environmentId, CancellationToken ct = default) => Task.FromResult(new IntegrationMessageFlowPackage(
        new MessageFlowDefinition { EnvironmentId = environmentId, Name = "Message flow review" }, new AltinnTestConfiguration(), new(false, 0, 0, [], []), [], []));
    Task<IntegrationMessageFlowPackage> SaveMessageFlowReviewAsync(string environmentId, IntegrationMessageFlowPackage package, CancellationToken ct = default) => Task.FromException<IntegrationMessageFlowPackage>(new NotSupportedException("Message flow configuration is unavailable."));
    Task<MessageFlowSourceContractOptions?> MessageFlowSourceContractsAsync(string environmentId, Guid snapshotId, CancellationToken ct = default) => Task.FromResult<MessageFlowSourceContractOptions?>(null);

    // SCIM identity provisioning. Default members keep other implementations (test fakes) valid; the backend client overrides them.
    /// <summary>The environment's latest SCIM source analysis and stored safe-check history.</summary>
    Task<ScimEvidenceOverview> ScimOverviewAsync(string environmentId, CancellationToken ct = default) => Task.FromResult(new ScimEvidenceOverview());
    /// <summary>Source integrations: the latest source snapshot's discovery reconciled with the catalog. Source-only; never writes or confirms.</summary>
    Task<SourceIntegrationsReport?> SourceIntegrationsAsync(string environmentId, CancellationToken ct = default) => Task.FromResult<SourceIntegrationsReport?>(null);
    /// <summary>"Discover from source": re-runs discovery on the stored source snapshot (no runtime call, no archive re-processing).</summary>
    Task<SourceIntegrationsReport?> DiscoverSourceIntegrationsAsync(string environmentId, CancellationToken ct = default) => Task.FromResult<SourceIntegrationsReport?>(null);
    /// <summary>Uploads repository archives for read-only SCIM source analysis.</summary>
    /// <summary>Source Analysis snapshots for SCIM provisioning (read-only metadata).</summary>
    Task<ReviewSourceOptions> ScimSourceScopeAsync(string environmentId, ReviewSourceScopeRequest? scope, CancellationToken ct = default) => Task.FromResult(new ReviewSourceOptions());
    /// <summary>Records the SCIM source evidence of exactly this Source Analysis snapshot (no upload).</summary>
    Task<(ScimSourceEvidence? Evidence, string? Error)> UseScimSourceAsync(string environmentId, ReviewSourceScopeRequest scope, CancellationToken ct = default) =>
        Task.FromResult<(ScimSourceEvidence?, string?)>((null, "SCIM evidence is unavailable."));
    /// <summary>"Run safe SCIM checks": GET-only runtime checks plus source/configuration evidence. Never mutates or lists users.</summary>
    Task<(ScimEvidenceCheck? Check, string? Error)> RunScimChecksAsync(FrontendAnalysisProfile profile, string platformId, CancellationToken ct = default) =>
        Task.FromResult<(ScimEvidenceCheck?, string?)>((null, "SCIM checks are not available."));
    Task<ScimEvidenceCheck?> ScimCheckAsync(Guid runId, CancellationToken ct = default) => Task.FromResult<ScimEvidenceCheck?>(null);
}

public sealed class IntegrationCatalogApiService(HttpClient http) : IIntegrationCatalogApiService
{
    public async Task<BirkNext.SourceDomains.SourceInfrastructureSuggestion?> InfrastructureSuggestionAsync(string environmentId, BirkNext.SourceDomains.InfrastructureResourceKind kind, string field, string? configured, string? targetEnvironment, string? parent, CancellationToken ct = default)
    {
        static string Q(string name, string? value) => value is null ? "" : $"&{name}={Uri.EscapeDataString(value)}";
        try { return await http.GetFromJsonAsync<BirkNext.SourceDomains.SourceInfrastructureSuggestion>($"api/source-analysis/infrastructure-suggestions?{Env(environmentId)}&kind={kind}{Q("field", field)}{Q("targetEnvironment", targetEnvironment)}{Q("parent", parent)}", Json, ct); }
        catch (HttpRequestException) { return null; }
    }
    public async Task<IReadOnlyList<IqrSourceSnapshot>> ListSourceSnapshotsAsync(string environmentId, CancellationToken ct = default) => await http.GetFromJsonAsync<List<IqrSourceSnapshot>>($"api/source-analysis?{Env(environmentId)}", Json, ct) ?? [];
    public Task<(IqrSourceSnapshot? Snapshot, string? Error)> AnalyzeSourceSnapshotAsync(string environmentId, string fileName, Stream content, CancellationToken ct = default) => UploadSource($"api/source-analysis/snapshots?{Env(environmentId)}", fileName, content, ct);
    public async Task<ReviewSourceOptions> IqrSourceScopeAsync(string environmentId, Guid? primary, CancellationToken ct = default) =>
        await http.GetFromJsonAsync<ReviewSourceOptions>($"api/integration-review/source/scope?{Env(environmentId)}{(primary is { } id ? $"&primary={id}" : "")}", Json, ct) ?? new();
    private async Task<(IqrSourceSnapshot? Snapshot, string? Error)> UploadSource(string route, string fileName, Stream content, CancellationToken ct)
    {
        using var body = new MultipartFormDataContent();
        body.Add(new StreamContent(content), "file", fileName);
        using var response = await http.PostAsync(route, body, ct);
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode == System.Net.HttpStatusCode.BadRequest)
            {
                var text = await response.Content.ReadAsStringAsync(ct);
                try { using var json = JsonDocument.Parse(text); return (null, json.RootElement.TryGetProperty("message", out var message) ? message.GetString() : "Source archive validation failed."); }
                catch (JsonException) { return (null, "Source archive validation failed. Check ZIP validity, safe paths and the 50 MB upload / 100 MB expanded limits."); }
            }
            return (null, "Source archive could not be analyzed. No new evidence was selected.");
        }
        return (await response.Content.ReadFromJsonAsync<IqrSourceSnapshot>(Json, ct), null);
    }
    public async Task<IntegrationReviewResult> RunWithSourceAsync(FrontendAnalysisProfile profile, IReadOnlyList<IqrSourceSelection> selections, CancellationToken ct = default)
    {
        var request = new IntegrationReviewRunRequest { EnvironmentId = profile.Id, EnvironmentName = profile.Name, SourceSelections = selections.ToList() };
        var scope = $"environmentType={Uri.EscapeDataString(profile.EnvironmentType.ToString())}&targetUrl={Uri.EscapeDataString(profile.TargetUrl ?? "")}";
        return await Read<IntegrationReviewResult>(await http.PostAsJsonAsync($"api/integration-review/run?{scope}", request, Json, ct), ct);
    }
    public async Task<IntegrationMappingEvidenceCheck> CheckMappingAsync(string environmentId, string integrationId, CancellationToken ct = default) =>
        await Read<IntegrationMappingEvidenceCheck>(await http.PostAsync($"api/integrations/{Uri.EscapeDataString(integrationId)}/mapping-evidence?{Env(environmentId)}", null, ct), ct);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<ApplicationMessagingEvidenceSet?> ApplicationMessagingAsync(string environmentId, CancellationToken ct = default)
    {
        using var response = await http.GetAsync($"api/integrations/application-messaging?{Env(environmentId)}", ct);
        response.EnsureSuccessStatusCode();
        return response.StatusCode == System.Net.HttpStatusCode.NoContent ? null : await response.Content.ReadFromJsonAsync<ApplicationMessagingEvidenceSet>(Json, ct);
    }

    public async Task<ReviewSourceOptions> ApplicationMessagingSourceScopeAsync(string environmentId, ReviewSourceScopeRequest? scope, CancellationToken ct = default) =>
        await http.GetFromJsonAsync<ReviewSourceOptions>($"api/integrations/application-messaging/source-scope?{Env(environmentId)}{ReviewSourceQuery.Of(scope)}", Json, ct) ?? new();

    public async Task<(ApplicationMessagingEvidenceSet? Set, string? Error)> UseApplicationMessagingSourceAsync(string environmentId, ReviewSourceScopeRequest scope, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync($"api/integrations/application-messaging/source-scope?{Env(environmentId)}", scope, Json, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.BadRequest) return (null, (await response.Content.ReadAsStringAsync(ct)).Trim('"'));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ApplicationMessagingEvidenceSet>(Json, ct), null);
    }

    public async Task<SourceIntegrationsReport?> SourceIntegrationsAsync(string environmentId, CancellationToken ct = default) =>
        await http.GetFromJsonAsync<SourceIntegrationsReport>($"api/integrations/source-integrations?{Env(environmentId)}", Json, ct);

    public async Task<SourceIntegrationsReport?> DiscoverSourceIntegrationsAsync(string environmentId, CancellationToken ct = default)
    {
        using var response = await http.PostAsync($"api/integrations/source-integrations/discover?{Env(environmentId)}", null, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<SourceIntegrationsReport>(Json, ct);
    }

    public async Task<ScimEvidenceOverview> ScimOverviewAsync(string environmentId, CancellationToken ct = default) =>
        await http.GetFromJsonAsync<ScimEvidenceOverview>($"api/integrations/scim?{Env(environmentId)}", Json, ct) ?? new ScimEvidenceOverview();

    public async Task<ReviewSourceOptions> ScimSourceScopeAsync(string environmentId, ReviewSourceScopeRequest? scope, CancellationToken ct = default) =>
        await http.GetFromJsonAsync<ReviewSourceOptions>($"api/integrations/scim/source-scope?{Env(environmentId)}{ReviewSourceQuery.Of(scope)}", Json, ct) ?? new();

    public async Task<(ScimSourceEvidence? Evidence, string? Error)> UseScimSourceAsync(string environmentId, ReviewSourceScopeRequest scope, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync($"api/integrations/scim/source-scope?{Env(environmentId)}", scope, Json, ct);
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

    public async Task<IntegrationCatalog> ApplyTemplateAsync(string environmentId, string templateId, CancellationToken ct = default) =>
        await Read<IntegrationCatalog>(await http.PostAsync($"api/integrations/templates/{Uri.EscapeDataString(templateId)}/apply?{Env(environmentId)}", null, ct), ct);

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

    public async Task<IntegrationMessageFlowPackage> GetMessageFlowReviewAsync(string environmentId, CancellationToken ct = default) =>
        await http.GetFromJsonAsync<IntegrationMessageFlowPackage>($"api/integration-message-flow/{Uri.EscapeDataString(environmentId)}", Json, ct)
        ?? new IntegrationMessageFlowPackage(new MessageFlowDefinition { EnvironmentId = environmentId, Name = "Message flow review" }, new(), new(false, 0, 0, [], []), [], []);

    public async Task<IntegrationMessageFlowPackage> SaveMessageFlowReviewAsync(string environmentId, IntegrationMessageFlowPackage package, CancellationToken ct = default) =>
        await Read<IntegrationMessageFlowPackage>(await http.PutAsJsonAsync($"api/integration-message-flow/{Uri.EscapeDataString(environmentId)}", package, Json, ct), ct);

    public async Task<MessageFlowSourceContractOptions?> MessageFlowSourceContractsAsync(string environmentId, Guid snapshotId, CancellationToken ct = default)
    {
        using var response = await http.GetAsync($"api/integration-message-flow/{Uri.EscapeDataString(environmentId)}/source-contracts/{snapshotId}", ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<MessageFlowSourceContractOptions>(Json, ct);
    }

    private static async Task<T> Read<T>(HttpResponseMessage response, CancellationToken ct)
    {
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>(Json, ct))!;
    }
}
