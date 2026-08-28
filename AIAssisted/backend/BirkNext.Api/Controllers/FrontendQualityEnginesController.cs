using BirkNext.Api.Services.FrontendQualityEngines;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

[ApiController]
[Route("api/frontend-quality-engines")]
public sealed class FrontendQualityEnginesController : ControllerBase
{
    private readonly IFrontendQualityEngineStatusService _statusService;
    private readonly IFrontendQualityEngineReadinessAggregator _readinessAggregator;
    private readonly ILogger<FrontendQualityEnginesController> _logger;

    public FrontendQualityEnginesController(
        IFrontendQualityEngineStatusService statusService,
        IFrontendQualityEngineReadinessAggregator readinessAggregator,
        ILogger<FrontendQualityEnginesController> logger)
    {
        _statusService = statusService;
        _readinessAggregator = readinessAggregator;
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

    [HttpGet("readiness/{engineId}")]
    [ProducesResponseType(typeof(FrontendQualityEngineReadiness), StatusCodes.Status200OK)]
    public async Task<ActionResult<FrontendQualityEngineReadiness>> GetReadiness(
        int engineId,
        CancellationToken ct)
    {
        _logger.LogInformation("Checking readiness for engine {EngineId}", engineId);

        if (!Enum.IsDefined(typeof(FrontendQualityEngineId), engineId))
        {
            _logger.LogWarning("Invalid engine ID: {EngineId}", engineId);
            return BadRequest($"Invalid engine ID: {engineId}");
        }

        var engine = (FrontendQualityEngineId)engineId;
        var readiness = await _readinessAggregator.RevalidateAsync(engine, ct);

        return Ok(readiness);
    }
}
