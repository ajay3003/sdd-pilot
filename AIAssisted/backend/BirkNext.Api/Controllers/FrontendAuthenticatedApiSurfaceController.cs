using BirkNext.Api.Services.FrontendQualityEngines;
using BirkNext.LocalHttpsProxy;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

/// <summary>
/// Authenticated API-surface probes for the Frontend Quality Review. Executes only approved read-only requests through the
/// authenticated-review gateway with the memory-only Local HTTPS proxy credential and returns sanitized results. No token,
/// cookie, header dump or body is ever returned; a missing/expired context yields a typed "not executed" result, never a
/// silent public fallback.
/// </summary>
[ApiController]
[Route("api/frontend-quality")]
public sealed class FrontendAuthenticatedApiSurfaceController(IFrontendAuthenticatedApiSurfaceService surface) : ControllerBase
{
    [HttpPost("authenticated-api-surface")]
    public async Task<ActionResult<FrontendAuthenticatedApiSurfaceResult>> Probe([FromBody] FrontendAuthenticatedApiSurfaceRequest request, CancellationToken cancellationToken)
    {
        if (request?.Identity is null)
            return BadRequest(new { message = "Review identity is required." });
        Response.Headers.CacheControl = "no-store";
        return Ok(await surface.ProbeAsync(request, cancellationToken));
    }
}
