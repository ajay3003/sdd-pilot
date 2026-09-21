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

    [HttpPost("stop")]
    public Task<IActionResult> Stop(LocalHttpsProxySessionRequest request) => Run(async () => Ok(await proxy.StopAsync(request)));

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
