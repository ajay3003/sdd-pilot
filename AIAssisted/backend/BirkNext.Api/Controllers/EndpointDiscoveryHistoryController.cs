using BirkNext.Api.Services.LocalHttpsProxy;
using BirkNext.LocalHttpsProxy;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

/// <summary>
/// Persisted Endpoint Discovery history (runs + observed operations) of one Target Environment and target host. A capture still running for the
/// same environment is included as the in-progress latest run, so Live Discovery shows "observed now" before the capture is stopped. Safe
/// metadata only; nothing here writes traffic content.
/// </summary>
[ApiController]
[Route("api/endpoint-discovery")]
public sealed class EndpointDiscoveryHistoryController(EndpointObservationHistoryService history, LocalHttpsProxyService proxy) : ControllerBase
{
    [HttpGet("history")]
    public async Task<ActionResult<EndpointObservationHistoryView>> History([FromQuery] string environmentId, [FromQuery] string targetUrl, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(environmentId) || EndpointObservationHistoryService.HostOf(targetUrl) is not { } host)
            return BadRequest(new { message = "environmentId and an absolute targetUrl are required." });
        return Ok(await history.CurrentViewAsync(proxy, environmentId, host, ct));
    }

    /// <summary>One-time import of the observations this browser held before runs were recorded (ignored once any run exists for the host).</summary>
    [HttpPost("baseline")]
    public async Task<ActionResult<EndpointDiscoveryRunSummary?>> Baseline([FromQuery] string environmentId, [FromBody] EndpointObservationBaselineRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(environmentId) || string.IsNullOrWhiteSpace(request.Host)) return BadRequest(new { message = "environmentId and host are required." });
        return Ok(await history.ImportBaselineAsync(environmentId, request.Host, request.Observations ?? [], ct));
    }
}
