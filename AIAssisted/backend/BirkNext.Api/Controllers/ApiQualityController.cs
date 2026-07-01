using BirkNext.Api.Services.ApiQuality;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

[ApiController]
[Route("api/api-quality")]
public class ApiQualityController : ControllerBase
{
    private readonly IApiQualityReviewService _service;
    private readonly ILogger<ApiQualityController> _logger;

    public ApiQualityController(
        IApiQualityReviewService service,
        ILogger<ApiQualityController> logger)
    {
        _service = service;
        _logger  = logger;
    }

    [HttpPost("analyze")]
    public async Task<IActionResult> Analyze([FromBody] ApiQualityReviewRequest request, CancellationToken ct)
    {
        bool hasAnyApiUrl =
            !string.IsNullOrWhiteSpace(request.RestBaseUrl)     ||
            !string.IsNullOrWhiteSpace(request.HealthEndpoint)  ||
            !string.IsNullOrWhiteSpace(request.SwaggerUrl)      ||
            !string.IsNullOrWhiteSpace(request.GraphQlEndpoint);

        if (!hasAnyApiUrl)
            return BadRequest(new { message = "No API endpoints configured. Add a REST Base URL, Health Endpoint, Swagger URL, or GraphQL Endpoint to the active Target Environment." });

        var correlationId = HttpContext.Request.Headers["X-Correlation-Id"].FirstOrDefault() ?? "unknown";
        _logger.LogInformation(
            "API quality review requested for environment '{Name}' CorrelationId: {CorrelationId}",
            request.EnvironmentName, correlationId);

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
            _logger.LogError(ex, "API quality review failed for environment '{Name}'", request.EnvironmentName);
            return StatusCode(500, new { message = "API quality review failed: " + ex.Message });
        }
    }
}
