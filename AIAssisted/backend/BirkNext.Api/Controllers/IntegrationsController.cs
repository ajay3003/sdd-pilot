using BirkNext.Api.Services.Integrations;
using BirkNext.Api.Services.IntegrationQuality;
using BirkNext.Integrations;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

/// <summary>
/// Target Environment → Integrations: the configured, expected integrations of one environment. Configuration only — no
/// secret is accepted or returned (authentication is a mechanism name). <c>environmentType</c> and
/// <c>targetUrl</c> identify whether the known M2LB DEV seed applies to the environment being read.
/// </summary>
[ApiController]
[Route("api/integrations")]
public sealed class IntegrationsController(IIntegrationCatalogService catalog) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IntegrationCatalog>> Get([FromQuery] string environmentId, [FromQuery] string? environmentType, [FromQuery] string? targetUrl, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(environmentId) ? BadRequest("environmentId is required.") : Ok(await catalog.GetAsync(environmentId, environmentType, targetUrl, ct));

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
    public async Task<ActionResult<IntegrationReviewResult>> Run([FromBody] IntegrationReviewRunRequest request, [FromQuery] string? environmentType, [FromQuery] string? targetUrl, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(request.EnvironmentId) ? BadRequest("environmentId is required.") : Ok(await review.RunAsync(request, environmentType, targetUrl, ct));

    [HttpGet("runs")]
    public async Task<ActionResult<IReadOnlyList<IntegrationReviewRunSummary>>> History([FromQuery] string environmentId, CancellationToken ct) =>
        Ok(await review.HistoryAsync(environmentId, ct));

    [HttpGet("runs/{runId:guid}")]
    public async Task<ActionResult<IntegrationReviewResult>> Run(Guid runId, CancellationToken ct) =>
        await review.GetRunAsync(runId, ct) is { } result ? Ok(result) : NotFound();
}
