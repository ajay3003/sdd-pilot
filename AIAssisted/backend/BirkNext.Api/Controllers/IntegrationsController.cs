using BirkNext.Api.Services.Integrations;
using BirkNext.Api.Services.IntegrationQuality;
using BirkNext.Integrations;
using BirkNext.Api.Services.ActiveEventTesting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

/// <summary>
/// Target Environment → Integrations: the configured, expected integrations of one environment. Configuration only — no
/// secret is accepted or returned (authentication is a mechanism name). <c>environmentType</c> and
/// <c>targetUrl</c> only decide whether the M2LB DEV template is SUGGESTED; it is applied solely through
/// <c>POST templates/{templateId}/apply</c> (an explicit action), so a generic project never receives M2LB records.
/// Reads stay open (configuration metadata only, never a secret); every write requires the IntegrationConfigurationWrite policy, because
/// Active Event execution resolves its destination from this catalog.
/// </summary>
[ApiController]
[Route("api/integrations")]
public sealed class IntegrationsController(IIntegrationCatalogService catalog) : ControllerBase
{
    [HttpPost("{id}/mapping-evidence")]
    public async Task<ActionResult<IntegrationMappingEvidenceCheck>> CheckMapping([FromQuery] string environmentId, string id,
        [FromServices] IntegrationMappingEvidenceService evidence, [FromServices] IIntegrationContractStore contracts, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(environmentId)) return BadRequest("environmentId is required.");
        // No environment type/URL: GetAsync must never attach seed records during a read-only check.
        var configured = await catalog.GetAsync(environmentId, null, null, ct);
        var definition = configured.Integrations.FirstOrDefault(i => i.Id == id);
        if (definition is null) return NotFound();
        return Ok(await evidence.CheckAsync(definition, configured.Platforms.FirstOrDefault(p => p.Id == definition.PlatformId),
            new IntegrationContractSet(await contracts.LoadAsync(environmentId, ct)), ct));
    }

    /// <summary>Read-only "Test Service Bus": configured topology, code-route consistency and — when configured — runtime metadata GETs.</summary>
    [HttpPost("platforms/{id}/servicebus-evidence")]
    public async Task<ActionResult<ServiceBusEvidenceCheck>> CheckServiceBus([FromQuery] string environmentId, string id,
        [FromServices] BirkNext.Api.Services.Integrations.ServiceBus.ServiceBusEvidenceService serviceBus,
        [FromServices] BirkNext.Api.Services.Integrations.ApplicationMessaging.IApplicationMessagingStore messaging, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(environmentId)) return BadRequest("environmentId is required.");
        // No environment type/URL: GetAsync must never attach seed records during a read-only check.
        var platform = (await catalog.GetAsync(environmentId, null, null, ct)).Platforms.FirstOrDefault(p => p.Id == id);
        if (platform is null) return NotFound();
        if (!BirkNext.Api.Services.Integrations.ServiceBus.ServiceBusEvidenceService.IsServiceBus(platform)) return BadRequest("Not a Service Bus platform.");
        return Ok(await serviceBus.CheckAsync(platform, await messaging.GetAsync(environmentId, ct), ct));
    }

    [HttpGet]
    public async Task<ActionResult<IntegrationCatalog>> Get([FromQuery] string environmentId, [FromQuery] string? environmentType, [FromQuery] string? targetUrl,
        [FromServices] IIntegrationAzureCredential azure, CancellationToken ct) =>
        // Configured sources and Azure execution are separate facts: the page shows both (IntegrationReview:Azure:Enabled).
        string.IsNullOrWhiteSpace(environmentId) ? BadRequest("environmentId is required.")
            : Ok(await catalog.GetAsync(environmentId, environmentType, targetUrl, ct) with { AzureRuntimeEnabled = azure.Credential is not null });

    /// <summary>
    /// Configured environment values compared with the Terraform in Source Analysis (read-only). No environment type/URL: a read never attaches
    /// seed records. Without infrastructure evidence the response says so, and seeded values stay assumptions.
    /// </summary>
    [HttpGet("terraform-comparison")]
    public async Task<ActionResult<IntegrationTerraformComparison>> TerraformComparison([FromQuery] string environmentId, [FromQuery] string? environmentName,
        [FromServices] BirkNext.Api.Services.SourceAnalysis.IReviewSourceEvidenceProvider sources, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(environmentId)) return BadRequest("environmentId is required.");
        var configured = await catalog.GetAsync(environmentId, null, null, ct);
        var snapshots = sources.SourceAnalysisEnabled ? await sources.ListAsync(environmentId, ct) : [];
        return Ok(IntegrationTerraformComparisons.Compare(configured, snapshots, environmentName, sources.SourceAnalysisEnabled));
    }

    /// <summary>
    /// Explicitly applies a project integration template (add-missing only). The only path that writes template records. The environment's
    /// type and target URL decide whether the template fits; a backend-owned trusted environment record wins over what the request says, and
    /// a template for another environment (QA, Production, another host) is refused with 409 — nothing is written.
    /// </summary>
    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    [HttpPost("templates/{templateId}/apply")]
    public async Task<ActionResult<IntegrationCatalog>> ApplyTemplate([FromQuery] string environmentId, string templateId, [FromQuery] string? environmentType, [FromQuery] string? targetUrl,
        [FromServices] IIntegrationAzureCredential azure, [FromServices] BirkNext.Api.Services.ActiveEventTesting.ITrustedExecutionEnvironmentRegistry trusted, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(environmentId)) return BadRequest("environmentId is required.");
        if (trusted.Resolve(environmentId, out _) is { } record) (environmentType, targetUrl) = (record.EnvironmentType, record.TargetUrl ?? targetUrl);
        var result = await catalog.ApplyTemplateAsync(environmentId, templateId, environmentType, targetUrl, ct);
        if (result is null) return NotFound();
        if (result.Blocked is { } blocked) return Conflict(new { message = blocked });
        return Ok(result.Catalog! with { AzureRuntimeEnabled = azure.Credential is not null });
    }

    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    [HttpPost]
    public async Task<ActionResult<IntegrationDefinition>> Create([FromQuery] string environmentId, [FromBody] IntegrationDefinition definition, CancellationToken ct)
    {
        try { return Ok(await catalog.CreateAsync(environmentId, definition, ct)); }
        catch (InvalidOperationException ex) { return Conflict(ex.Message); }
    }

    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    [HttpPut("{id}")]
    public async Task<ActionResult<IntegrationDefinition>> Update([FromQuery] string environmentId, string id, [FromBody] IntegrationDefinition definition, CancellationToken ct) =>
        await catalog.UpdateAsync(environmentId, id, definition, ct) is { } updated ? Ok(updated) : NotFound();

    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    [HttpPost("{id}/enabled")]
    public async Task<ActionResult<IntegrationDefinition>> SetEnabled([FromQuery] string environmentId, string id, [FromQuery] bool enabled, CancellationToken ct) =>
        await catalog.SetEnabledAsync(environmentId, id, enabled, ct) is { } updated ? Ok(updated) : NotFound();

    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete([FromQuery] string environmentId, string id, CancellationToken ct) =>
        await catalog.DeleteAsync(environmentId, id, ct) ? NoContent() : NotFound();

    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    [HttpPut("platforms/{id}")]
    public async Task<ActionResult<IntegrationPlatform>> UpdatePlatform([FromQuery] string environmentId, string id, [FromBody] IntegrationPlatform platform, CancellationToken ct)
    {
        // Runtime evidence settings are identifiers only; a SAS URL or a malformed id is rejected before it can be stored.
        if (platform.RuntimeEvidence?.Validate() is { } invalid) return BadRequest(new { message = invalid });
        if (platform.ScimProvisioning?.Validate() is { } invalidScim) return BadRequest(new { message = invalidScim });
        return await catalog.UpdatePlatformAsync(environmentId, id, platform, ct) is { } updated ? Ok(updated) : NotFound();
    }

    // Trusted contracts (JSON Schema or XSD) per integration and role. Metadata only is returned — never the schema text.
    [HttpGet("contracts")]
    public async Task<ActionResult<IReadOnlyList<IntegrationContractArtifact>>> Contracts([FromQuery] string environmentId, [FromServices] IIntegrationContractStore store, CancellationToken ct) =>
        Ok(await store.ListAsync(environmentId, ct));

    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    [HttpPut("contracts")]
    [RequestSizeLimit(JsonSchemaContract.MaxBytes * 2 + 64 * 1024)]
    public async Task<ActionResult<IntegrationContractArtifact>> SaveContract([FromBody] IntegrationContractUpload upload, [FromServices] IIntegrationContractStore store, CancellationToken ct)
    {
        var (artifact, error) = await store.SaveAsync(upload, ct);
        return artifact is null ? BadRequest(new { message = error }) : Ok(artifact);
    }

    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    [HttpDelete("contracts")]
    public async Task<IActionResult> DeleteContract([FromQuery] string environmentId, [FromQuery] string integrationId, [FromQuery] IntegrationContractRole role, [FromServices] IIntegrationContractStore store, CancellationToken ct) =>
        await store.DeleteAsync(environmentId, integrationId, role, ct) ? NoContent() : NotFound();

    /// <summary>Producer/consumer contract discovery from the integration's canonical source binding (read-only; nothing is selected or bound).</summary>
    [HttpGet("{id}/contract-discovery")]
    public async Task<ActionResult<IntegrationContractDiscovery>> ContractDiscovery(string id, [FromQuery] string environmentId,
        [FromServices] IIntegrationContractStore contracts, [FromServices] BirkNext.Api.Services.Integrations.SourceEvidence.IIntegrationEvidenceBindingStore bindings,
        [FromServices] BirkNext.Api.Services.Integrations.SourceEvidence.IqrSourceStore sources, [FromServices] BirkNext.Api.Services.IntegrationJourneys.IIntegrationJourneyService journeys,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(environmentId)) return BadRequest("environmentId is required.");
        var discovery = await new IntegrationContractDiscoveryService(catalog, bindings, sources, contracts, journeys).DiscoverAsync(environmentId, id, ct);
        return discovery is null ? NotFound() : Ok(discovery);
    }

    /// <summary>Uses one discovered source contract for one side (explicit, per side). Only a candidate of the current binding with retained content.</summary>
    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    [HttpPut("contracts/source-selection")]
    public async Task<ActionResult<IntegrationContractArtifact>> SelectSourceContract([FromBody] IntegrationContractSourceSelectionRequest request,
        [FromServices] IIntegrationContractStore contracts, [FromServices] BirkNext.Api.Services.Integrations.SourceEvidence.IIntegrationEvidenceBindingStore bindings,
        [FromServices] BirkNext.Api.Services.Integrations.SourceEvidence.IqrSourceStore sources, [FromServices] BirkNext.Api.Services.IntegrationJourneys.IIntegrationJourneyService journeys,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.EnvironmentId)) return BadRequest(new { message = "environmentId is required." });
        var (artifact, error) = await new IntegrationContractDiscoveryService(catalog, bindings, sources, contracts, journeys).SelectAsync(request, ct);
        return artifact is null ? BadRequest(new { message = error }) : Ok(artifact);
    }

    /// <summary>One-time import of integrations that were stored in the browser Target Environment profile.</summary>
    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    [HttpPost("import-legacy")]
    public async Task<ActionResult<int>> ImportLegacy([FromQuery] string environmentId, [FromBody] List<IntegrationConfigDto> legacy, CancellationToken ct) =>
        Ok(await catalog.ImportLegacyAsync(environmentId, legacy, ct));
}

