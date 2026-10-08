using System.Text.RegularExpressions;
using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.Api.Services.SourceAnalysis;
using BirkNext.Api.Services.SourceAnalysis.Technology;
using BirkNext.Integrations;
using BirkNext.ProjectImport;
using BirkNext.Technology;
using Path = System.IO.Path;

namespace BirkNext.Api.Services.ProjectImport;

/// <summary>A preview, or the archive rejection (nothing was staged or activated).</summary>
public sealed record ProjectImportPreviewResult(ProjectImportPreview? Preview, SourceArchiveValidationFailure? Failure);

/// <summary>
/// Project Import: one archive, uploaded and validated once, feeds two owners.
/// <list type="bullet">
/// <item>Preview validates the bytes through the one Source Analysis archive reader (<see cref="IqrSourceArchiveReader.ReadDetailed(string, byte[], bool, CancellationToken)"/>)
/// with document capture, stages the bytes and the validated workspace in memory and reports the documents and the source it found.</item>
/// <item>Commit creates the Source Analysis snapshot from that same staged workspace, stamped with the shared import provenance. The documents
/// are activated by the frontend in the Shared Artifact Repository, which it owns.</item>
/// </list>
/// Source Analysis keeps owning snapshot identity, currentness and evidence; this service never stores documents and never merges the two.
/// </summary>
public sealed class ProjectImportService(IqrSourceStore store, ProjectImportStagingStore staging, IReviewSourceEvidenceProvider sources, ILogger<ProjectImportService> logger)
{
    /// <summary>The import identity: derived from the exact archive bytes, so the same archive always has the same identity.</summary>
    public static string ImportIdFor(string sha256) => "import-" + sha256[..Math.Min(16, sha256.Length)];

