using BirkNext.Api.Services.FrontendQualityEngines;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

[ApiController]
[Route("api/frontend-quality-engines")]
public sealed class FrontendQualityEnginesController : ControllerBase
{
    private readonly IFrontendQualityEngineStatusService _statusService;
    private readonly ILogger<FrontendQualityEnginesController> _logger;

    public FrontendQualityEnginesController(
        IFrontendQualityEngineStatusService statusService,
        ILogger<FrontendQualityEnginesController> logger)
    {
        _statusService = statusService;
        _logger = logger;
    }

    [HttpGet("status")]
    [ProducesResponseType(typeof(FrontendQualityEngineStatusReport), StatusCodes.Status200OK)]
    public async Task<ActionResult<FrontendQualityEngineStatusReport>> GetStatus(CancellationToken ct)
    {
        _logger.LogInformation("Retrieving frontend quality engines status");
        var report = await _statusService.GetStatusAsync(ct: ct);
        return Ok(report);
    }

    [HttpPost("status")]
    [ProducesResponseType(typeof(FrontendQualityEngineStatusReport), StatusCodes.Status200OK)]
    public async Task<ActionResult<FrontendQualityEngineStatusReport>> PostStatus(
        [FromBody] FrontendQualityEngineStatusQuery? query,
        CancellationToken ct)
    {
        _logger.LogInformation("Retrieving frontend quality engines status with query");
        var report = await _statusService.GetStatusAsync(query, ct);
        return Ok(report);
    }
}
