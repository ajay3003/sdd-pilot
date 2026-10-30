using BirkNext.Api.Services.DeliveryReporting;
using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.DeliveryReporting;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

/// <summary>
/// Delivery Reporting: reporting projects, snapshot import (its own ZIP upload; no Import Project), snapshots and weekly reports. Every
/// endpoint reads and writes the delivery_reporting schema only. Upload failures use the Source Analysis upload contract
/// ({ code, stage, message, entryPath, actual, limit }) because the same bounded reader validates the archive.
/// </summary>
[ApiController]
[Route("api/delivery-reporting")]
public sealed class DeliveryReportingController(DeliverySnapshotStore store, DeliveryReportingImportService imports, WeeklyReportService reports) : ControllerBase
{
    [HttpGet("projects")]
    public async Task<ActionResult<List<DeliveryProject>>> Projects(CancellationToken ct) => await store.ProjectsAsync(ct);

    [HttpPost("projects")]
    public async Task<IActionResult> CreateProject(CreateDeliveryProjectRequest request, CancellationToken ct)
    {
        var (project, error) = await store.CreateProjectAsync(request, ct);
        return project is null ? BadRequest(new { message = error }) : Ok(project);
    }

    /// <summary>Validates and parses one ZIP and returns what it holds. Nothing is stored until the import is confirmed.</summary>
    [HttpPost("imports/preview")]
    [RequestSizeLimit(SourceArchiveUpload.RequestLimit)]
    [RequestFormLimits(MultipartBodyLengthLimit = SourceArchiveUpload.RequestLimit)]
    public async Task<IActionResult> Preview([FromQuery] Guid? projectId, CancellationToken ct)
    {
        var upload = await SourceArchiveUpload.ReadAsync(Request, ct);
        if (!upload.IsRead) return Failure(upload.StatusCode, upload.Failure!);
        var outcome = await imports.PreviewAsync(projectId, upload.FileName!, upload.Bytes!, ct);
        return outcome.Failure is { } failure ? Failure(StatusCodes.Status400BadRequest, failure) : Ok(outcome.Preview);
    }

    [HttpPost("imports/{stagingId:guid}/commit")]
    public async Task<IActionResult> Commit(Guid stagingId, DeliveryImportCommitRequest request, CancellationToken ct)
    {
        var outcome = await imports.CommitAsync(stagingId, request, ct);
        return StatusCode(outcome.StatusCode, outcome.Result);
    }

    [HttpDelete("imports/{stagingId:guid}")]
    public IActionResult Discard(Guid stagingId)
    {
        imports.Discard(stagingId);
        return NoContent();
    }

    [HttpGet("projects/{projectId:guid}/snapshots")]
    public async Task<ActionResult<List<DeliverySnapshotSummary>>> Snapshots(Guid projectId, CancellationToken ct) => await store.SnapshotsAsync(projectId, ct);

    [HttpGet("projects/{projectId:guid}/snapshots/{snapshotId:guid}")]
    public async Task<IActionResult> Snapshot(Guid projectId, Guid snapshotId, CancellationToken ct) =>
        await store.SnapshotAsync(projectId, snapshotId, ct) is { } detail ? Ok(detail) : NotFound();

    [HttpPatch("projects/{projectId:guid}/snapshots/{snapshotId:guid}")]
    public async Task<IActionResult> UpdateSnapshot(Guid projectId, Guid snapshotId, UpdateDeliverySnapshotRequest request, CancellationToken ct) =>
        await store.UpdateSnapshotAsync(projectId, snapshotId, request, ct) is { } summary ? Ok(summary) : NotFound();

    [HttpDelete("projects/{projectId:guid}/snapshots/{snapshotId:guid}")]
    public async Task<IActionResult> DeleteSnapshot(Guid projectId, Guid snapshotId, CancellationToken ct) =>
        await store.DeleteSnapshotAsync(projectId, snapshotId, ct) ? NoContent() : NotFound();

    [HttpGet("projects/{projectId:guid}/snapshots/{snapshotId:guid}/baseline-suggestion")]
    public async Task<IActionResult> SuggestBaseline(Guid projectId, Guid snapshotId, CancellationToken ct) =>
        await store.SuggestBaselineAsync(projectId, snapshotId, reports.Zone, ct) is { } suggestion ? Ok(suggestion) : NotFound();

    [HttpGet("projects/{projectId:guid}/reports")]
    public async Task<ActionResult<List<DeliveryReportSummaryRow>>> Reports(Guid projectId, CancellationToken ct) => await reports.ListAsync(projectId, ct);

    [HttpPost("reports")]
    public async Task<IActionResult> Generate(GenerateWeeklyReportRequest request, CancellationToken ct)
    {
        var outcome = await reports.GenerateAsync(request, ct);
        return outcome.Report is null ? StatusCode(outcome.StatusCode, new { message = outcome.Error }) : Ok(outcome.Report);
    }

    [HttpGet("projects/{projectId:guid}/reports/{reportId:guid}")]
    public async Task<IActionResult> Report(Guid projectId, Guid reportId, CancellationToken ct) =>
        await reports.GetAsync(projectId, reportId, ct) is { } report ? Ok(report) : NotFound();

    /// <summary>Exports the stored report version (Markdown or HTML). The evidence appendix and unchanged stories are opt-in.</summary>
    [HttpGet("projects/{projectId:guid}/reports/{reportId:guid}/export")]
    public async Task<IActionResult> Export(Guid projectId, Guid reportId, [FromQuery] DeliveryExportFormat format = DeliveryExportFormat.Markdown,
        [FromQuery] bool appendix = false, [FromQuery] bool unchanged = false, CancellationToken ct = default) =>
        await reports.ExportAsync(projectId, reportId, format, new(appendix, unchanged), ct) is { } export ? Ok(export) : NotFound();

    [HttpDelete("projects/{projectId:guid}/reports/{reportId:guid}")]
    public async Task<IActionResult> DeleteReport(Guid projectId, Guid reportId, CancellationToken ct) =>
        await reports.DeleteAsync(projectId, reportId, ct) ? NoContent() : NotFound();

    private ObjectResult Failure(int status, SourceArchiveValidationFailure failure) =>
        StatusCode(status, new { code = failure.Code, stage = failure.Stage, message = failure.Message, entryPath = failure.EntryPath, actual = failure.Actual, limit = failure.Limit });
}
