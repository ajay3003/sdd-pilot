using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.Api.Services.ProjectImport;
using BirkNext.ProjectImport;
using Microsoft.AspNetCore.Mvc;

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

    /// <summary>The environment id is the active Target Environment, which owns source snapshots. It is optional: without it the documents
    /// still import and the source part is reported as not created (retryable while staged).</summary>
    [HttpPost("{stagingId:guid}/commit")]
    public async Task<ActionResult<ProjectImportCommitResult>> Commit(Guid stagingId, [FromQuery] string? environmentId, CancellationToken ct)
    {
        var result = await imports.CommitAsync(stagingId, environmentId, ct);
        return result is null
            ? Failure(StatusCodes.Status404NotFound, new("IMPORT_STAGING_EXPIRED", "upload", "The staged archive is no longer available (it expired or was already imported). Choose the ZIP again."))
            : Ok(result);
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
