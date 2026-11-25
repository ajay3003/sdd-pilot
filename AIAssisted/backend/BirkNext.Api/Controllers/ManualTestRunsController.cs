using BirkNext.Api.Services.ActiveEventTesting;
using BirkNext.Api.Services.TestPlanning;
using BirkNext.TestPlanning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

/// <summary>
/// Manual test runs of saved test cases — «Testgjennomføring i BirkNext» (restored 2026-11-25 as an OPTIONAL capability; Azure DevOps Test Plans
/// remains the default place to execute). Recording a run is possible only when <see cref="ManualExecutionSettings.Key"/> is true (default false);
/// reading stays possible, so history recorded while it was on never disappears. Every call names the project; a run of another project is never
/// returned or changed (404). Recording needs the same write permission as editing a test plan. Nothing is sent to Azure DevOps.
/// </summary>
[ApiController]
[Route("api/manual-test-runs")]
public sealed class ManualTestRunsController(ManualTestRunService runs, IConfiguration configuration) : ControllerBase
{
    private bool Enabled => ManualExecutionSettings.Enabled(configuration[ManualExecutionSettings.Key]);

    /// <summary>Whether testers can execute in BirkNext (the UI shows «Start test» only then).</summary>
    [HttpGet("availability")]
    public ActionResult Availability() => Ok(new ManualExecutionAvailability(Enabled));

    /// <summary>Writes are refused while execution in BirkNext is off (reads stay available).</summary>
    private Task<ActionResult> Write(Func<Task<ActionResult>> action) => Enabled ? Run(action)
        : Task.FromResult<ActionResult>(new ObjectResult(new { code = "manual-execution-off", message = ManualExecutionSettings.OffMessage }) { StatusCode = 409 });
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
    public Task<ActionResult> Start([FromBody] StartManualRunRequest request, CancellationToken ct) => Write(async () => Ok(await runs.StartAsync(request, Actor, ct)));

    [HttpPut("{id:guid}")]
    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    public Task<ActionResult> Save(Guid id, [FromQuery] string projectKey, [FromBody] SaveManualRunRequest request, CancellationToken ct) =>
        Write(async () => Ok(await runs.SaveAsync(projectKey, id, request, Actor, ct)));

    [HttpPost("{id:guid}/complete")]
    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    public Task<ActionResult> Complete(Guid id, [FromQuery] string projectKey, [FromBody] CompleteManualRunRequest request, CancellationToken ct) =>
        Write(async () => Ok(await runs.CompleteAsync(projectKey, id, request, Actor, ct)));

    [HttpPost("{id:guid}/abandon")]
    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    public Task<ActionResult> Abandon(Guid id, [FromQuery] string projectKey, [FromBody] AbandonManualRunRequest request, CancellationToken ct) =>
        Write(async () => Ok(await runs.AbandonAsync(projectKey, id, request, Actor, ct)));

    /// <summary>Every defect of the project's runs, with the run it was found in.</summary>
    [HttpGet("defects")]
    public Task<ActionResult> Defects([FromQuery] string projectKey, CancellationToken ct) => Run(async () => Ok(await runs.DefectsAsync(projectKey, ct)));

    /// <summary>Follows up a defect of a completed or stopped run (status, work item reference, note, retest run). The run's results stay final.</summary>
    [HttpPost("{id:guid}/defects/{defectId}")]
    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    public Task<ActionResult> UpdateDefect(Guid id, string defectId, [FromQuery] string projectKey, [FromBody] UpdateManualDefectRequest request, CancellationToken ct) =>
        Write(async () => Ok(await runs.UpdateDefectAsync(projectKey, id, defectId, request, Actor, ct)));

    /// <summary>Attaches a file (multipart field "file") as evidence; optional "step" and "label" fields.</summary>
    [HttpPost("{id:guid}/attachments")]
    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    [RequestSizeLimit(ManualTestRunService.MaxAttachmentBytes + 64 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = ManualTestRunService.MaxAttachmentBytes + 64 * 1024)]
    public Task<ActionResult> Attach(Guid id, [FromQuery] string projectKey, [FromQuery] int baseRevision, [FromForm] IFormFile? file, [FromForm] int? step, [FromForm] string? label,
        CancellationToken ct) =>
        Write(async () =>
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
            // Never rendered or executed by the browser: a download (Content-Disposition: attachment), no sniffing, a sandboxed empty policy.
            Response.Headers["X-Content-Type-Options"] = "nosniff";
            Response.Headers["Content-Security-Policy"] = "default-src 'none'; sandbox";
            Response.Headers["Cache-Control"] = "no-store";
            Response.Headers["Cross-Origin-Resource-Policy"] = "same-origin";
            return File(a.Content, a.ContentType, a.FileName);
        });

    /// <summary>The run's attached files: name, type, size, SHA-256, scan state and withdrawal (no content).</summary>
    [HttpGet("{id:guid}/attachments")]
    public Task<ActionResult> Attachments(Guid id, [FromQuery] string projectKey, CancellationToken ct) =>
        Run(async () => Ok(await runs.AttachmentsAsync(projectKey, id, ct)));

    /// <summary>Withdraws a file at a person's request: its content is removed, its record (name, size, SHA-256, who/when/why) stays.</summary>
    [HttpPost("{id:guid}/attachments/{attachmentId:guid}/withdraw")]
    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    public Task<ActionResult> Withdraw(Guid id, Guid attachmentId, [FromQuery] string projectKey, [FromBody] WithdrawAttachmentRequest request, CancellationToken ct) =>
        Write(async () => Ok(await runs.WithdrawAttachmentAsync(projectKey, id, attachmentId, request.Reason, Actor, ct)));

    private static async Task<ActionResult> Run(Func<Task<ActionResult>> action)
    {
        try { return await action(); }
        catch (TestPlanException e) { return new ObjectResult(new { code = e.Code, message = e.Message }) { StatusCode = e.Status }; }
    }
}
