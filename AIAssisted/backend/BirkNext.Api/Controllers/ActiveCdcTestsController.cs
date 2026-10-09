using BirkNext.Api.Services.ActiveCdcTests;
using BirkNext.Api.Services.ActiveEventTesting;
using BirkNext.Integrations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

/// <summary>
/// Active CDC tests (IQR → Active tests → CDC). Only built-in scenarios can run — there is no endpoint that accepts a payload, a hub name
/// or a connection string. Every gate (DEV/QA only, enrolled destination, source contract, synthetic key range) is enforced in the backend.
/// </summary>
[ApiController]
[Authorize(Policy = "ActiveEventExecute")]
[Route("api/active-cdc-tests")]
public sealed class ActiveCdcTestsController(IActiveCdcTestService tests, IActiveEventLifecycleService lifecycle) : ControllerBase
{
    private bool HasExecutionPermission() => User.Identity?.IsAuthenticated == true &&
        (User.Claims.Any(c => c.Type == "permission" && c.Value == "ActiveEventExecute") ||
         User.FindAll("scp").SelectMany(c => c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Contains("ActiveEventExecute", StringComparer.Ordinal));

    private ActionResult<T> AuthorizationFailure<T>() => User.Identity?.IsAuthenticated == true ? Forbid() : Unauthorized();

    [HttpGet("scenarios")]
    public async Task<ActionResult<IReadOnlyList<ActiveEventScenarioDescriptor>>> Scenarios([FromQuery] string environmentId, [FromQuery] string integrationId, CancellationToken ct)
    {
        if (!HasExecutionPermission()) return AuthorizationFailure<IReadOnlyList<ActiveEventScenarioDescriptor>>();
        if (string.IsNullOrWhiteSpace(environmentId) || string.IsNullOrWhiteSpace(integrationId)) return BadRequest(new { message = "environmentId and integrationId are required." });
        try { return Ok(await lifecycle.ScenariosAsync(environmentId, integrationId, ct)); }
        catch (ActiveCdcRequestException ex) { return BadRequest(new { message = ex.Message }); }
    }

    [HttpGet("readiness")]
    public async Task<ActionResult<ActiveCdcReadiness>> Readiness([FromQuery] string environmentId, [FromQuery] string integrationId,
        [FromQuery] Guid? snapshotId, [FromQuery] string? scenarioId, CancellationToken ct)
    {
        if (!HasExecutionPermission()) return AuthorizationFailure<ActiveCdcReadiness>();
        if (string.IsNullOrWhiteSpace(environmentId) || string.IsNullOrWhiteSpace(integrationId)) return BadRequest(new { message = "environmentId and integrationId are required." });
        try
        {
            var scenarios = await lifecycle.ScenariosAsync(environmentId, integrationId, ct);
            var descriptor = string.IsNullOrWhiteSpace(scenarioId) ? scenarios.FirstOrDefault() : scenarios.FirstOrDefault(item => item.ScenarioId == scenarioId);
            if (descriptor is null) return BadRequest(new { message = "No applicable registered scenario was found." });
            var readiness = await lifecycle.ReadinessAsync(environmentId, integrationId, descriptor.ExtensionId, descriptor.ScenarioId, snapshotId, ct);
            return Ok(ActiveEventLegacyCompatibilityMapper.Readiness(readiness));
        }
        catch (ActiveCdcRequestException ex) { return BadRequest(new { message = ex.Message }); }
    }

    [HttpPost("runs")]
    public async Task<ActionResult<ActiveCdcRun>> Start([FromBody] ActiveCdcRunRequest request, CancellationToken ct)
    {
        if (!HasExecutionPermission()) return AuthorizationFailure<ActiveCdcRun>();
        try
        {
            var descriptors = await lifecycle.ScenariosAsync(request.EnvironmentId, request.IntegrationId, ct);
            var descriptor = descriptors.FirstOrDefault(item => item.ScenarioId == request.ScenarioId);
            if (descriptor is null) return BadRequest(new { message = "The selected scenario is no longer available for this integration." });
            var result = await lifecycle.StartAsync(new ActiveEventRunRequest
            {
                TargetEnvironmentId = request.EnvironmentId,
                IntegrationId = request.IntegrationId,
                ExtensionId = descriptor.ExtensionId,
                ScenarioId = descriptor.ScenarioId,
                SourceSnapshotId = request.SourceSnapshotId,
                Confirmed = request.ConfirmedSend,
                ConfirmedDestination = request.ConfirmedEventHub,
            }, ct);
            return Ok(ActiveEventLegacyCompatibilityMapper.Run(result));
        }
        catch (ActiveCdcRequestException ex) { return BadRequest(new { message = ex.Message }); }
    }

    [HttpPost("runs/{runId:guid}/cancel")]
    public async Task<IActionResult> Cancel(Guid runId, CancellationToken ct) =>
        !HasExecutionPermission() ? (User.Identity?.IsAuthenticated == true ? Forbid() : Unauthorized()) :
        lifecycle.Cancel(runId) || await tests.CancelAsync(runId, ct) ? Accepted(new { message = ActiveCdcLabels.CancelNotice }) : Conflict(new { message = "The run is not in flight." });

    [HttpGet("runs")]
    public async Task<ActionResult<IReadOnlyList<ActiveCdcRunSummary>>> History([FromQuery] string environmentId, [FromQuery] string? integrationId, CancellationToken ct)
    {
        if (!HasExecutionPermission()) return AuthorizationFailure<IReadOnlyList<ActiveCdcRunSummary>>();
        var generic = await lifecycle.HistoryAsync(environmentId, integrationId, ct);
        var legacy = await tests.HistoryAsync(environmentId, integrationId, ct);
        return Ok(generic.Select(ActiveEventLegacyCompatibilityMapper.Summary).Concat(legacy).OrderByDescending(item => item.StartedAt).Take(50).ToArray());
    }

    [HttpGet("runs/{runId:guid}")]
    public async Task<ActionResult<ActiveCdcRun>> Get(Guid runId, CancellationToken ct)
    {
        if (!HasExecutionPermission()) return AuthorizationFailure<ActiveCdcRun>();
        if (await lifecycle.GetAsync(runId, ct) is { } generic) return Ok(ActiveEventLegacyCompatibilityMapper.Run(generic));
        return await tests.GetAsync(runId, ct) is { } legacy ? Ok(legacy) : NotFound();
    }
}
