using BirkNext.Api.Services.ActiveCdcTests;
using BirkNext.Integrations;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

/// <summary>
/// Active CDC tests (IQR → Active tests → CDC). Only built-in scenarios can run — there is no endpoint that accepts a payload, a hub name
/// or a connection string. Every gate (DEV/QA only, enrolled destination, source contract, synthetic key range) is enforced in the backend.
/// </summary>
[ApiController]
[Route("api/active-cdc-tests")]
public sealed class ActiveCdcTestsController(IActiveCdcTestService tests) : ControllerBase
{
    [HttpGet("scenarios")]
    public ActionResult<IReadOnlyList<ActiveCdcScenario>> Scenarios() => Ok(ActiveCdcScenarioCatalog.All);

    [HttpGet("readiness")]
    public async Task<ActionResult<ActiveCdcReadiness>> Readiness([FromQuery] string environmentId, [FromQuery] string integrationId, [FromQuery] string? environmentType,
        [FromQuery] string? targetUrl, [FromQuery] Guid? snapshotId, [FromQuery] string? scenarioId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(environmentId) || string.IsNullOrWhiteSpace(integrationId)) return BadRequest(new { message = "environmentId and integrationId are required." });
        try { return Ok(await tests.ReadinessAsync(environmentId, integrationId, environmentType, targetUrl, snapshotId, scenarioId, ct)); }
        catch (ActiveCdcRequestException ex) { return BadRequest(new { message = ex.Message }); }
    }

    [HttpPost("runs")]
    public async Task<ActionResult<ActiveCdcRun>> Start([FromBody] ActiveCdcRunRequest request, [FromQuery] string? environmentType, [FromQuery] string? targetUrl, CancellationToken ct)
    {
        try { return Ok(await tests.StartAsync(request, environmentType, targetUrl, ct)); }
        catch (ActiveCdcRequestException ex) { return BadRequest(new { message = ex.Message }); }
    }

    [HttpPost("runs/{runId:guid}/cancel")]
    public async Task<IActionResult> Cancel(Guid runId, CancellationToken ct) =>
        await tests.CancelAsync(runId, ct) ? Accepted(new { message = ActiveCdcLabels.CancelNotice }) : Conflict(new { message = "The run is not in flight." });

    [HttpGet("runs")]
    public async Task<ActionResult<IReadOnlyList<ActiveCdcRunSummary>>> History([FromQuery] string environmentId, [FromQuery] string? integrationId, CancellationToken ct) =>
        Ok(await tests.HistoryAsync(environmentId, integrationId, ct));

    [HttpGet("runs/{runId:guid}")]
    public async Task<ActionResult<ActiveCdcRun>> Get(Guid runId, CancellationToken ct) =>
        await tests.GetAsync(runId, ct) is { } run ? Ok(run) : NotFound();
}
