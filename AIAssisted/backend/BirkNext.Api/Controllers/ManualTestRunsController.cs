using BirkNext.Api.Services.ActiveEventTesting;
using BirkNext.Api.Services.TestPlanning;
using BirkNext.TestPlanning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

/// <summary>
/// Manual test runs of saved test cases (Manual Testing → Execute &amp; document). Every call names the project; a run of another project is
/// never returned or changed (404). Reading needs only the local session; starting and recording a run need the same write permission as
/// editing a test plan. Nothing is executed by BirkNext and nothing is sent to Azure DevOps: a tester records what happened.
/// </summary>
[ApiController]
[Route("api/manual-test-runs")]
public sealed class ManualTestRunsController(ManualTestRunService runs) : ControllerBase
{
    private string Actor => User.Identity is { IsAuthenticated: true, Name: { Length: > 0 } name } ? name : "Local user (this PC)";

    [HttpGet]
    public Task<ActionResult> List([FromQuery] string projectKey, [FromQuery] Guid? draftId, [FromQuery] string? caseId, CancellationToken ct) =>
        Run(async () => Ok(await runs.ListAsync(projectKey, draftId, caseId, ct)));

    /// <summary>A page of the project's runs (newest first, stable order) with the exact total. take: 1–500.</summary>
    [HttpGet("page")]
    public Task<ActionResult> Page([FromQuery] string projectKey, [FromQuery] Guid? draftId, [FromQuery] string? caseId, [FromQuery] int skip = 0, [FromQuery] int take = 50,
        CancellationToken ct = default) =>
        Run(async () =>
        {
            var (list, total) = await runs.ListPageAsync(projectKey, draftId, caseId, skip, take, ct);
            return Ok(new ManualRunPage(list, total, skip, take));
        });

    [HttpGet("{id:guid}")]
    public Task<ActionResult> Get(Guid id, [FromQuery] string projectKey, CancellationToken ct) => Run(async () => Ok(await runs.GetAsync(projectKey, id, ct)));

    /// <summary>Starts a run of the saved case as it is now (or returns its run that is still in progress).</summary>
    [HttpPost]
    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    public Task<ActionResult> Start([FromBody] StartManualRunRequest request, CancellationToken ct) => Run(async () => Ok(await runs.StartAsync(request, Actor, ct)));

    [HttpPut("{id:guid}")]
    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    public Task<ActionResult> Save(Guid id, [FromQuery] string projectKey, [FromBody] SaveManualRunRequest request, CancellationToken ct) =>
        Run(async () => Ok(await runs.SaveAsync(projectKey, id, request, Actor, ct)));

    [HttpPost("{id:guid}/complete")]
    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    public Task<ActionResult> Complete(Guid id, [FromQuery] string projectKey, [FromBody] CompleteManualRunRequest request, CancellationToken ct) =>
        Run(async () => Ok(await runs.CompleteAsync(projectKey, id, request, Actor, ct)));

    [HttpPost("{id:guid}/abandon")]
    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    public Task<ActionResult> Abandon(Guid id, [FromQuery] string projectKey, [FromBody] AbandonManualRunRequest request, CancellationToken ct) =>
        Run(async () => Ok(await runs.AbandonAsync(projectKey, id, request, Actor, ct)));

    private static async Task<ActionResult> Run(Func<Task<ActionResult>> action)
    {
        try { return await action(); }
        catch (TestPlanException e) { return new ObjectResult(new { code = e.Code, message = e.Message }) { StatusCode = e.Status }; }
    }
}
