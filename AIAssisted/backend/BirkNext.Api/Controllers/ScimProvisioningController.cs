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

    [HttpPost("source")]
    [RequestSizeLimit(4 * ScimSourceReader.MaxArchiveBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = 4 * ScimSourceReader.MaxArchiveBytes)]
    public async Task<ActionResult<ScimSourceEvidence>> Analyze([FromQuery] string environmentId, CancellationToken ct)
    {
        if (!Request.HasFormContentType) return BadRequest("Upload source archives as multipart form data.");
        var form = await Request.ReadFormAsync(ct);
        if (form.Files.Count is 0 or > 4) return BadRequest("Upload one to four source archives (.zip).");
        var archives = new List<(string, byte[])>();
        foreach (var file in form.Files)
        {
            if (file.Length > ScimSourceReader.MaxArchiveBytes) return BadRequest($"{file.FileName} is larger than the upload limit.");
            using var buffer = new MemoryStream();
            await file.CopyToAsync(buffer, ct);
            archives.Add((file.FileName, buffer.ToArray()));
        }
        var (evidence, error) = await scim.AnalyzeAsync(environmentId, archives, ct);
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
