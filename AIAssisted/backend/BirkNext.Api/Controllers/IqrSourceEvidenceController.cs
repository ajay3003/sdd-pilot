using BirkNext.Api.Services.Integrations;
using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.Integrations;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

/// <summary>
/// Source Analysis: the single source-ingestion entry point (ZIP upload → immutable snapshot). Reviews read snapshots through the shared
/// source-evidence provider; none of them uploads source. The per-integration upload Integration Quality Review once had is removed — the
/// snapshots it created stay in the store and in historical IQR results. Source Analysis snapshots need no Target Environment: a target is
/// runtime context, so these endpoints accept a blank environmentId and never scope source snapshots by it.
/// </summary>
[ApiController]
[Route("api/integration-review/source")]
[Route("api/source-analysis")]
public sealed class IqrSourceEvidenceController(IqrSourceStore store, BirkNext.Api.Services.SourceAnalysis.IReviewSourceEvidenceProvider sources,
    ILogger<IqrSourceEvidenceController> logger) : ControllerBase
{
    /// <summary>Integration Quality Review's view of Source Analysis: snapshots for binding one to an integration (read-only metadata).</summary>
    [HttpGet("scope")]
    public async Task<ActionResult<BirkNext.SourceEvidence.ReviewSourceOptions>> Scope([FromQuery] string? environmentId, [FromQuery] Guid? primary, CancellationToken ct)
    {
        var snapshots = sources.SourceAnalysisEnabled ? await sources.ListAsync(environmentId ?? "", ct) : [];
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
    public async Task<ActionResult<BirkNext.SourceDomains.SourceInfrastructureSuggestion>> InfrastructureSuggestions([FromQuery] string? environmentId, [FromQuery] BirkNext.SourceDomains.InfrastructureResourceKind kind,
        [FromQuery] string? field, [FromQuery] string? configured, [FromQuery] string? targetEnvironment, [FromQuery] string? parent, [FromQuery] Guid? snapshotId, CancellationToken ct)
    {
        var snapshots = sources.SourceAnalysisEnabled ? await sources.ListAsync(environmentId ?? "", ct) : [];
        return Ok(BirkNext.Api.Services.SourceAnalysis.SourceInfrastructureSuggestions.Suggest(snapshots, snapshotId, kind, field ?? kind.ToString(), configured,
            targetEnvironment ?? environmentId ?? "", parent, sources.SourceAnalysisEnabled));
    }

    /// <summary>
    /// The workspace's Source Analysis snapshots (newest first) whether or not a Target Environment is selected; with a target, also the
    /// legacy per-integration snapshots an earlier version stored for it, so their history stays readable.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<IqrSourceSnapshot>>> List([FromQuery] string? environmentId, CancellationToken ct) =>
        Ok(await store.ListAsync(environmentId ?? "", ct));

    [HttpPost("snapshots")]
    [RequestSizeLimit(SourceArchiveUpload.RequestLimit)]
    [RequestFormLimits(MultipartBodyLengthLimit = SourceArchiveUpload.RequestLimit)]
    // environmentId is optional and ignored for Source Analysis snapshots: source needs no Target Environment. Kept on the route so
    // existing clients that still send it are not rejected.
    public Task<IActionResult> AnalyzeSourceSnapshot([FromQuery] string? environmentId, CancellationToken ct) =>
        AnalyzeArchive(environmentId, IqrSourceStore.SourceAnalysisOwner, ct);

    private async Task<IActionResult> AnalyzeArchive(string? environmentId, string integrationId, CancellationToken ct)
    {
        // One upload reader for Source Analysis and Project Import: bytes from the uploaded stream only, never the client path.
        var upload = await SourceArchiveUpload.ReadAsync(Request, ct);
        if (!upload.IsRead) return Failure(upload.StatusCode, upload.Failure!);
        var bytes = upload.Bytes!;
        var fileName = upload.FileName!;
        var validation = IqrSourceArchiveReader.ReadDetailed(fileName, bytes, ct);
        if (!validation.IsValid) return Failure(StatusCodes.Status400BadRequest, validation.Failure!, bytes.Length, validation.EntryCount);

        IqrSourceSnapshot snapshot;
        try { snapshot = await store.AnalyzeValidatedAsync(environmentId, integrationId, fileName, bytes, validation.Workspace!, ct); }
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
