using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.Api.Services.ProjectImport;
using BirkNext.ProjectImport;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace BirkNext.Api.Controllers;

/// <summary>
/// Project Import: upload one project ZIP once. Preview validates and stages it and reports its documents and source; commit creates the
/// Source Analysis snapshot from the same staged archive. Documents are returned to the frontend, which owns the artifact repository.
/// Failure bodies use the Source Analysis upload contract ({ code, stage, message, entryPath, actual, limit }).
/// </summary>
[ApiController]
[Route("api/project-import")]
public sealed class ProjectImportController(ProjectImportService imports, ILogger<ProjectImportController> logger) : ControllerBase
{
    [HttpPost("preview")]
    [RequestSizeLimit(SourceArchiveUpload.RequestLimit)]
    [RequestFormLimits(MultipartBodyLengthLimit = SourceArchiveUpload.RequestLimit)]
    public async Task<IActionResult> Preview(CancellationToken ct)
    {
        var upload = await SourceArchiveUpload.ReadAsync(Request, ct);
        if (!upload.IsRead) return Failure(upload.StatusCode, upload.Failure!);
        var result = imports.Preview(upload.FileName!, upload.Bytes!, ct);
        if (result.Failure is { } failure) return Failure(StatusCodes.Status400BadRequest, failure, upload.Bytes!.Length);
        return Ok(result.Preview);
    }

    /// <summary>Creates (or reuses) the source snapshot of a staged archive. No Target Environment is needed: a target is runtime context and
    /// never owns source snapshots. An environmentId query value sent by an older client is ignored.</summary>
    [HttpPost("{stagingId:guid}/commit")]
    public async Task<IActionResult> Commit(Guid stagingId, CancellationToken ct)
    {
        // Preserve the existing JSON response for older clients. The current frontend opts into the streamed event representation.
        if (!Request.Headers.Accept.ToString().Contains("application/x-ndjson", StringComparison.OrdinalIgnoreCase))
        {
            var legacyResult = await imports.CommitAsync(stagingId, ct);
            return legacyResult is null
                ? Failure(StatusCodes.Status404NotFound, new("IMPORT_STAGING_EXPIRED", "upload", "Preview expired. Re-import the project: the staged archive is no longer available (it expired after 30 minutes or was discarded). Choose the ZIP again."))
                : Ok(legacyResult);
        }

        var sequence = 0;
        var currentStage = "Preparing the staged project";
        var stageClock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var result = await imports.CommitAsync(stagingId, ct, async update =>
            {
                if (!string.Equals(currentStage, update.Stage, StringComparison.Ordinal))
                {
                    logger.LogInformation("Project import stage completed. Stage {Stage}; elapsed {ElapsedMs} ms; trace {TraceId}", currentStage, stageClock.ElapsedMilliseconds, HttpContext.TraceIdentifier);
                    currentStage = update.Stage;
                    stageClock.Restart();
                }
                await WriteEventAsync(update with { Sequence = ++sequence }, ct);
            });
            if (result is null)
                return Failure(StatusCodes.Status404NotFound, new("IMPORT_STAGING_EXPIRED", "upload", "Preview expired. Re-import the project: the staged archive is no longer available (it expired after 30 minutes or was discarded). Choose the ZIP again."));

            logger.LogInformation("Project import stage completed. Stage {Stage}; elapsed {ElapsedMs} ms; trace {TraceId}", currentStage, stageClock.ElapsedMilliseconds, HttpContext.TraceIdentifier);
            await WriteEventAsync(new ProjectImportCommitEvent
            {
                Kind = ProjectImportCommitEventKind.Completed, Sequence = ++sequence, Stage = "Import result received",
                Detail = "Source Analysis returned its result. Project documents are activated in the workspace next.", Result = result,
            }, ct);
            return new EmptyResult();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The request owner disconnected/cancelled. Source Analysis receives the same token and checks it between work units.
            throw;
        }
        catch (Exception ex)
        {
            var correlationId = HttpContext.TraceIdentifier;
            logger.LogError("Project import commit failed. Code {Code}; stage {Stage}; exception type {ExceptionType}; trace {TraceId}",
                "PROJECT_IMPORT_FAILED", currentStage, ex.GetType().Name, correlationId);
            if (!Response.HasStarted)
                return Failure(StatusCodes.Status500InternalServerError, new("PROJECT_IMPORT_FAILED", "commit", "The import could not be completed. Retry the import; the staged archive may still be available."));
            await WriteEventAsync(new ProjectImportCommitEvent
            {
                Kind = ProjectImportCommitEventKind.Failed, Sequence = ++sequence, Stage = currentStage,
                Detail = "The import stopped before the backend returned a result.", ErrorCode = "PROJECT_IMPORT_FAILED",
                ErrorStage = currentStage, ErrorMessage = "The import could not be completed. Retry the import; the staged archive may still be available.",
                CorrelationId = correlationId,
            }, ct);
            return new EmptyResult();
        }
    }

    private async Task WriteEventAsync(ProjectImportCommitEvent update, CancellationToken ct)
    {
        if (!Response.HasStarted)
        {
            Response.StatusCode = StatusCodes.Status200OK;
            Response.ContentType = "application/x-ndjson; charset=utf-8";
            Response.Headers.CacheControl = "no-store, no-transform";
            Response.Headers["X-Accel-Buffering"] = "no";
            await Response.StartAsync(ct);
        }
        await Response.WriteAsync(JsonSerializer.Serialize(update, new JsonSerializerOptions(JsonSerializerDefaults.Web)) + "\n", ct);
        await Response.Body.FlushAsync(ct);
    }

    [HttpDelete("{stagingId:guid}")]
    public IActionResult Discard(Guid stagingId)
    {
        imports.Discard(stagingId);
        return NoContent();
    }

    private ObjectResult Failure(int status, SourceArchiveValidationFailure failure, long? archiveBytes = null)
    {
        logger.LogWarning("Project import rejected. Code {Code}; stage {Stage}; archive size {ArchiveBytes}; safe path {EntryPath}; trace {TraceId}",
            failure.Code, failure.Stage, archiveBytes ?? failure.Actual, failure.EntryPath, HttpContext.TraceIdentifier);
        return StatusCode(status, new { code = failure.Code, stage = failure.Stage, message = failure.Message,
            entryPath = failure.EntryPath, actual = failure.Actual, limit = failure.Limit });
    }
}
