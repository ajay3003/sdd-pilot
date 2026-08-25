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
        var result = await accessibility.ReviewAsync(
            request.TargetUrl,
            new AccessibilityReviewOptions(
                request.NavigationTimeoutMs ?? 30000,
                request.StabilizationMs ?? 1000,
                true,
                "Public"),
            request.RequiresAuthentication,
            ct);
        return Ok(result);
    }

    [HttpPost("readiness")]
    public async Task<IActionResult> Readiness(CancellationToken ct) => Ok(await accessibility.CheckReadinessAsync(ct));
}

public sealed record AccessibilityReviewRequest(
    string TargetUrl,
    int? NavigationTimeoutMs = null,
    int? StabilizationMs = null,
    bool RequiresAuthentication = false);
