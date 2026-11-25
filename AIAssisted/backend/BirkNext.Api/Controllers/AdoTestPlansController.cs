using BirkNext.Api.Services.ActiveEventTesting;
using BirkNext.Api.Services.TestPlanning;
using BirkNext.TestPlanning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

/// <summary>
/// Azure DevOps Test Plans (2026-11-25): System Settings → Azure DevOps – Testplaner (destination, new test plan / suite) and Manual Testing →
/// Overfør til Azure DevOps → Send direkte. Uses the stored Azure DevOps connection (no second login). Reads never change anything; every write to
/// Azure DevOps (create plan, create suite, transfer) needs an explicit confirmation in the request and the plan-editing permission. Saving a
/// default destination is local only.
/// </summary>
[ApiController]
[Route("api/azure-devops/test-plans")]
public sealed class AdoTestPlansController(AdoTestPlanSettingsService settings, AdoTestPlanTransferService transfer, AdoTestResultsService results) : ControllerBase
{
    private string Actor => User.Identity is { IsAuthenticated: true, Name: { Length: > 0 } name } ? name : "Local user (this PC)";

    [HttpGet("settings")]
    public Task<ActionResult> Settings([FromQuery] string? projectKey, CancellationToken ct) => Run(async () => Ok(await settings.ViewAsync(projectKey, ct)));

    [HttpGet("connections/{id:guid}/projects")]
    public async Task<ActionResult<AdoListResult<AdoProjectInfo>>> Projects(Guid id, CancellationToken ct) => Ok(await settings.ProjectsAsync(id, ct));

    /// <summary>«Kontroller tilkobling»: an explicit read of the organization (and project).</summary>
    [HttpPost("connections/{id:guid}/check")]
    public async Task<ActionResult> Check(Guid id, [FromQuery] string? project, CancellationToken ct) => Ok(await settings.CheckAsync(id, project, ct));

    [HttpGet("plans")]
    public async Task<ActionResult<AdoListResult<AdoTestPlanInfo>>> Plans([FromQuery] Guid connectionId, [FromQuery] string project, CancellationToken ct) =>
        Ok(await settings.PlansAsync(connectionId, project, ct));

    [HttpGet("suites")]
    public async Task<ActionResult<AdoListResult<AdoTestSuiteInfo>>> Suites([FromQuery] Guid connectionId, [FromQuery] string project, [FromQuery] int planId, CancellationToken ct) =>
        Ok(await settings.SuitesAsync(connectionId, project, planId, ct));

    /// <summary>Creates a test plan in Azure DevOps (Confirmed must be true). An existing plan with the same name is returned instead.</summary>
    [HttpPost("plans")]
    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    public async Task<ActionResult<AdoCreateResult>> CreatePlan([FromBody] AdoCreatePlanRequest request, CancellationToken ct) => Ok(await settings.CreatePlanAsync(request, Actor, ct));

    [HttpPost("suites")]
    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    public async Task<ActionResult<AdoCreateResult>> CreateSuite([FromBody] AdoCreateSuiteRequest request, CancellationToken ct) => Ok(await settings.CreateSuiteAsync(request, Actor, ct));

    /// <summary>Checks a create whose answer was lost (read-only against Azure DevOps).</summary>
    [HttpPost("pending/{id:guid}/verify")]
    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    public async Task<ActionResult<AdoVerifyResult>> Verify(Guid id, CancellationToken ct) => Ok(await settings.VerifyAsync(id, ct));

    /// <summary>Saves the default destination for the system or a project (local only; nothing is created in Azure DevOps).</summary>
    [HttpPut("default")]
    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    public Task<ActionResult> SaveDefault([FromBody] AdoSaveDestinationRequest request, CancellationToken ct) =>
        Run(async () => Ok(new { saved = await settings.SaveDefaultAsync(request, Actor, ct) }));

    [HttpPost("transfer/preview")]
    public Task<ActionResult> Preview([FromBody] AdoTransferRequest request, CancellationToken ct) => Run(async () => Ok(await transfer.PreviewAsync(request, ct)));

    /// <summary>Sends the reviewed cases to Azure DevOps (Confirmed must be true).</summary>
    [HttpPost("transfer")]
    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    public Task<ActionResult> Transfer([FromBody] AdoTransferRequest request, CancellationToken ct) => Run(async () => Ok(await transfer.TransferAsync(request, Actor, ct)));

    [HttpPost("transfer/reconcile")]
    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    public Task<ActionResult> Reconcile([FromBody] AdoReconcileRequest request, CancellationToken ct) => Run(async () => Ok(await transfer.ReconcileAsync(request, Actor, ct)));

    /// <summary>Results fetched earlier from Azure DevOps for the plan («Gjennomført i Azure DevOps»).</summary>
    [HttpGet("results")]
    public Task<ActionResult> Results([FromQuery] string projectKey, [FromQuery] Guid draftId, CancellationToken ct) => Run(async () => Ok(await results.ListAsync(projectKey, draftId, ct)));

    /// <summary>Reads the latest results from Azure DevOps (read-only there) and stores the new ones locally.</summary>
    [HttpPost("results/fetch")]
    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    public Task<ActionResult> FetchResults([FromBody] AdoResultsFetchRequest request, CancellationToken ct) => Run(async () => Ok(await results.FetchAsync(request, Actor, ct)));

    private static async Task<ActionResult> Run(Func<Task<ActionResult>> action)
    {
        try { return await action(); }
        catch (TestPlanException e) { return new ObjectResult(new { code = e.Code, message = e.Message }) { StatusCode = e.Status }; }
    }
}
