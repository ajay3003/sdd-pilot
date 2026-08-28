using BirkNext.Api.Services.FrontendAccessibility;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

[ApiController]
[Route("api/frontend-accessibility")]
public sealed class FrontendAccessibilityController(IFrontendAccessibilityReviewService accessibility) : ControllerBase
{
    [HttpPost("review")]
    public async Task<IActionResult> Review([FromBody] AccessibilityReviewRequest request, CancellationToken ct)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.TargetUrl))
            return BadRequest(new { message = "TargetUrl is required" });

        if (request.ExecutionMode == AccessibilityExecutionMode.AuthenticatedSessionPage &&
            (string.IsNullOrWhiteSpace(request.SessionId) ||
             string.IsNullOrWhiteSpace(request.ReviewSessionId) ||
             string.IsNullOrWhiteSpace(request.ProfileId)))
            return BadRequest(new { message = "Authenticated session, review, and profile identifiers are required." });

        var execution = request.ExecutionMode == AccessibilityExecutionMode.AuthenticatedSessionPage
            ? new AccessibilityExecutionRequest(
                request.TargetUrl,
                AccessibilityExecutionMode.AuthenticatedSessionPage,
                request.ReviewSessionId,
                request.ProfileId,
                request.SessionId,
                new AccessibilityReviewOptions(
                    request.NavigationTimeoutMs ?? 30000,
                    request.StabilizationMs ?? 1000,
                    true,
                    "Public"))
            : new AccessibilityExecutionRequest(
                request.TargetUrl,
                AccessibilityExecutionMode.AnonymousOwnedBrowser,
                null, null, null,
                new AccessibilityReviewOptions(
                    request.NavigationTimeoutMs ?? 30000,
                    request.StabilizationMs ?? 1000,
                    true,
                    "Public"));

        var result = await accessibility.ReviewAsync(execution, ct);
        return Ok(result);
    }

    [HttpPost("readiness")]
    public async Task<IActionResult> Readiness(CancellationToken ct) => Ok(await accessibility.CheckReadinessAsync(ct));
}

public sealed record AccessibilityReviewRequest(
    string TargetUrl,
    int? NavigationTimeoutMs = null,
    int? StabilizationMs = null,
    bool RequiresAuthentication = false,
    AccessibilityExecutionMode ExecutionMode = AccessibilityExecutionMode.AnonymousOwnedBrowser,
    string? SessionId = null,
    string? ReviewSessionId = null,
    string? ProfileId = null);
