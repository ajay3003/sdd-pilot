using BirkNext.Api.Services.ActiveEventTesting;
using BirkNext.Integrations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

/// <summary>
/// Active Event Testing (IQR → Active event tests). Execution, cancellation, run detail and history require an authenticated principal with
/// ActiveEventExecute. Environment trust, the provider catalog, scenarios and readiness are configuration metadata (the same exposure as the
/// open Integration catalog reads) and stay readable so the review explains why execution is unavailable — including "authentication not
/// configured". Only registered provider scenarios can run — there is no endpoint that accepts a payload, a namespace, a hub, a connection
/// string, an environment type or a target URL. Every gate is enforced in the backend; history mixes all providers (and read-only legacy runs).
/// </summary>
[ApiController]
[Authorize(Policy = BirkNextPermissions.ActiveEventExecutePolicy)]
[Route("api/active-events")]
public sealed class ActiveEventsController(IActiveEventLifecycleService lifecycle) : ControllerBase
{
    [AllowAnonymous]
    [HttpGet("environments/{environmentId}/trust")]
    public ActionResult<ActiveEventEnvironmentTrust> Trust(string environmentId) => Ok(lifecycle.EnvironmentTrust(environmentId));

    [AllowAnonymous]
    [HttpGet("providers")]
    public async Task<ActionResult<IReadOnlyList<ActiveEventProviderSummary>>> Providers([FromQuery] string environmentId, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(environmentId) ? BadRequest(new { message = "environmentId is required." }) : Ok(await lifecycle.ProvidersAsync(environmentId, ct));

    [AllowAnonymous]
    [HttpGet("scenarios")]
    public async Task<ActionResult<IReadOnlyList<ActiveEventScenarioDescriptor>>> Scenarios([FromQuery] string environmentId, [FromQuery] string integrationId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(environmentId) || string.IsNullOrWhiteSpace(integrationId)) return BadRequest(new { message = "environmentId and integrationId are required." });
        try { return Ok(await lifecycle.ScenariosAsync(environmentId, integrationId, ct)); }
        catch (ActiveEventRequestException ex) { return BadRequest(new { message = ex.Message }); }
    }

    [AllowAnonymous]
    [HttpGet("readiness")]
    public async Task<ActionResult<ActiveEventReadiness>> Readiness([FromQuery] string environmentId, [FromQuery] string integrationId, [FromQuery] string extensionId,
        [FromQuery] string scenarioId, [FromQuery] Guid? snapshotId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(environmentId) || string.IsNullOrWhiteSpace(integrationId) || string.IsNullOrWhiteSpace(extensionId) || string.IsNullOrWhiteSpace(scenarioId))
            return BadRequest(new { message = "environmentId, integrationId, extensionId and scenarioId are required." });
        try { return Ok(await lifecycle.ReadinessAsync(environmentId, integrationId, extensionId, scenarioId, snapshotId, ct)); }
        catch (ActiveEventRequestException ex) { return BadRequest(new { message = ex.Message }); }
    }

    [HttpPost("runs")]
    public async Task<ActionResult<ActiveEventRunResult>> Start([FromBody] ActiveEventRunRequest request, CancellationToken ct)
    {
        try { return Ok(await lifecycle.StartAsync(request, ct)); }
        catch (ActiveEventRequestException ex) { return BadRequest(new { message = ex.Message }); }
    }

    [HttpPost("runs/{runId:guid}/cancel")]
    public IActionResult Cancel(Guid runId) => lifecycle.Cancel(runId)
        ? Accepted(new { message = "Cancellation requested. A sent event cannot be recalled; cancelling only stops the remaining steps." })
        : Conflict(new { message = "The run is not in flight." });

    [HttpGet("runs")]
    public async Task<ActionResult<IReadOnlyList<ActiveEventRunSummary>>> History([FromQuery] string environmentId, [FromQuery] string? integrationId, [FromQuery] string? extensionId,
        [FromQuery] string? scenarioId, [FromQuery] ActiveEventRunStatus? status, [FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(environmentId) ? BadRequest(new { message = "environmentId is required." })
            : Ok(await lifecycle.HistoryAsync(new ActiveEventHistoryQuery(environmentId, integrationId, extensionId, scenarioId, status, from, to), ct));

    [HttpGet("runs/{runId:guid}")]
    public async Task<ActionResult<ActiveEventRunResult>> Get(Guid runId, CancellationToken ct) =>
        await lifecycle.GetAsync(runId, ct) is { } run ? Ok(run) : NotFound();
}
