using BirkNext.Api.Services.Integrations;
using BirkNext.Api.Services.IntegrationQuality;
using BirkNext.Integrations;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

/// <summary>
/// Target Environment → Integrations: the configured, expected integrations of one environment. Configuration only — no
/// secret is accepted or returned (authentication is a mechanism name). <c>environmentType</c> and
/// <c>targetUrl</c> only decide whether the M2LB DEV template is SUGGESTED; it is applied solely through
/// <c>POST templates/{templateId}/apply</c> (an explicit action), so a generic project never receives M2LB records.
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

    /// <summary>Explicitly applies a project integration template (add-missing only). The only path that writes template records.</summary>
    [HttpPost("templates/{templateId}/apply")]
    public async Task<ActionResult<IntegrationCatalog>> ApplyTemplate([FromQuery] string environmentId, string templateId, [FromServices] IIntegrationAzureCredential azure, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(environmentId)) return BadRequest("environmentId is required.");
        return await catalog.ApplyTemplateAsync(environmentId, templateId, ct) is { } applied ? Ok(applied with { AzureRuntimeEnabled = azure.Credential is not null }) : NotFound();
    }

    [HttpPost]
    public async Task<ActionResult<IntegrationDefinition>> Create([FromQuery] string environmentId, [FromBody] IntegrationDefinition definition, CancellationToken ct)
    {
        try { return Ok(await catalog.CreateAsync(environmentId, definition, ct)); }
        catch (InvalidOperationException ex) { return Conflict(ex.Message); }
    }

    [HttpPut("{id}")]
    public async Task<ActionResult<IntegrationDefinition>> Update([FromQuery] string environmentId, string id, [FromBody] IntegrationDefinition definition, CancellationToken ct) =>
        await catalog.UpdateAsync(environmentId, id, definition, ct) is { } updated ? Ok(updated) : NotFound();

    [HttpPost("{id}/enabled")]
    public async Task<ActionResult<IntegrationDefinition>> SetEnabled([FromQuery] string environmentId, string id, [FromQuery] bool enabled, CancellationToken ct) =>
        await catalog.SetEnabledAsync(environmentId, id, enabled, ct) is { } updated ? Ok(updated) : NotFound();

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete([FromQuery] string environmentId, string id, CancellationToken ct) =>
        await catalog.DeleteAsync(environmentId, id, ct) ? NoContent() : NotFound();

    [HttpPut("platforms/{id}")]
    public async Task<ActionResult<IntegrationPlatform>> UpdatePlatform([FromQuery] string environmentId, string id, [FromBody] IntegrationPlatform platform, CancellationToken ct)
    {
        // Runtime evidence settings are identifiers only; a SAS URL or a malformed id is rejected before it can be stored.
        if (platform.RuntimeEvidence?.Validate() is { } invalid) return BadRequest(new { message = invalid });
        if (platform.ScimProvisioning?.Validate() is { } invalidScim) return BadRequest(new { message = invalidScim });
        return await catalog.UpdatePlatformAsync(environmentId, id, platform, ct) is { } updated ? Ok(updated) : NotFound();
    }

    // Trusted event contracts (JSON Schema) per integration and role. Metadata only is returned — never the schema text.
    [HttpGet("contracts")]
    public async Task<ActionResult<IReadOnlyList<IntegrationContractArtifact>>> Contracts([FromQuery] string environmentId, [FromServices] IIntegrationContractStore store, CancellationToken ct) =>
        Ok(await store.ListAsync(environmentId, ct));

    [HttpPut("contracts")]
    [RequestSizeLimit(JsonSchemaContract.MaxBytes * 2 + 64 * 1024)]
    public async Task<ActionResult<IntegrationContractArtifact>> SaveContract([FromBody] IntegrationContractUpload upload, [FromServices] IIntegrationContractStore store, CancellationToken ct)
    {
        var (artifact, error) = await store.SaveAsync(upload, ct);
        return artifact is null ? BadRequest(new { message = error }) : Ok(artifact);
    }

    [HttpDelete("contracts")]
    public async Task<IActionResult> DeleteContract([FromQuery] string environmentId, [FromQuery] string integrationId, [FromQuery] IntegrationContractRole role, [FromServices] IIntegrationContractStore store, CancellationToken ct) =>
        await store.DeleteAsync(environmentId, integrationId, role, ct) ? NoContent() : NotFound();

    /// <summary>One-time import of integrations that were stored in the browser Target Environment profile.</summary>
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
