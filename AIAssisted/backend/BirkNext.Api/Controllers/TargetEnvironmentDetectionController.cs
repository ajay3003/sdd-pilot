using BirkNext.Api.Filters;
using BirkNext.Api.Models;
using BirkNext.Api.Services.AuthenticatedReview;
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
[ServiceFilter(typeof(RequireTargetDetectionHttpsFilter))]
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

    /// <summary>
    /// Continues target detection using interactive browser authentication.
    /// Called after preflight has identified authentication requirement.
    /// Launches headed browser for user-driven authentication flow.
    /// </summary>
    /// <param name="request">Browser detection request with target URL, session ID, and profile ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Detection outcome with completion state and metadata</returns>
    [HttpPost("continue-in-browser")]
    public async Task<IActionResult> ContinueDetectionInBrowser(
        [FromBody] BrowserDetectionRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.TargetUrl))
        {
            _logger.LogWarning("Browser detection request received with invalid target URL");
            return BadRequest(new { message = "Target URL is required" });
        }

        if (string.IsNullOrWhiteSpace(request.ReviewSessionId))
        {
            _logger.LogWarning("Browser detection request received without review session ID");
            return BadRequest(new { message = "Review session ID is required" });
        }

        if (string.IsNullOrWhiteSpace(request.ProfileId))
        {
            _logger.LogWarning("Browser detection request received without profile ID");
            return BadRequest(new { message = "Profile ID is required" });
        }

        try
        {
            // Instantiate interactive browser strategy and delegate to service
            var strategy = new InteractiveBrowserDetectionStrategy(
                HttpContext.RequestServices.GetRequiredService<IAuthenticatedBrowserSessionManager>(),
                HttpContext.RequestServices.GetRequiredService<ILogger<InteractiveBrowserDetectionStrategy>>());

            var outcome = await _detectionService.DetectWithStrategyAsync(
                request.TargetUrl,
                request.ReviewSessionId,
                request.ProfileId,
                strategy,
                cancellationToken);

            return Ok(outcome);
        }
        catch (OperationCanceledException)
        {
            return StatusCode(408, new { message = "Browser detection timeout" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during browser detection for {Url}", request.TargetUrl);
            return StatusCode(500, new { message = "Browser detection failed" });
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
    [RegularExpression(@"^https?://.+$", ErrorMessage = "Only HTTP and HTTPS schemes are supported")]
    public string TargetUrl { get; set; } = "";
}

/// <summary>
/// Request model for browser-based detection continuation.
/// Do NOT send credentials, tokens, or authorization headers here.
/// </summary>
public sealed class BrowserDetectionRequest
{
    /// <summary>
    /// Target URL to continue detection against.
    /// Example: https://m2lbdev.bufetat.no/
    /// </summary>
    [Required(ErrorMessage = "Target URL is required")]
    [Url(ErrorMessage = "Target URL must be a valid HTTP/HTTPS URL")]
    [MaxLength(2048, ErrorMessage = "Target URL must not exceed 2048 characters")]
    [RegularExpression(@"^https?://.+$", ErrorMessage = "Only HTTP and HTTPS schemes are supported")]
    public string TargetUrl { get; set; } = "";

    /// <summary>
    /// Review session ID for tracking and authorization.
    /// </summary>
    [Required(ErrorMessage = "Review session ID is required")]
    [MaxLength(256, ErrorMessage = "Review session ID must not exceed 256 characters")]
    public string ReviewSessionId { get; set; } = "";

    /// <summary>
    /// Profile ID associated with this detection.
    /// Used to bind detection result to specific profile and prevent cross-profile leakage.
    /// </summary>
    [Required(ErrorMessage = "Profile ID is required")]
    [MaxLength(256, ErrorMessage = "Profile ID must not exceed 256 characters")]
    public string ProfileId { get; set; } = "";
}
