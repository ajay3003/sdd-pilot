using BirkNext.Api.Services.ActiveEventTesting;
using BirkNext.Api.Services.IntegrationJourneys;
using BirkNext.Api.Services.SetupProfiles;
using BirkNext.Integrations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

/// <summary>
/// Setup profiles for a test pack: validate + preview (read-only), apply only the selected safe changes (needs the integration-configuration
/// permission, like every owner it writes to), export the safe setup, and a placeholder example. Profiles are small text documents.
/// </summary>
[ApiController]
[Route("api/setup-profiles/{packId}")]
public sealed class SetupProfilesController(SetupProfileService profiles) : ControllerBase
{
    [HttpPost("preview")]
    [RequestSizeLimit(SetupProfileParser.MaxBytes * 2)]
    public async Task<ActionResult<SetupProfilePreview>> Preview(string packId, [FromQuery] string environmentId, [FromBody] SetupProfileRequest request, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(environmentId) ? BadRequest("environmentId is required.") : Ok(await profiles.PreviewAsync(environmentId, packId, request, ct));

    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    [HttpPost("apply")]
    [RequestSizeLimit(SetupProfileParser.MaxBytes * 2)]
    public async Task<ActionResult<SetupApplyResult>> Apply(string packId, [FromQuery] string environmentId, [FromBody] SetupApplyRequest request, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(environmentId) ? BadRequest("environmentId is required.") : Ok(await profiles.ApplyAsync(environmentId, packId, request, ct));

    [HttpGet("export")]
    public async Task<ActionResult<SetupProfileExport>> Export(string packId, [FromQuery] string environmentId, [FromQuery] SetupProfileFormat format = SetupProfileFormat.Yaml,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(environmentId)) return BadRequest("environmentId is required.");
        try { return Ok(await profiles.ExportAsync(environmentId, packId, format, ct)); }
        catch (IntegrationJourneyRequestException ex) { return NotFound(ex.Message); }
    }

    [HttpGet("example")]
    public ActionResult<SetupProfileExport> Example(string packId) =>
        Ok(new SetupProfileExport(SetupProfileFormat.Yaml, $"{packId}-development.example.yaml", SetupProfileService.Example(packId), SetupProfileService.ExportExcluded));
}
