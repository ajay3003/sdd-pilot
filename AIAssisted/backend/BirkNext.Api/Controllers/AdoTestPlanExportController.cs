using BirkNext.Api.Services.ActiveEventTesting;
using BirkNext.Api.Services.TestPlanning;
using BirkNext.TestPlanning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

/// <summary>
/// Azure DevOps Test Plans export (offline XLSX / CSV import files) from saved test cases. Reading, previewing and generating a file need only
/// the local session (the plan is not changed); saving the project's export preferences needs the plan-editing permission. Nothing here
/// connects to Azure DevOps.
/// </summary>
[ApiController]
[Route("api/test-plan-suggestions/ado-export")]
public sealed class AdoTestPlanExportController(AdoTestPlanExportService export) : ControllerBase
{
    private string Actor => User.Identity is { IsAuthenticated: true, Name: { Length: > 0 } name } ? name : "Local user (this PC)";

    [HttpGet("profile")]
    public Task<ActionResult> Profile([FromQuery] string projectKey, CancellationToken ct) => Run(async () => Ok(await export.ProfileAsync(projectKey, ct)));

    [HttpPut("profile")]
    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    public Task<ActionResult> SaveProfile([FromQuery] string projectKey, [FromBody] AdoExportProfile profile, CancellationToken ct) =>
        Run(async () => Ok(await export.SaveProfileAsync(projectKey, profile, Actor, ct)));

    [HttpPost("preview")]
    public Task<ActionResult> Preview([FromBody] AdoExportRequest request, CancellationToken ct) => Run(async () => Ok(await export.PreviewAsync(request, ct)));

    [HttpPost("file")]
    public Task<ActionResult> File([FromBody] AdoExportRequest request, CancellationToken ct) => Run(async () => Ok(await export.FileAsync(request, Actor, ct)));

    /// <summary>The export package of a hierarchical plan (2026-11-26): overview, suite manifest, one import file per suite and the import guide (ZIP; no remote call).</summary>
    [HttpPost("package")]
    public Task<ActionResult> Package([FromBody] AdoExportRequest request, CancellationToken ct) => Run(async () => Ok(await export.PackageAsync(request, Actor, ct)));

    [HttpPost("report")]
    public Task<ActionResult> Report([FromBody] AdoExportRequest request, CancellationToken ct) => Run(async () => Ok(await export.ReportAsync(request, ct)));

    [HttpGet("history")]
    public Task<ActionResult> History([FromQuery] string projectKey, CancellationToken ct) => Run(async () => Ok(await export.HistoryAsync(projectKey, ct)));

    private static async Task<ActionResult> Run(Func<Task<ActionResult>> action)
    {
        try { return await action(); }
        catch (TestPlanException e) { return new ObjectResult(new { code = e.Code, message = e.Message }) { StatusCode = e.Status }; }
    }
}
