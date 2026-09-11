using System.Net;
using BirkNext.Api.Services.ManagedEdge;
using BirkNext.ManagedEdge;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace BirkNext.Api.Controllers;

/// <summary>Workstation-only bridge. A browser Origin is required to prevent cross-site local CDP access.</summary>
[ApiController]
[Route("api/managed-edge")]
[ServiceFilter(typeof(ManagedEdgeLocalCallerFilter))]
public sealed class ManagedEdgeCdpController(IManagedEdgeCdpService service, IManagedEdgePreflightService preflight) : ControllerBase
{
    /// <summary>Transient compatibility check: Edge install, RemoteDebuggingAllowed policy, loopback CDP runtime, target tab. Nothing is persisted.</summary>
    [HttpPost("preflight")]
    public Task<IActionResult> Preflight(ManagedEdgePreflightRequest request, CancellationToken ct) =>
        Run(async () => Ok(await preflight.CheckAsync(request.TargetUrl, ct)));

    /// <summary>Starts a separate Edge instance with a dedicated BirkNext profile. Never touches the normal Edge profile or process.</summary>
    [HttpPost("launch")]
    public Task<IActionResult> Launch(ManagedEdgeLaunchRequest request, CancellationToken ct) =>
        Run(async () => Ok(await preflight.LaunchAsync(request.TargetUrl, ct)));

    [HttpPost("connect")]
    public Task<IActionResult> Connect(ManagedEdgeConnectRequest request, CancellationToken ct) =>
        Run(async () => Ok(await service.ConnectAsync(request, ct)));

    [HttpPost("status")]
    public Task<IActionResult> Status(ManagedEdgeSessionRequest request) => Run(async () => Ok(await service.StatusAsync(request)));

    [HttpPost("verify")]
    public Task<IActionResult> Verify(ManagedEdgeSessionRequest request) => Run(async () => Ok(await service.StatusAsync(request, true)));

    [HttpPost("fetch")]
    public Task<IActionResult> Fetch(ManagedEdgeFetchRequest request) => Run(async () => Ok(await service.ExecuteSameOriginFetchAsync(request)));

    [HttpPost("disconnect")]
    public Task<IActionResult> Disconnect(ManagedEdgeSessionRequest request) => Run(async () => { await service.DisconnectAsync(request); return NoContent(); });

    private async Task<IActionResult> Run(Func<Task<IActionResult>> action)
    {
        Response.Headers.CacheControl = "no-store";
        try { return await action(); }
        catch (ArgumentException) { return BadRequest(new { message = "Request rejected by managed Edge safety policy." }); }
        catch (System.Collections.Generic.KeyNotFoundException) { return NotFound(new { message = "Runtime session expired or does not match this environment." }); }
        catch (Exception) { return Conflict(new { message = "Managed Edge operation unavailable. Reconnect and verify again." }); }
    }
}

public sealed class ManagedEdgeLocalCallerFilter(IConfiguration configuration) : IActionFilter
{
    public void OnActionExecuting(ActionExecutingContext context)
    {
        var http = context.HttpContext;
        var origin = configuration["FRONTEND_ORIGIN"] ?? "http://localhost:5173";
        if (http.Connection.RemoteIpAddress is not { } peer || !IPAddress.IsLoopback(peer) ||
            !ManagedEdgePolicy.IsLoopbackHost(http.Request.Host.Host) ||
            !ManagedEdgePolicy.MatchesOrigin(origin, origin) || !ManagedEdgePolicy.IsLoopbackHost(new Uri(origin).Host) ||
            http.Request.Headers.Origin.ToString() != origin)
            context.Result = new StatusCodeResult(StatusCodes.Status403Forbidden);
    }
    public void OnActionExecuted(ActionExecutedContext context) { }
}
