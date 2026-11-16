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

    /// <summary>Every defect of the project's runs, with the run it was found in.</summary>
    [HttpGet("defects")]
    public Task<ActionResult> Defects([FromQuery] string projectKey, CancellationToken ct) => Run(async () => Ok(await runs.DefectsAsync(projectKey, ct)));

    /// <summary>Follows up a defect of a completed or stopped run (status, work item reference, note, retest run). The run's results stay final.</summary>
    [HttpPost("{id:guid}/defects/{defectId}")]
    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    public Task<ActionResult> UpdateDefect(Guid id, string defectId, [FromQuery] string projectKey, [FromBody] UpdateManualDefectRequest request, CancellationToken ct) =>
        Run(async () => Ok(await runs.UpdateDefectAsync(projectKey, id, defectId, request, Actor, ct)));

    /// <summary>Attaches a file (multipart field "file") as evidence; optional "step" and "label" fields.</summary>
    [HttpPost("{id:guid}/attachments")]
    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    [RequestSizeLimit(ManualTestRunService.MaxAttachmentBytes + 64 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = ManualTestRunService.MaxAttachmentBytes + 64 * 1024)]
    public Task<ActionResult> Attach(Guid id, [FromQuery] string projectKey, [FromQuery] int baseRevision, [FromForm] IFormFile? file, [FromForm] int? step, [FromForm] string? label,
        CancellationToken ct) =>
        Run(async () =>
        {
            if (file is null) return BadRequest(new { code = "attachment-missing", message = "Choose a file to attach." });
            if (file.Length > ManualTestRunService.MaxAttachmentBytes)
                return StatusCode(413, new { code = "attachment-too-large", message = $"A file can be at most {ManualTestRunService.MaxAttachmentBytes / (1024 * 1024)} MB." });
            using var buffer = new MemoryStream();
            await file.CopyToAsync(buffer, ct);
            return Ok(await runs.AttachAsync(projectKey, id, baseRevision, step, label, file.FileName, buffer.ToArray(), Actor, ct));
        });

    /// <summary>Downloads an attachment (always as a download, never rendered by the browser as a page).</summary>
    [HttpGet("{id:guid}/attachments/{attachmentId:guid}")]
    public Task<ActionResult> Attachment(Guid id, Guid attachmentId, [FromQuery] string projectKey, CancellationToken ct) =>
        Run(async () =>
        {
            var a = await runs.AttachmentAsync(projectKey, id, attachmentId, ct);
            Response.Headers["X-Content-Type-Options"] = "nosniff";
            return File(a.Content, a.ContentType, a.FileName);
        });

    private static async Task<ActionResult> Run(Func<Task<ActionResult>> action)
    {
        try { return await action(); }
        catch (TestPlanException e) { return new ObjectResult(new { code = e.Code, message = e.Message }) { StatusCode = e.Status }; }
    }
}
