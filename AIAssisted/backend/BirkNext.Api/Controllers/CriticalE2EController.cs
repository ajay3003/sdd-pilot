using BirkNext.Api.Services.CriticalE2E;
using BirkNext.Api.Services.ManagedEdge;
using BirkNext.CriticalE2E;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

/// <summary>
/// Critical E2E Regression. Same loopback-peer / loopback-host / configured-frontend-Origin gate as the other
/// workstation-local capabilities, because running a flow drives an authenticated browser session and reaches a target
/// environment's APIs — neither is something a page on the internet gets to ask for.
/// </summary>
[ApiController]
[Route("api/critical-e2e")]
[ServiceFilter(typeof(ManagedEdgeLocalCallerFilter))]
public sealed class CriticalE2EController(ICriticalE2EService service) : ControllerBase
{
    [HttpPost("overview")]
    public IActionResult Overview(CriticalE2EOverviewRequest request) => Run(() => Ok(service.Overview(request)));

    [HttpGet("flows")]
    public IActionResult Flows([FromQuery] string environmentId) => Run(() => Ok(service.Flows(environmentId ?? "")));

    [HttpPost("flows")]
    public IActionResult SaveFlow(CriticalE2EFlowDefinition flow) => Run(() => Ok(service.SaveFlow(flow)));

    [HttpDelete("flows/{flowId}")]
    public IActionResult DeleteFlow(string flowId) => Run(() => service.DeleteFlow(flowId) ? Ok(new { deleted = true }) : NotFound());

    /// <summary>Runs one flow, or every enabled flow of one mode. Returns the runs and the refreshed overview together.</summary>
    [HttpPost("run")]
    public async Task<IActionResult> RunAsync(CriticalE2ERunFlowRequest request, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        try { return Ok(await service.RunAsync(request, cancellationToken)); }
        catch (OperationCanceledException) { return StatusCode(StatusCodes.Status499ClientClosedRequest, new { message = "Run cancelled." }); }
        catch (ArgumentException) { return BadRequest(new { message = "Request rejected by the Critical E2E safety policy." }); }
        catch (Exception) { return Conflict(new { message = "Critical E2E operation unavailable." }); }
    }

    private IActionResult Run(Func<IActionResult> action)
    {
        Response.Headers.CacheControl = "no-store";
        try { return action(); }
        catch (ArgumentException) { return BadRequest(new { message = "Request rejected by the Critical E2E safety policy." }); }
        catch (Exception) { return Conflict(new { message = "Critical E2E operation unavailable." }); }
    }
}
