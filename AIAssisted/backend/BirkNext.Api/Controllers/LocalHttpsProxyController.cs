using BirkNext.Api.Services.LocalHttpsProxy;
using BirkNext.LocalHttpsProxy;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

/// <summary>
/// Workstation-only control surface for the DEV HTTPS inspection proxy and the approved authenticated API checks it enables.
/// Same loopback-peer / loopback-host / configured-frontend-Origin gate as the managed Edge bridge. No response ever carries a credential.
/// </summary>
[ApiController]
[Route("api/local-https-proxy")]
[ServiceFilter(typeof(ManagedEdgeLocalCallerFilter))]
public sealed class LocalHttpsProxyController(ILocalHttpsProxyService proxy, IAuthenticatedApiExecutionService execution) : ControllerBase
{
    [HttpGet("runtime")]
    public Task<IActionResult> Runtime() => Run(async () => Ok(await proxy.GetRuntimeAsync()));

    [HttpPost("compatibility")]
    public Task<IActionResult> Compatibility(LocalHttpsProxyScopeRequest request, CancellationToken ct) => Run(async () => Ok(await proxy.CheckCompatibilityAsync(request, ct)));

    [HttpPost("start")]
    public Task<IActionResult> Start(LocalHttpsProxyScopeRequest request, CancellationToken ct) => Run(async () => Ok(await proxy.StartAsync(request, ct)));

    [HttpPost("status")]
    public Task<IActionResult> Status(LocalHttpsProxySessionRequest request) => Run(async () => Ok(await proxy.StatusAsync(request)));

    /// <summary>Cookie attributes (name, Secure, HttpOnly, SameSite, Domain, Path, persistence) observed in this session. Never a cookie value.</summary>
    [HttpPost("observed-cookie-attributes")]
    public Task<IActionResult> ObservedCookieAttributes(LocalHttpsProxySessionRequest request) => Run(async () => Ok(await proxy.ObservedCookieAttributesAsync(request)));

    [HttpPost("stop")]
    public Task<IActionResult> Stop(LocalHttpsProxySessionRequest request) => Run(async () => Ok(await proxy.StopAsync(request)));

    /// <summary>The attended session of the environment (metadata only). Needs the environment's current context fingerprint.</summary>
    [HttpPost("attended-session")]
    public IActionResult AttendedSession([FromServices] ITransientAuthenticatedApiContextStore store, LocalHttpsProxySessionRequest request)
    {
        Response.Headers.CacheControl = "no-store";
        return Ok(Describe(store, request));
    }

    /// <summary>
    /// Ends the attended session ("sign out of BirkNext's use of it"): the in-memory credential is wiped; the proxy keeps running and a new
    /// sign-in through it starts the next generation. Runs that need it then wait or are Blocked for authentication — never sent anonymously.
    /// </summary>
    [HttpPost("attended-session/end")]
    public IActionResult EndAttendedSession([FromServices] ITransientAuthenticatedApiContextStore store, LocalHttpsProxySessionRequest request)
    {
        Response.Headers.CacheControl = "no-store";
        if (string.IsNullOrWhiteSpace(request.ProfileId) || string.IsNullOrWhiteSpace(request.ContextFingerprint)) return BadRequest(new { message = "The environment and its context are required." });
        // Only a caller that knows the environment's current context ends its session.
        if (store.Describe(request.ProfileId, request.ContextFingerprint) is null) return Ok(Describe(store, request));
        store.Invalidate(request.ProfileId);
        return Ok(new AttendedSessionView(false, null, null, null, 0, null, "The attended session was ended. Sign in through the proxy again to start a new one."));
    }

    private static AttendedSessionView Describe(ITransientAuthenticatedApiContextStore store, LocalHttpsProxySessionRequest request) =>
        string.IsNullOrWhiteSpace(request.ProfileId) || string.IsNullOrWhiteSpace(request.ContextFingerprint) || store.Describe(request.ProfileId, request.ContextFingerprint) is not { } d
            ? new(false, null, null, null, 0, null, "No attended session: sign in to the application through the Local HTTPS proxy.")
            : new(true, d.ObservedHost, d.ObservedAt, d.ExpiresAt, d.Generation, d.Identity, $"Attended session active (generation {d.Generation}); expires {d.ExpiresAt:HH:mm:ss} UTC.");

    /// <summary>Adds the BirkNext DEV inspection root to the current user's trusted roots. Requires explicit confirmation; Windows asks again.</summary>
    [HttpPost("certificate/install")]
    public Task<IActionResult> InstallCertificate(LocalHttpsProxyCertificateRequest request) => Run(async () => Ok(await proxy.InstallCertificateAsync(request)));

    [HttpPost("certificate/remove")]
    public Task<IActionResult> RemoveCertificate(LocalHttpsProxyCertificateRequest request) => Run(async () => Ok(await proxy.RemoveCertificateAsync(request)));

    /// <summary>Starts a separate Edge instance with --proxy-server for the running session. Never changes global proxy settings.</summary>
    [HttpPost("launch-edge")]
    public Task<IActionResult> LaunchEdge(LocalHttpsProxyEdgeLaunchRequest request, CancellationToken ct) => Run(async () => Ok(await proxy.LaunchEdgeAsync(request, ct)));

    /// <summary>Closes only the dedicated browser this runtime launched, then starts it again on the current proxy port.</summary>
    [HttpPost("edge/restart")]
    public Task<IActionResult> RestartEdge(LocalHttpsProxyEdgeLaunchRequest request, CancellationToken ct) => Run(async () => Ok(await proxy.RestartEdgeAsync(request, ct)));

    [HttpPost("execute/rest")]
    public Task<IActionResult> ExecuteRest(AuthenticatedRestRequest request, CancellationToken ct) => Run(async () => Ok(await execution.ExecuteRestAsync(request, ct)));

    [HttpPost("execute/graphql")]
    public Task<IActionResult> ExecuteGraphQl(AuthenticatedGraphQlRequest request, CancellationToken ct) => Run(async () => Ok(await execution.ExecuteGraphQlQueryAsync(request, ct)));

    private async Task<IActionResult> Run(Func<Task<IActionResult>> action)
    {
        Response.Headers.CacheControl = "no-store";
        try { return await action(); }
        catch (ArgumentException) { return BadRequest(new { message = "Request rejected by the local HTTPS proxy safety policy." }); }
        catch (System.Collections.Generic.KeyNotFoundException) { return NotFound(new { message = "Proxy session expired or does not match this environment." }); }
        catch (Exception) { return Conflict(new { message = "Local HTTPS proxy operation unavailable. Check the proxy status and retry." }); }
    }
}
