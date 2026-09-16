using System.Net;
using BirkNext.Api.Services.BrowserCompanion;
using BirkNext.Api.Services.ManagedEdge;
using BirkNext.BrowserCompanion;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace BirkNext.Api.Controllers;

/// <summary>
/// BirkNext UI side of the Browser Companion channel: start pairing, read status, unpair. Same loopback-peer / loopback-host /
/// configured-frontend-Origin gate as the proxy and managed Edge controllers. Responses carry only non-secret status and safe evidence.
/// </summary>
[ApiController]
[Route("api/browser-companion")]
[ServiceFilter(typeof(ManagedEdgeLocalCallerFilter))]
public sealed class BrowserCompanionController(IBrowserCompanionService companion) : ControllerBase
{
    [HttpPost("pairing/start")]
    public IActionResult StartPairing(BrowserCompanionPairingStartRequest request) => Run(() => Ok(companion.StartPairing(request)));

    [HttpPost("status")]
    public IActionResult Status(BrowserCompanionStatusRequest request) => Run(() => Ok(companion.Status(request.ProfileId)));

    [HttpPost("unpair")]
    public IActionResult Unpair(BrowserCompanionStatusRequest request) => Run(() => Ok(companion.Unpair(request.ProfileId)));

    private IActionResult Run(Func<IActionResult> action)
    {
        Response.Headers.CacheControl = "no-store";
        try { return action(); }
        catch (ArgumentException) { return BadRequest(new { message = "Request rejected by the browser companion safety policy." }); }
        catch (Exception) { return Conflict(new { message = "Browser companion operation unavailable." }); }
    }
}

/// <summary>
/// Extension side of the Browser Companion channel. Loopback peer and host only, and the request Origin must be a browser-extension
/// origin (never a web page, never the BirkNext frontend). The session id travels in the JSON body; the extension origin is bound to the
/// session at pairing so a different extension cannot reuse it. Payloads are size-limited.
/// </summary>
[ApiController]
[Route("api/browser-companion/extension")]
[ServiceFilter(typeof(BrowserCompanionExtensionCallerFilter))]
public sealed class BrowserCompanionExtensionController(IBrowserCompanionService companion) : ControllerBase
{
    private string ExtensionOrigin => Request.Headers.Origin.ToString().Trim();

    [HttpPost("pair")]
    [RequestSizeLimit(8 * 1024)]
    public IActionResult Pair(BrowserCompanionPairRequest request) => Run(() => Ok(companion.CompletePairing(request, ExtensionOrigin)));

    [HttpPost("validate")]
    [RequestSizeLimit(8 * 1024)]
    public IActionResult Validate(BrowserCompanionHeartbeat request) => Run(() => Ok(companion.ValidateSession(request.SessionId, request.ProfileId, ExtensionOrigin)));

    [HttpPost("heartbeat")]
    [RequestSizeLimit(8 * 1024)]
    public IActionResult Heartbeat(BrowserCompanionHeartbeat request) => Run(() => Ok(companion.Heartbeat(request, ExtensionOrigin)));

    [HttpPost("evidence")]
    [RequestSizeLimit(BrowserCompanionLimits.MaxEnvelopeBytes)]
    public IActionResult Evidence(BrowserCompanionEvidenceEnvelope envelope) => Run(() =>
    {
        var result = companion.AcceptEvidence(envelope, ExtensionOrigin);
        return result.Accepted ? Ok(result) : StatusCode(StatusCodes.Status403Forbidden, result);
    });

    private IActionResult Run(Func<IActionResult> action)
    {
        Response.Headers.CacheControl = "no-store";
        try { return action(); }
        catch (ArgumentException) { return BadRequest(new { message = "Request rejected by the browser companion safety policy." }); }
        catch (Exception) { return Conflict(new { message = "Browser companion operation unavailable." }); }
    }
}

/// <summary>Loopback peer + loopback host + browser-extension Origin. No credential is involved; a web page can never pass this gate.</summary>
public sealed class BrowserCompanionExtensionCallerFilter : IActionFilter
{
    public void OnActionExecuting(ActionExecutingContext context)
    {
        var http = context.HttpContext;
        if (http.Connection.RemoteIpAddress is not { } peer || !IPAddress.IsLoopback(peer) ||
            !ManagedEdgePolicy.IsLoopbackHost(http.Request.Host.Host) ||
            !BrowserCompanionService.IsExtensionOrigin(http.Request.Headers.Origin.ToString()))
            context.Result = new StatusCodeResult(StatusCodes.Status403Forbidden);
    }

    public void OnActionExecuted(ActionExecutedContext context) { }
}
