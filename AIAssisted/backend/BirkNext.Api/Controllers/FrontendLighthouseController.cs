using BirkNext.Api.Services.FrontendLighthouse;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

[ApiController]
[Route("api/frontend-lighthouse")]
public sealed class FrontendLighthouseController(IFrontendLighthouseReviewService lighthouse) : ControllerBase
{
    [HttpPost("review")]
    public async Task<IActionResult> Review([FromBody] LighthouseReviewRequest request, CancellationToken ct)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.TargetUrl)) return BadRequest(new { message = "TargetUrl is required" });
        return Ok(await lighthouse.ReviewAsync(request.TargetUrl,
            new LighthouseReviewOptions(request.TimeoutMs ?? 90000, "Public"), request.RequiresAuthentication, ct));
    }

    [HttpPost("readiness")]
    public async Task<IActionResult> Readiness(CancellationToken ct) => Ok(await lighthouse.CheckReadinessAsync(ct));
}

public sealed record LighthouseReviewRequest(string TargetUrl, int? TimeoutMs = null, bool RequiresAuthentication = false);