/// <summary>Integration Quality Review over the configured catalog. Read-only: no event is published or consumed, no checkpoint moved.</summary>
[ApiController]
[Route("api/integration-review")]
public sealed class IntegrationReviewController(IIntegrationReviewService review) : ControllerBase
{
    [HttpGet("readiness")]
    public async Task<ActionResult<IntegrationReviewReadiness>> Readiness([FromQuery] string environmentId, [FromQuery] string? environmentType, [FromQuery] string? targetUrl, CancellationToken ct) =>
        Ok(await review.ReadinessAsync(environmentId, environmentType, targetUrl, ct));

    [HttpPost("run")]
    public async Task<ActionResult<IntegrationReviewResult>> Run([FromBody] IntegrationReviewRunRequest request, [FromQuery] string? environmentType, [FromQuery] string? targetUrl, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.EnvironmentId)) return BadRequest("environmentId is required.");
        try { return Ok(await review.RunAsync(request, environmentType, targetUrl, ct)); }
        catch (BirkNext.Api.Services.Integrations.SourceEvidence.InvalidSourceSelectionException ex) { return BadRequest(new { message = ex.Message }); }
    }

    [HttpGet("runs")]
    public async Task<ActionResult<IReadOnlyList<IntegrationReviewRunSummary>>> History([FromQuery] string environmentId, CancellationToken ct) =>
        Ok(await review.HistoryAsync(environmentId, ct));

    [HttpGet("runs/{runId:guid}")]
    public async Task<ActionResult<IntegrationReviewResult>> Run(Guid runId, CancellationToken ct) =>
        await review.GetRunAsync(runId, ct) is { } result ? Ok(result) : NotFound();
}
