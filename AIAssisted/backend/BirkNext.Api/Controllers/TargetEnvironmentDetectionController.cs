using BirkNext.Api.Models;
using BirkNext.Api.Services.TargetEnvironmentDetection;
using Microsoft.AspNetCore.Mvc;
using System.ComponentModel.DataAnnotations;

namespace BirkNext.Api.Controllers;

/// <summary>
/// API endpoint for frontend target environment configuration detection.
/// Safely inspects target URLs to extract authentication and configuration metadata.
/// All requests pass through SSRF validation before making outbound requests.
/// Response contains only safe, non-sensitive metadata suitable for configuration draft.
/// SECURITY: HTTPS only; rate-limited; SSRF validation; response sanitization.
/// NOTE: Authentication is not configured in this application.
/// This endpoint is internal (frontend-to-backend within same Tester Package).
/// </summary>
[ApiController]
[Route("api/frontend-target")]
[RequireHttps]
public sealed class TargetEnvironmentDetectionController : ControllerBase
{
    private readonly ITargetEnvironmentDetectionService _detectionService;
    private readonly ILogger<TargetEnvironmentDetectionController> _logger;

    public TargetEnvironmentDetectionController(
        ITargetEnvironmentDetectionService detectionService,
        ILogger<TargetEnvironmentDetectionController> logger)
    {
        _detectionService = detectionService;
        _logger = logger;
    }

    /// <summary>
    /// Detects target environment configuration from URL inspection.
    /// </summary>
    /// <param name="request">Detection request with target URL</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Safe detection metadata</returns>
    [HttpPost("detect")]
    public async Task<IActionResult> DetectConfiguration(
        [FromBody] TargetEnvironmentDetectionRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.TargetUrl))
        {
            _logger.LogWarning("Detection request received with invalid target URL");
            return BadRequest(new { message = "Target URL is required" });
        }

        try
        {
            var result = await _detectionService.DetectFromUrlAsync(request.TargetUrl, cancellationToken);
            return Ok(result);
        }
        catch (OperationCanceledException)
        {
            return StatusCode(408, new { message = "Detection timeout" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during detection for {Url}", request.TargetUrl);
            return StatusCode(500, new { message = "Detection failed" });
        }
    }
}

/// <summary>
/// Request model for target detection.
/// Do NOT send credentials, tokens, or authorization headers here.
/// </summary>
public sealed class TargetEnvironmentDetectionRequest
{
    /// <summary>
    /// Target URL to inspect for configuration.
    /// Example: https://m2lbdev.bufetat.no/
    /// </summary>
    [Required(ErrorMessage = "Target URL is required")]
    [Url(ErrorMessage = "Target URL must be a valid HTTP/HTTPS URL")]
    [MaxLength(2048, ErrorMessage = "Target URL must not exceed 2048 characters")]
    [RegularExpression(@"^https?://", ErrorMessage = "Only HTTP and HTTPS schemes are supported")]
    public string TargetUrl { get; set; } = "";
}
