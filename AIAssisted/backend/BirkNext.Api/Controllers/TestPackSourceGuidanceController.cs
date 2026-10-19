using BirkNext.Api.Services.IntegrationJourneys;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

/// <summary>
/// Test pack → "Apply safe source-derived configuration": stores bound, unambiguous, non-secret source values into EMPTY configuration fields.
/// It is configuration, not execution, so it needs the integration-configuration write permission only (never the active-test permission).
/// </summary>
[ApiController]
[Route("api/integration-journeys/packs/{packId}/source-guidance")]
public sealed class TestPackSourceGuidanceController(IIntegrationJourneyService journeys) : ControllerBase
{
    [Authorize(Policy = BirkNext.Api.Services.ActiveEventTesting.BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    [HttpPost("apply")]
    public async Task<ActionResult<BirkNext.SourceDomains.SourceConfigurationApplyResult>> Apply(string packId, [FromQuery] string environmentId, CancellationToken ct)
    {
        try { return Ok(await journeys.ApplySourceConfigurationAsync(environmentId ?? "", packId, ct)); }
        catch (IntegrationJourneyRequestException ex) { return BadRequest(new { message = ex.Message }); }
    }
}
