using BirkNext.Api.Services.LocalHttpsProxy;
using BirkNext.LocalHttpsProxy;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

/// <summary>
/// Resolves the authenticated-review capability matrix for the active Target Environment so any review page (API, Integration,
/// Front-end) can show a consistent authenticated-testing status. Returns non-secret capability flags only; never a token, header or session.
/// </summary>
[ApiController]
[Route("api/authenticated-review")]
public sealed class AuthenticatedReviewController(IAuthenticatedReviewGateway gateway) : ControllerBase
{
    [HttpPost("capabilities")]
    public ActionResult<AuthenticatedReviewCapabilities> Capabilities([FromBody] AuthenticatedReviewIdentity identity)
    {
        Response.Headers.CacheControl = "no-store";
        return Ok(gateway.Resolve(identity));
    }
}
