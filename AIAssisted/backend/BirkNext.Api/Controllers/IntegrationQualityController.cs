using BirkNext.Api.Services.IntegrationQuality;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

[ApiController]
[Route("api/integration-quality")]
public class IntegrationQualityController : ControllerBase
{
    private readonly IIntegrationQualityReviewService _service;
    private readonly ILogger<IntegrationQualityController> _logger;

    public IntegrationQualityController(
        IIntegrationQualityReviewService service,
        ILogger<IntegrationQualityController> logger)
    {
        _service = service;
        _logger  = logger;
    }

    [HttpPost("analyze")]
    public async Task<IActionResult> Analyze([FromBody] IntegrationQualityRequest request, CancellationToken ct)
    {
        if (!request.Integrations.Any())
            return BadRequest(new { message = "No integrations are configured in the active Target Environment. Add integrations under System Settings → Target Environments → Integrations." });

        var correlationId = HttpContext.Request.Headers["X-Correlation-Id"].FirstOrDefault() ?? "unknown";
        _logger.LogInformation(
            "Integration quality review requested for environment '{Name}' with {Count} integrations. CorrelationId: {CorrelationId}",
            request.EnvironmentName, request.Integrations.Count, correlationId);

        try
        {
            var report = await _service.AnalyzeAsync(request, ct);
            return Ok(report);
        }
        catch (OperationCanceledException)
        {
            return StatusCode(499);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Integration quality review failed for environment '{Name}'", request.EnvironmentName);
            return StatusCode(500, new { message = "Integration quality review failed: " + ex.Message });
        }
    }
}
