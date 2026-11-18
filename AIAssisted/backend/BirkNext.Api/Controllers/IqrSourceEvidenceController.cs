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
            var files = SourceSchemaUsage.Attach(JsonSerializer.Deserialize<List<SourceSchemaFileDto>>(artifact.PackageJson, SchemaJson) ?? [], snapshot);
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
    /// <summary>The stored snapshots as summaries: identity, provenance, status, counts and which evidence each holds. A full snapshot is
    /// megabytes; it is fetched by id when it is opened (GET {snapshotId}).</summary>
    /// Summary-first: Source Analysis snapshots come from the Snapshot Registry's stored summaries (no payload is loaded); only legacy
    /// per-integration snapshots of the environment are still read from their store. Falls back to the legacy path when the registry cannot answer.
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<IqrSourceSnapshotSummary>>> List([FromQuery] string? environmentId, CancellationToken ct,
        [FromServices] Services.Snapshots.SnapshotRegistryService? registry = null)
    {
        IReadOnlyList<IqrSourceSnapshotSummary>? fromRegistry = null;
        // The registry describes the configured relational store only; a store swapped for another provider (test hosts) is listed directly.
        try { if (registry is not null && store.IsRelational) fromRegistry = await registry.SourceAnalysisSummariesAsync(50, ct); }
        catch (Exception ex) when (ex is InvalidOperationException or System.Data.Common.DbException) { fromRegistry = null; }
        if (fromRegistry is null) return Ok((await store.ListAsync(environmentId ?? "", ct)).Select(Services.IntegrationJourneys.SourceSetupAreas.Summary).ToList());
        var legacy = string.IsNullOrWhiteSpace(environmentId) ? [] : (await store.ListLegacyAsync(environmentId, ct)).Select(Services.IntegrationJourneys.SourceSetupAreas.Summary);
        return Ok(fromRegistry.Concat(legacy).OrderByDescending(s => s.AnalyzedAt).ThenByDescending(s => s.Id).Take(50).ToList());
    }

    /// <summary>The dependency evidence of every stored snapshot that has it — what internal-package mappings between snapshots are built from —
    /// without the rest of each snapshot.</summary>
    [HttpGet("dependency-sources")]
    public async Task<ActionResult<IReadOnlyList<BirkNext.Dependencies.DependencySourceEvidence>>> DependencySources([FromQuery] string? environmentId, CancellationToken ct) =>
        Ok((await store.ListAsync(environmentId ?? "", ct)).Where(s => s.DependencyEvidence is not null)
            .Select(s => new BirkNext.Dependencies.DependencySourceEvidence(s.Id, s.Archive.FileName, s.AnalyzedAt, s.DependencyEvidence!, s.Archive.Sha256)).ToList());

    /// <summary>One Source Analysis snapshot with its full evidence — fetched when a person opens it; readiness only carries its summary.</summary>
    [HttpGet("{snapshotId:guid}")]
    public async Task<ActionResult<IqrSourceSnapshot>> Get(Guid snapshotId, CancellationToken ct) =>
        await store.FindSourceAnalysisAsync(snapshotId, ct) is { } snapshot ? Ok(snapshot) : NotFound();

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

public sealed record SourceSchemaFileDto(string Path, string Text, string Fingerprint, SourceSchemaUsageDto? Usage = null);

/// <summary>
/// What the snapshot's source shows about one XSD (2026-11-18), in three separate answers a technical tester needs: is it declared in the
/// source, is it referenced by validation code, and has a test run verified it. Computed from the snapshot's contract evidence when read
/// (nothing stored twice). <see cref="Tier"/>: "ReferencedByValidationCode" (a validator names the file or namespace, or loads the registry
/// that lists it), "ReferencedBySource" (configuration, code or tests name it), "Declared" (present in the archive only).
/// </summary>
public sealed record SourceSchemaUsageDto(string? TargetNamespace, string? Version, string Tier, string TierBasis,
    IReadOnlyList<BirkNext.SourceDomains.SourceContractCodeReference> References, bool RuntimeVerified, string RuntimeVerification);

public static class SourceSchemaUsage
{
    public const string ValidationTier = "ReferencedByValidationCode", SourceTier = "ReferencedBySource", DeclaredTier = "Declared";
    public const string NotRuntimeVerified = "Not verified at runtime: no test run in BirkNext records which schema was applied to a message.";

    public static List<SourceSchemaFileDto> Attach(List<SourceSchemaFileDto> files, IqrSourceSnapshot snapshot)
    {
        var contracts = snapshot.EvidenceDomains?.Contracts.Contracts.Where(c => c.Type == BirkNext.SourceDomains.SourceContractType.XmlSchema).ToList() ?? [];
        // Snapshots analysed before namespace/registry references were recorded can show "nothing names it" for a registry-selected schema.
        var stale = (snapshot.EvidenceDomains?.Contracts.AnalyzerVersion ?? 0) < BirkNext.Api.Services.SourceAnalysis.Evidence.ContractAnalyzer.Version;
        return [.. files.Select(f =>
        {
            var contract = contracts.FirstOrDefault(c => string.Equals(Norm(c.File), Norm(f.Path), StringComparison.OrdinalIgnoreCase));
            return f with { Usage = Of(contract, stale) };
        })];
    }

    public static SourceSchemaUsageDto Of(BirkNext.SourceDomains.SourceContract? contract, bool staleAnalysis = false)
    {
        var references = contract?.CodeReferences ?? [];
        var literal = references.FirstOrDefault(r => r.Kind == BirkNext.SourceDomains.SourceContractReferenceKinds.Validator);
        var registry = references.FirstOrDefault(r => r.Kind == BirkNext.SourceDomains.SourceContractReferenceKinds.RegistryValidator);
        // Naming references only: being packaged with a project (often in bulk) or listed in a registry declares a schema, it does not use it.
        var other = references.FirstOrDefault(r => r.Kind is "Configuration reference" or "Code reference" or "Test code reference");
        var packaged = references.FirstOrDefault(r => r.Kind == "Embedded resource");
        var (tier, basis) = literal is not null ? (ValidationTier, $"Named by validation code: {literal.File}:{literal.Line}")
            : registry is not null ? (ValidationTier, $"Registered in a schema registry that validation code loads ({registry.File}:{registry.Line}); chosen per message by namespace")
            : other is not null ? (SourceTier, $"{other.Kind}: {other.File}:{other.Line}")
            : (DeclaredTier, packaged is not null ? $"Packaged with a project ({packaged.File}:{packaged.Line}); nothing in the source names it"
                : references.Count > 0 ? "Listed in a schema registry only" : "Present in the source archive; nothing in the source names it");
        if (staleAnalysis && tier != ValidationTier)
            basis += " (this snapshot was analysed before references by namespace and schema registry were recorded; re-import the source to refresh)";
        return new(contract?.XmlSchema?.TargetNamespace, contract?.Version ?? contract?.XmlSchema?.ExplicitVersion, tier, basis, references, false, NotRuntimeVerified);
    }

    private static string Norm(string path) => path.Replace('\\', '/').TrimStart('/');
}
public sealed record SourceSchemaPackageDto(Guid SnapshotId, string SnapshotFingerprint, string SourceArchiveName,
    DateTimeOffset AnalyzedAt, bool IsComplete, int OmittedFiles, IReadOnlyList<SourceSchemaFileDto> Files,
    bool IsAvailable = true, string? UnavailableReason = null, string? ProjectImportId = null, string? SourceProjectName = null);
public sealed record SourceSchemaSnapshotIndexDto(IReadOnlyList<SourceSchemaSnapshotIndexItem> Snapshots, bool IsTruncated);
