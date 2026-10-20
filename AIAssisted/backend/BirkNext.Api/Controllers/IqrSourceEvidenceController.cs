using BirkNext.Api.Services.Integrations;
using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.Integrations;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

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
    private static readonly JsonSerializerOptions SchemaJson = new(JsonSerializerDefaults.Web);

    /// <summary>Small snapshot index for Schema Studio selectors; source evidence payloads stay on the server.</summary>
    [HttpGet("schema-index")]
    public async Task<ActionResult<SourceSchemaSnapshotIndexDto>> SchemaIndex(CancellationToken ct)
    {
        var (items, isTruncated) = await store.ListSchemaSnapshotIndexAsync(ct: ct);
        return Ok(new SourceSchemaSnapshotIndexDto(items, isTruncated));
    }

    /// <summary>Loads the bounded XSD-only artifact captured for one exact Source Analysis snapshot.</summary>
    [HttpGet("{snapshotId:guid}/schemas")]
    public async Task<ActionResult<SourceSchemaPackageDto>> Schemas(Guid snapshotId, CancellationToken ct)
    {
        var snapshot = await store.FindSourceAnalysisAsync(snapshotId, ct);
        if (snapshot is null) return NotFound(new { error = "Source Analysis snapshot was not found." });
        var artifact = await store.FindSchemaArtifactAsync(snapshotId, ct);
        if (artifact is null)
            return Ok(new SourceSchemaPackageDto(snapshot.Id, snapshot.Archive.Sha256, snapshot.Archive.FileName,
                snapshot.AnalyzedAt, false, 0, [], false,
                "XSD content was not retained in this snapshot. Reimport the source archive in Source Analysis to create a new snapshot with reusable XSD artifacts.",
                snapshot.ProjectImport?.ImportId, SourceProjectName(snapshot)));
        if (!string.Equals(artifact.SnapshotFingerprint, snapshot.Archive.Sha256, StringComparison.Ordinal))
            return Conflict(new { error = "The stored schema artifact does not match this source snapshot." });
        if (!string.Equals(artifact.ProjectImportId, snapshot.ProjectImport?.ImportId, StringComparison.Ordinal))
            return Conflict(new { error = "The stored schema artifact does not match this source project's import identity." });
        try
        {
            var files = JsonSerializer.Deserialize<List<SourceSchemaFileDto>>(artifact.PackageJson, SchemaJson) ?? [];
            return Ok(new SourceSchemaPackageDto(snapshot.Id, snapshot.Archive.Sha256, snapshot.Archive.FileName,
                snapshot.AnalyzedAt, artifact.IsComplete, artifact.OmittedFiles, files,
                ProjectImportId: artifact.ProjectImportId, SourceProjectName: SourceProjectName(snapshot)));
        }
        catch (JsonException)
        {
            logger.LogError("Stored schema artifact could not be read for snapshot {SnapshotId}", snapshotId);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "The stored schema artifact is unavailable." });
        }
    }

    private static string? SourceProjectName(BirkNext.Integrations.IqrSourceSnapshot snapshot) =>
        !string.IsNullOrWhiteSpace(snapshot.Repository?.DisplayName) ? snapshot.Repository.DisplayName
        : snapshot.Projects.FirstOrDefault(project => !string.IsNullOrWhiteSpace(project.Name))?.Name;

    /// <summary>Environment-specific values the workspace's snapshots declare (safe values only, with provenance) for setup forms to offer for the
    /// target environment of the same kind. Declared in source; never configured, deployed or runtime verified by being listed.</summary>
    [HttpGet("environment-values")]
    public async Task<ActionResult<BirkNext.SourceDomains.SourceEnvironmentValuesView>> EnvironmentValues(CancellationToken ct)
    {
        if (!sources.SourceAnalysisEnabled)
            return Ok(new BirkNext.SourceDomains.SourceEnvironmentValuesView { Limitations = [BirkNext.Api.Services.SourceAnalysis.ReviewSourceEvidenceProvider.Disabled] });
        var snapshots = await sources.ListAsync("", ct);
        return Ok(new BirkNext.SourceDomains.SourceEnvironmentValuesView
        {
            Candidates = BirkNext.SourceDomains.SourceEnvironmentValueExtractor.Candidates(snapshots),
            Limitations = ["Declared in source: not configured, deployed, reachable or runtime verified.",
                "Only safe values are listed; secrets, connection strings, placeholders and loopback (developer machine) URLs are never offered."],
        });
    }

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
        catch (SourceSnapshotPersistenceException failure)
        {
            return Failure(StatusCodes.Status500InternalServerError,
                new("SOURCE_SNAPSHOT_SAVE_FAILED", "persistence", $"The source snapshot could not be saved. {failure.Reason}"), bytes.Length, validation.EntryCount);
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

public sealed record SourceSchemaFileDto(string Path, string Text, string Fingerprint);
public sealed record SourceSchemaPackageDto(Guid SnapshotId, string SnapshotFingerprint, string SourceArchiveName,
    DateTimeOffset AnalyzedAt, bool IsComplete, int OmittedFiles, IReadOnlyList<SourceSchemaFileDto> Files,
    bool IsAvailable = true, string? UnavailableReason = null, string? ProjectImportId = null, string? SourceProjectName = null);
public sealed record SourceSchemaSnapshotIndexDto(IReadOnlyList<SourceSchemaSnapshotIndexItem> Snapshots, bool IsTruncated);
