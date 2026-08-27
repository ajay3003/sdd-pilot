using BirkNext.Api.Services.FrontendBrowserRuntime;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

[ApiController]
[Route("api/frontend-runtime")]
public class FrontendBrowserRuntimeController : ControllerBase
{
    private readonly IFrontendBrowserRuntimeReviewService _runtime;
    private readonly ILogger<FrontendBrowserRuntimeController> _logger;

    public FrontendBrowserRuntimeController(
        IFrontendBrowserRuntimeReviewService runtime,
        ILogger<FrontendBrowserRuntimeController> logger)
    {
        _runtime = runtime;
        _logger = logger;
    }

    [HttpPost("review")]
    public async Task<IActionResult> Review([FromBody] BrowserRuntimeRequest request, CancellationToken ct)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.TargetUrl))
            return BadRequest(new { message = "TargetUrl is required" });

        if (!Uri.TryCreate(request.TargetUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "http" && uri.Scheme != "https"))
        {
            return BadRequest(new { message = "TargetUrl must be a valid http or https URL" });
        }

        var correlationId = HttpContext.Request.Headers["X-Correlation-Id"].FirstOrDefault() ?? "unknown";
        _logger.LogInformation(
            "Browser runtime review requested for {Host} CorrelationId: {CorrelationId}",
            uri.Host, correlationId);

        try
        {
            var options = new BrowserRuntimeOptions(
                NavigationTimeoutMs: request.NavigationTimeoutMs ?? 30000,
                StartupObservationMs: request.StartupObservationMs ?? 5000,
                HeadlessMode: request.HeadlessMode ?? true);

            var execution = request.ExecutionMode == BrowserRuntimeExecutionMode.AuthenticatedSessionPage
                ? new BrowserRuntimeExecutionRequest(
                    request.TargetUrl,
                    BrowserRuntimeExecutionMode.AuthenticatedSessionPage,
                    request.ReviewSessionId,
                    request.ProfileId,
                    request.AuthenticatedSessionId,
                    options)
                : new BrowserRuntimeExecutionRequest(request.TargetUrl, Options: options);

            if (execution.ExecutionMode == BrowserRuntimeExecutionMode.AuthenticatedSessionPage &&
                (string.IsNullOrWhiteSpace(execution.ReviewSessionId) ||
                 string.IsNullOrWhiteSpace(execution.ProfileId) ||
                 string.IsNullOrWhiteSpace(execution.AuthenticatedSessionId)))
                return BadRequest(new { message = "Authenticated session, review, and profile identifiers are required." });

            var result = await _runtime.ReviewAsync(execution, ct);
            return Ok(result);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Browser runtime review cancelled. CorrelationId: {CorrelationId}", correlationId);
            return StatusCode(499);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Browser runtime review failed. CorrelationId: {CorrelationId}", correlationId);
            return StatusCode(500, new { message = "Review failed unexpectedly. Check backend logs.", correlationId });
        }
    }

    [HttpPost("readiness")]
    public async Task<IActionResult> CheckReadiness(CancellationToken ct)
    {
        try
        {
            var result = await _runtime.CheckReadinessAsync(ct);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Readiness check failed");
            return StatusCode(500, new { message = "Readiness check failed", error = ex.Message });
        }
    }
}

public sealed record BrowserRuntimeRequest(
    string TargetUrl,
    int? NavigationTimeoutMs = null,
    int? StartupObservationMs = null,
    bool? HeadlessMode = null,
    BrowserRuntimeExecutionMode ExecutionMode = BrowserRuntimeExecutionMode.AnonymousOwnedBrowser,
    string? ReviewSessionId = null,
    string? ProfileId = null,
    string? AuthenticatedSessionId = null);
