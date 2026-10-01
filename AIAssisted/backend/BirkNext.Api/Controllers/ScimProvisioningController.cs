using BirkNext.Api.Services.Integrations.Scim;
using BirkNext.Integrations;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

/// <summary>
/// SCIM identity provisioning evidence (Target Environment → Integrations → Identity provisioning). Source archives are analyzed in memory;
/// the safe checks send GET requests only (health, authentication challenge on a random id, metadata) and never create, change, delete or
/// list a user or publish a message. Production and unknown environment types are refused. No token or user payload is stored or returned.
/// </summary>
[ApiController]
[Route("api/integrations/scim")]
public sealed class ScimProvisioningController(IScimEvidenceService scim) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ScimEvidenceOverview>> Overview([FromQuery] string environmentId, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(environmentId) ? BadRequest("environmentId is required.") : Ok(await scim.OverviewAsync(environmentId, ct));

    // Source ingestion belongs to Source Analysis: SCIM source evidence is taken from one of its snapshots; there is no upload here.

    /// <summary>Source Analysis snapshots for SCIM provisioning (read-only).</summary>
    [HttpGet("source-scope")]
    public async Task<ActionResult<ReviewSourceOptions>> SourceScope([FromQuery] string environmentId, [FromQuery] Guid? primary, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(environmentId) ? BadRequest("environmentId is required.")
            : Ok(await scim.SourceScopeAsync(environmentId, primary is { } p ? new ReviewSourceScopeRequest { PrimarySnapshotId = p } : null, ct));

    /// <summary>Records the SCIM source evidence of exactly the chosen Source Analysis snapshot.</summary>
    [HttpPost("source-scope")]
    public async Task<ActionResult<ScimSourceEvidence>> UseSourceScope([FromQuery] string environmentId, [FromBody] ReviewSourceScopeRequest scope, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(environmentId)) return BadRequest("environmentId is required.");
        var (evidence, error) = await scim.UseSourceScopeAsync(environmentId, scope, ct);
        return error is not null ? BadRequest(error) : Ok(evidence);
    }

    [HttpPost("{platformId}/checks")]
    public async Task<ActionResult<ScimEvidenceCheck>> RunSafeChecks(string platformId, [FromQuery] string environmentId, [FromQuery] string? environmentType, [FromQuery] string? targetUrl, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(environmentId)) return BadRequest("environmentId is required.");
        var (check, error) = await scim.RunSafeChecksAsync(environmentId, platformId, environmentType, targetUrl, ct);
        return error is not null ? BadRequest(error) : Ok(check);
    }

    [HttpGet("checks/{runId:guid}")]
    public async Task<ActionResult<ScimEvidenceCheck>> GetRun(Guid runId, CancellationToken ct) =>
        await scim.GetRunAsync(runId, ct) is { } check ? Ok(check) : NotFound();
}
