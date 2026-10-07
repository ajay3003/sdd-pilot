using BirkNext.Api.Services.Integrations;
using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.Integrations;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

/// <summary>
/// Source Analysis: the single source-ingestion entry point (ZIP upload → immutable snapshot). Reviews read snapshots through the shared
/// source-evidence provider; none of them uploads source. The per-integration upload Integration Quality Review once had is removed — the
/// snapshots it created stay in the store and in historical IQR results.
/// </summary>
[ApiController]
[Route("api/integration-review/source")]
[Route("api/source-analysis")]
public sealed class IqrSourceEvidenceController(IqrSourceStore store, BirkNext.Api.Services.SourceAnalysis.IReviewSourceEvidenceProvider sources,
    ILogger<IqrSourceEvidenceController> logger) : ControllerBase
{
    /// <summary>Integration Quality Review's view of Source Analysis: snapshots for binding one to an integration (read-only metadata).</summary>
    [HttpGet("scope")]
    public async Task<ActionResult<BirkNext.SourceEvidence.ReviewSourceOptions>> Scope([FromQuery] string environmentId, [FromQuery] Guid? primary, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(environmentId)) return BadRequest("environmentId is required.");
        var snapshots = sources.SourceAnalysisEnabled ? await sources.ListAsync(environmentId, ct) : [];
        return Ok(BirkNext.Api.Services.SourceAnalysis.ReviewSourceEvidenceProvider.Options(sources.SourceAnalysisEnabled, snapshots, IqrEvidence,
            primary is { } p ? new BirkNext.SourceEvidence.ReviewSourceScopeRequest { PrimarySnapshotId = p } : null, _ => []));
    }

    /// <summary>What a snapshot holds for Integration Quality Review (source-defined behaviour only; never runtime processing).</summary>
    public static BirkNext.Api.Services.SourceAnalysis.ConsumerSourceEvidence IqrEvidence(IqrSourceSnapshot s) =>
        new(true, s.Rules.Count == 0 && s.IntegrationPath is null ? "No integration implementation evidence was found in this snapshot." : null,
            $"{s.Rules.Count} implementation rule(s) · {s.IntegrationPath?.Hops.Count ?? 0} integration path hop(s)");

    /// <summary>
    /// Source suggestions for ONE configured field: what the Infrastructure evidence declares for a resource kind (scoped to the target environment
    /// when source names it), compared with the configured value. Read-only — a suggestion is applied only by a person saving the form.
    /// </summary>
    [HttpGet("infrastructure-suggestions")]
    public async Task<ActionResult<BirkNext.SourceDomains.SourceInfrastructureSuggestion>> InfrastructureSuggestions([FromQuery] string environmentId, [FromQuery] BirkNext.SourceDomains.InfrastructureResourceKind kind,
        [FromQuery] string? field, [FromQuery] string? configured, [FromQuery] string? targetEnvironment, [FromQuery] string? parent, [FromQuery] Guid? snapshotId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(environmentId)) return BadRequest("environmentId is required.");
        var snapshots = sources.SourceAnalysisEnabled ? await sources.ListAsync(environmentId, ct) : [];
        return Ok(BirkNext.Api.Services.SourceAnalysis.SourceInfrastructureSuggestions.Suggest(snapshots, snapshotId, kind, field ?? kind.ToString(), configured,
            targetEnvironment ?? environmentId, parent, sources.SourceAnalysisEnabled));
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<IqrSourceSnapshot>>> List([FromQuery] string environmentId, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(environmentId) ? BadRequest("environmentId is required.") : Ok(await store.ListAsync(environmentId, ct));

    [HttpPost("snapshots")]
    [RequestSizeLimit(IqrSourceArchiveReader.MaxArchiveBytes + 64 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = IqrSourceArchiveReader.MaxArchiveBytes + 64 * 1024)]
    public Task<IActionResult> AnalyzeSourceSnapshot([FromQuery] string environmentId, CancellationToken ct) =>
        AnalyzeArchive(environmentId, IqrSourceStore.SourceAnalysisOwner, ct);

    private async Task<IActionResult> AnalyzeArchive(string environmentId, string integrationId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(environmentId)) return Failure(StatusCodes.Status400BadRequest,
            new("NO_ACTIVE_ENVIRONMENT", "prerequisite", "Select or create a Target Environment before uploading source."));
        if (!Request.HasFormContentType) return Failure(StatusCodes.Status400BadRequest,
            new("UPLOAD_MULTIPART_REQUIRED", "upload", "Upload one ZIP file using multipart form data."));
        IFormCollection form;
        try { form = await Request.ReadFormAsync(ct); }
        catch (InvalidDataException)
        {
            var tooLarge = Request.ContentLength > IqrSourceArchiveReader.MaxArchiveBytes + 64 * 1024;
            var failure = tooLarge
                ? new SourceArchiveValidationFailure("ARCHIVE_TOO_LARGE", "upload", "Upload exceeds the 50 MB compressed archive limit.", Actual: Request.ContentLength, Limit: IqrSourceArchiveReader.MaxArchiveBytes)
                : new SourceArchiveValidationFailure("UPLOAD_INVALID_FORM", "upload", "The ZIP upload could not be read. Choose the file again and retry.");
            return Failure(tooLarge ? StatusCodes.Status413PayloadTooLarge : StatusCodes.Status400BadRequest, failure);
        }
        if (form.Files.Count != 1) return Failure(StatusCodes.Status400BadRequest,
            new("UPLOAD_FILE_COUNT_INVALID", "upload", "Choose exactly one ZIP archive to upload."));
        var file = form.Files[0];
        if (file.Length == 0) return Failure(StatusCodes.Status400BadRequest,
            new("ARCHIVE_EMPTY_UPLOAD", "upload", "The uploaded file is empty."));
        if (file.Length > IqrSourceArchiveReader.MaxArchiveBytes) return Failure(StatusCodes.Status413PayloadTooLarge,
            new("ARCHIVE_TOO_LARGE", "upload", "Upload exceeds the 50 MB compressed archive limit.", Actual: file.Length, Limit: IqrSourceArchiveReader.MaxArchiveBytes));
        using var stream = file.OpenReadStream();
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + count > IqrSourceArchiveReader.MaxArchiveBytes) return Failure(StatusCodes.Status413PayloadTooLarge,
                new("ARCHIVE_TOO_LARGE", "upload", "Upload exceeds the 50 MB compressed archive limit.", Actual: buffer.Length + count, Limit: IqrSourceArchiveReader.MaxArchiveBytes));
            await buffer.WriteAsync(chunk.AsMemory(0, count), ct);
        }
        var bytes = buffer.ToArray();
        var validation = IqrSourceArchiveReader.ReadDetailed(file.FileName, bytes, ct);
        if (!validation.IsValid) return Failure(StatusCodes.Status400BadRequest, validation.Failure!, bytes.Length, validation.EntryCount);

        IqrSourceSnapshot snapshot;
        try { snapshot = await store.AnalyzeValidatedAsync(environmentId, integrationId, file.FileName, bytes, validation.Workspace!, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (SourceSnapshotPersistenceException)
        {
            return Failure(StatusCodes.Status500InternalServerError,
                new("SOURCE_SNAPSHOT_SAVE_FAILED", "persistence", "The source snapshot could not be saved. No new snapshot is available."), bytes.Length, validation.EntryCount);
        }
        catch (Exception ex)
        {
            logger.LogError("Source analysis failed. Code {Code}; stage {Stage}; archive size {ArchiveBytes}; entries {EntryCount}; exception type {ExceptionType}; trace {TraceId}",
                "SOURCE_ANALYSIS_FAILED", "analysis", bytes.Length, validation.EntryCount, ex.GetType().Name, HttpContext.TraceIdentifier);
            return Failure(StatusCodes.Status422UnprocessableEntity,
                new("SOURCE_ANALYSIS_FAILED", "analysis", "The ZIP archive passed validation, but source analysis could not complete. Retry or choose a smaller archive."), bytes.Length, validation.EntryCount);
        }

        logger.LogInformation("Source snapshot created. Archive size {ArchiveBytes}; entries {EntryCount}; stage {Stage}; trace {TraceId}",
            bytes.Length, validation.EntryCount, "complete", HttpContext.TraceIdentifier);
        return Ok(snapshot);
    }

    private IActionResult Failure(int status, SourceArchiveValidationFailure failure, long? archiveBytes = null, long? entryCount = null)
    {
        logger.LogWarning("Source archive rejected. Code {Code}; stage {Stage}; archive size {ArchiveBytes}; entries {EntryCount}; safe path {EntryPath}; trace {TraceId}",
            failure.Code, failure.Stage, archiveBytes ?? failure.Actual, entryCount, failure.EntryPath, HttpContext.TraceIdentifier);
        return StatusCode(status, new { code = failure.Code, stage = failure.Stage, message = failure.Message,
            entryPath = failure.EntryPath, actual = failure.Actual, limit = failure.Limit });
    }
}
