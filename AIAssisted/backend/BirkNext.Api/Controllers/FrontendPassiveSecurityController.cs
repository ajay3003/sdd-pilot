using BirkNext.Api.Services.FrontendPassiveSecurity;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

[ApiController, Route("api/frontend-passive-security")]
public sealed class FrontendPassiveSecurityController(IFrontendZapPassiveReviewService service) : ControllerBase
{
    [HttpPost("review")]
    public async Task<IActionResult> Review([FromBody] PassiveSecurityReviewRequest request, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(request?.TargetUrl) ? BadRequest(new { message = "TargetUrl is required" }) : Ok(await service.ReviewAsync(request, ct));

    [HttpPost("readiness")]
    public async Task<IActionResult> Readiness(CancellationToken ct) => Ok(await service.CheckReadinessAsync(ct));
}