    public ProjectImportPreviewResult Preview(string fileName, byte[] bytes, CancellationToken ct = default)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var validation = IqrSourceArchiveReader.ReadDetailed(fileName, bytes, captureDocuments: true, ct);
        if (!validation.IsValid) return new(null, validation.Failure);
        var validationMs = clock.ElapsedMilliseconds;
        var workspace = validation.Workspace!;
        var source = DetectSource(workspace);
        var sourceMs = clock.ElapsedMilliseconds - validationMs;
        // Document roles: the shared artifact classifier (the browser's own source, compiled here too) over the captured documents. It never
        // picks among several candidates of a role; the user chooses after the import, exactly as with browser classification.
        var discovery = BirkNext.Web.Services.SampleProjects.ArtifactDocumentDiscovery.Classify((workspace.DocumentFiles ?? [])
            .Select(d => new BirkNext.Web.Services.SampleProjects.ArtifactDocumentDiscovery.Candidate(d.Path, Path.GetFileName(d.Path), d.Content)));
        var classificationMs = clock.ElapsedMilliseconds - validationMs - sourceMs;
        var importId = ImportIdFor(workspace.Archive.Sha256);
        var staged = staging.Add(importId, fileName, bytes, workspace, source);
        var stagingMs = clock.ElapsedMilliseconds - validationMs - sourceMs - classificationMs;
        var (projectName, basis) = ProjectName(fileName, workspace);
        var documents = (workspace.DocumentFiles ?? [])
            .OrderBy(d => d.Path, StringComparer.Ordinal)
            .Select(d => new ProjectImportDocument(d.Path, Path.GetFileName(d.Path), System.Text.Encoding.UTF8.GetByteCount(d.Content), d.Content))
            .ToList();
        var skipped = (workspace.SkippedDocuments ?? []).Select(d => new ProjectImportSkippedDocument(d.Path, d.Reason)).ToList();
        var other = Math.Max(0, (workspace.AllPaths?.Count ?? 0) - documents.Count - skipped.Count - workspace.Files.Count
            - (workspace.ConfigurationFiles?.Count ?? 0) - (workspace.EvidenceFiles?.Count ?? 0) - source.UnsupportedSourceFiles);
        logger.LogInformation("Project import staged. Archive size {ArchiveBytes}; entries {EntryCount}; documents {Documents}; source detected {SourceDetected}; "
            + "validation {ValidationMs} ms; source detection {SourceMs} ms; classification {ClassificationMs} ms; staging {StagingMs} ms",
            bytes.Length, workspace.EntryCount, documents.Count, source.Detected, validationMs, sourceMs, classificationMs, stagingMs);
        return new(new ProjectImportPreview
        {
            StagingId = staged.StagingId,
            ImportId = importId,
            Archive = new(workspace.Archive.FileName, workspace.Archive.Sha256, bytes.Length, workspace.EntryCount),
            ProjectName = projectName,
            ProjectNameBasis = basis,
            Documents = documents,
            SkippedDocuments = skipped,
            Source = source,
            OtherFiles = other,
            StagedAt = staged.StagedAt,
            ExpiresAt = staged.ExpiresAt,
            Discovery = discovery,
            Timings = new(validationMs, sourceMs, classificationMs, stagingMs),
        }, null);
    }

    /// <summary>
    /// Creates (or reuses) the Source Analysis snapshot of a staged archive. Null when the staging expired or is unknown.
    /// A source snapshot needs no Target Environment: source evidence is not runtime evidence, and a target added, switched or deleted later
    /// uses the same snapshot. Once the source part is settled (created, reused, no source or Source Analysis turned off) the archive bytes are
    /// released and only the result is kept until expiry, so a repeated commit returns it; a genuine failure (analysis or save) keeps the bytes
    /// so the source part can be retried without choosing the archive again. Stages survive a backend restart (see ProjectImportStagingStore).
    /// </summary>
    public async Task<ProjectImportCommitResult?> CommitAsync(Guid stagingId, CancellationToken ct = default)
    {
        var staged = staging.Find(stagingId);
        if (staged is null) return null;
        await staged.Gate.WaitAsync(ct);
        try
        {
            // Idempotent: a repeated commit of a settled stage (double click, client retry after a lost response, after a restart) returns the
            // same result and never analyses the archive again.
            if (staged.Committed is { } settledBefore) return settledBefore;
            var provenance = new ProjectImportProvenance
            {
                ImportId = staged.ImportId, ArchiveFileName = staged.Workspace.Archive.FileName, ArchiveSha256 = staged.Workspace.Archive.Sha256,
                ImportedAt = staging.Now,
            };
            var source = await SourceAsync(staged, provenance, ct);
            var settled = !source.CanRetry;
            var result = new ProjectImportCommitResult { StagingId = stagingId, Provenance = provenance, Source = source, StagedUntil = settled ? null : staged.ExpiresAt };
            if (settled) staging.MarkCommitted(staged, result);
            return result;
        }
        finally { staged.Gate.Release(); }
    }

    public bool Discard(Guid stagingId) => staging.Remove(stagingId);

    private async Task<ProjectImportSourceResult> SourceAsync(StagedProjectImport staged, ProjectImportProvenance provenance, CancellationToken ct)
    {
        var detection = staged.Source;
        if (!detection.Detected)
            return new ProjectImportSourceResult { State = ProjectImportSourceState.NotDetected, Technologies = detection.Technologies };
        if (!sources.SourceAnalysisEnabled)
            return NotCreated(detection, "SOURCE_ANALYSIS_DISABLED", "Source Analysis is turned off in Feature Visibility, so no source snapshot was created.", canRetry: false);
        // Idempotent re-import: the same archive bytes that are already the workspace's current Source Analysis snapshot are reused, not
        // analysed again. Any other case inserts a new immutable snapshot, which becomes current (history is never rewritten). Whether a
        // Target Environment is selected changes neither the archive identity nor this decision.
        var current = (await store.ListSourceAnalysisAsync(1, ct)).FirstOrDefault();
        if (current?.ProjectImport is { } previous && previous.ImportId == staged.ImportId && current.Archive.Sha256 == staged.Workspace.Archive.Sha256)
            return Snapshot(ProjectImportSourceState.Reused, current);

        try
        {
            var snapshot = await store.AnalyzeValidatedAsync(null, IqrSourceStore.SourceAnalysisOwner, staged.FileName, staged.Bytes, staged.Workspace, ct, provenance);
            return Snapshot(ProjectImportSourceState.Created, snapshot);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (SourceSnapshotPersistenceException)
        {
            return Failed(detection, "SOURCE_SNAPSHOT_SAVE_FAILED", "The source snapshot could not be saved. No new snapshot is available; retry without choosing the archive again.");
        }
        catch (Exception ex)
        {
            logger.LogError("Project import source analysis failed. Code {Code}; archive size {ArchiveBytes}; exception type {ExceptionType}",
                "SOURCE_ANALYSIS_FAILED", staged.Bytes.Length, ex.GetType().Name);
            return Failed(detection, "SOURCE_ANALYSIS_FAILED", "The archive passed validation, but source analysis could not complete. No snapshot was created; retry without choosing the archive again.");
        }
    }

    private static ProjectImportSourceResult Snapshot(ProjectImportSourceState state, IqrSourceSnapshot snapshot) => new()
    {
        State = state, SnapshotId = snapshot.Id, SnapshotStatus = snapshot.Status, AnalyzedAt = snapshot.AnalyzedAt,
        FilesAnalyzed = snapshot.Archive.FilesAnalyzed,
        Technologies = snapshot.TechnologyCoverage is { } coverage ? Technologies(coverage) : [],
        Limitations = snapshot.TechnologyCoverage?.Limitations.Take(10).ToList() ?? [],
    };

    private static ProjectImportSourceResult NotCreated(ProjectImportSourceDetection detection, string code, string message, bool canRetry) => new()
    {
        State = ProjectImportSourceState.NotCreated, Technologies = detection.Technologies, Code = code, Message = message, CanRetry = canRetry,
    };

    private static ProjectImportSourceResult Failed(ProjectImportSourceDetection detection, string code, string message) => new()
    {
        State = ProjectImportSourceState.Failed, Technologies = detection.Technologies, Code = code, Message = message, CanRetry = true,
    };

    /// <summary>
    /// Source = what Source Analysis reads: code and project files, dependency manifests, pipelines, infrastructure as code and contracts
    /// (path inventory and project markers only — the analysis itself runs at commit). JSON/YAML configuration alone, scripts and documents
    /// are not source, so a documents-only archive is "no source", never a failure.
    /// </summary>
    public static ProjectImportSourceDetection DetectSource(IqrSourceArchiveReader.Workspace workspace)
    {
        var coverage = TechnologyInventory.Detect(workspace);
        var sourceAreas = coverage.Technologies.Where(t => t.Area is TechnologyArea.Language or TechnologyArea.Dependency or TechnologyArea.Pipeline or TechnologyArea.Contract).ToList();
        var detected = workspace.Files.Count > 0 || (workspace.EvidenceFiles?.Count ?? 0) > 0 || sourceAreas.Count > 0;
        return new ProjectImportSourceDetection
        {
            Detected = detected,
            SourceFiles = workspace.Files.Count + (workspace.EvidenceFiles?.Count ?? 0) + coverage.UnsupportedSourceFiles,
            UnsupportedSourceFiles = coverage.UnsupportedSourceFiles,
            Technologies = detected ? Technologies(coverage) : [],
            Limitations = detected ? workspace.Limitations.Where(l => l.StartsWith("Not analyzed", StringComparison.Ordinal) || l.StartsWith("Nested", StringComparison.Ordinal)).Take(10).ToList() : [],
        };
    }

    private static List<ProjectImportTechnology> Technologies(SourceTechnologyCoverage coverage) =>
        coverage.Technologies
            .OrderBy(t => t.Area).ThenByDescending(t => t.Files).ThenBy(t => t.TechnologyId, StringComparer.Ordinal)
            .Select(t => new ProjectImportTechnology(t.TechnologyId, t.DisplayName, t.Area.ToString(),
                TechnologySupportRegistry.Find(t.TechnologyId) is { } d && TechnologySupportRegistry.IsSupported(d.SourceAnalysis), t.Files))
            .ToList();

    /// <summary>
    /// The project display name: the archive's single root folder when every entry is under one, else the archive file name without
    /// ".zip" and without a browser duplicate-download suffix such as " (2)". Never a hardcoded project name.
    /// </summary>
    public static (string Name, ProjectNameBasis Basis) ProjectName(string fileName, IqrSourceArchiveReader.Workspace workspace)
    {
        var paths = workspace.AllPaths ?? [];
        var roots = paths.Select(p => p.Split('/')).Where(s => s.Length > 1).Select(s => s[0]).Distinct(StringComparer.Ordinal).Take(2).ToList();
        if (paths.Count > 0 && roots.Count == 1 && paths.All(p => p.Contains('/')))
            return (IqrSourceArchiveReader.SafeLabel(roots[0]), ProjectNameBasis.ArchiveRoot);
        var name = Path.GetFileNameWithoutExtension(Path.GetFileName(fileName.Replace('\\', '/')));
        name = Regex.Replace(name, @"(\s*\(\d+\))+$", "").Trim();
        return (IqrSourceArchiveReader.SafeLabel(name.Length == 0 ? "Imported project" : name), ProjectNameBasis.ArchiveFileName);
    }
}
