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

    /// <summary>
    /// The known M2LB integration templates, resolved for one environment.
    ///
    /// Every template is returned whatever the environment is — a logical integration such as
    /// "Person CDC" exists in Development, QA and Production alike. What varies is the binding:
    /// the hub name, namespace and consumer group this environment actually uses. An environment
    /// with no evidenced binding gets the template with its structural values reported as missing,
    /// never with another environment's values adapted to fit.
    ///
    /// <paramref name="environmentType"/> is the normalised type ("Development", "QA",
    /// "Production"), not a profile display name.
    /// </summary>
    [HttpGet("known-templates")]
    public IActionResult KnownTemplates([FromQuery] string? environmentType) =>
        Ok(KnownIntegrationTemplates.ForEnvironment(environmentType));

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
