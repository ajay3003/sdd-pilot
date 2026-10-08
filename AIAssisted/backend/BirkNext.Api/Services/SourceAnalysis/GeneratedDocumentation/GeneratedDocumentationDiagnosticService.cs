using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.GeneratedDocumentation;
using BirkNext.Integrations;
using Microsoft.Extensions.Caching.Memory;

namespace BirkNext.Api.Services.SourceAnalysis.GeneratedDocumentation;

/// <summary>
/// System Settings → Developer → Generated Documentation Health. Reads the generated-documentation evidence Source Analysis stored on ONE exact
/// snapshot (never rescans an archive, never falls back to another snapshot) and adds the authored comparisons for the artifacts the current
/// workspace selected — only when that snapshot belongs to the same project (shared Project Import identity). Results are cached by snapshot id,
/// generated-evidence fingerprint, authored fingerprints and analyzer version. No AI, model or embedding call is made.
/// </summary>
public sealed class GeneratedDocumentationDiagnosticService(IqrSourceStore store, IMemoryCache cache)
{
    public const int Version = 1;

    public async Task<GeneratedDocumentationDiagnosticRun> RunAsync(GeneratedDocumentationDiagnosticRequest request, CancellationToken ct = default)
    {
        var started = DateTimeOffset.UtcNow;
        var watch = Stopwatch.StartNew();
        var snapshots = await store.ListSourceAnalysisAsync(100, ct);
        IqrSourceSnapshot? snapshot;
        string basis;
        if (request.SnapshotId is { } id)
        {
            snapshot = await store.FindSourceAnalysisAsync(id, ct);
            basis = "Requested snapshot";
            // A snapshot of another imported project is never evaluated against this project's artifacts.
            if (snapshot is not null && !string.IsNullOrWhiteSpace(request.ProjectImportId) && snapshot.ProjectImport is { } provenance && provenance.ImportId != request.ProjectImportId)
                return Finish(started, watch, GeneratedDocumentationRunState.NoSnapshot, "The requested snapshot belongs to a different imported project; it is not evaluated for the current project.", null, null, request, false);
        }
        else if (!string.IsNullOrWhiteSpace(request.ProjectImportId))
        {
            snapshot = snapshots.FirstOrDefault(s => s.ProjectImport?.ImportId == request.ProjectImportId);
            basis = "Snapshot of the current imported project";
            if (snapshot is null)
                return Finish(started, watch, GeneratedDocumentationRunState.NoSnapshot, "The current imported project has no Source Analysis snapshot. Snapshots of other projects are not used.", null, null, request, false);
        }
        else
        {
            snapshot = snapshots.FirstOrDefault();
            basis = "Newest Source Analysis snapshot";
        }
        if (snapshot is null)
            return Finish(started, watch, GeneratedDocumentationRunState.NoSnapshot, "No Source Analysis snapshot exists. Import a project or upload source in Source Analysis first.", null, null, request, false);

        var binding = new GeneratedDocumentationSnapshotBinding
        {
            SnapshotId = snapshot.Id, AnalyzedAt = snapshot.AnalyzedAt, ArchiveName = snapshot.Archive.FileName, Fingerprint = snapshot.Archive.Sha256,
            ProjectImportId = snapshot.ProjectImport?.ImportId, Basis = basis, IsNewest = snapshots.FirstOrDefault()?.Id == snapshot.Id,
            AnalyzerVersion = snapshot.GeneratedDocumentation?.AnalyzerVersion ?? 0,
        };
        // Historical snapshot: evaluate only its own evidence. Analyzed before generated-documentation evidence existed → not available (never "current source").
        if (snapshot.GeneratedDocumentation is not { } generated)
            return Finish(started, watch, GeneratedDocumentationRunState.NotAvailableForSnapshot,
                "This snapshot was analyzed before generated-documentation evidence existed. Analyze the same source again to create a new immutable snapshot; the current source is not substituted.", binding, null, request, false);

        var authoredAllowed = !string.IsNullOrWhiteSpace(request.ProjectImportId) && snapshot.ProjectImport?.ImportId == request.ProjectImportId;
        var key = CacheKey(snapshot, generated, request, authoredAllowed);
        if (cache.TryGetValue(key, out GeneratedDocumentationDiagnosticRun? cached) && cached is not null)
            return cached with { RunId = Guid.NewGuid(), StartedAt = started, CompletedAt = DateTimeOffset.UtcNow, DurationMilliseconds = watch.ElapsedMilliseconds, FromCache = true };
        var run = Finish(started, watch, generated.Documents.Count == 0 ? GeneratedDocumentationRunState.NoGeneratedDocumentation : GeneratedDocumentationRunState.Ready,
            generated.Documents.Count == 0 ? GeneratedDocumentationText.EmptyState : "Generated documentation evaluated for this snapshot.", binding, generated, request, authoredAllowed);
        cache.Set(key, run, new MemoryCacheEntryOptions { SlidingExpiration = TimeSpan.FromMinutes(10), Size = null });
        return run;
    }

    private static string CacheKey(IqrSourceSnapshot snapshot, GeneratedDocumentationSnapshot generated, GeneratedDocumentationDiagnosticRequest request, bool authored)
    {
        var builder = new StringBuilder($"gendoc|{Version}|{generated.AnalyzerVersion}|{snapshot.Id}|{authored}|");
        foreach (var d in generated.Documents) builder.Append(d.ContentFingerprint[..Math.Min(16, d.ContentFingerprint.Length)]);
        if (authored) foreach (var a in request.AuthoredArtifacts.OrderBy(a => a.Role, StringComparer.Ordinal)) builder.Append($"|{a.Role}:{a.ArtifactId}:{a.SourcePath}:{a.Fingerprint}");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    internal static GeneratedDocumentationDiagnosticRun Finish(DateTimeOffset started, Stopwatch watch, GeneratedDocumentationRunState state, string message,
        GeneratedDocumentationSnapshotBinding? binding, GeneratedDocumentationSnapshot? generated, GeneratedDocumentationDiagnosticRequest request, bool authoredAllowed)
    {
        var drift = new List<CrossArtifactDriftCandidate>(generated?.Drift ?? []);
        var authoredArtifacts = request.AuthoredArtifacts.Where(a => !string.IsNullOrWhiteSpace(a.Content)).ToList();
        AuthoredComparisonState authored;
        if (generated is null || generated.Documents.Count == 0) authored = new(false, "No generated documentation to compare with.", []);
        else if (authoredArtifacts.Count == 0) authored = new(false, "No authored artifact (Constitution, Specification, Plan, Tasks) is selected in the current workspace.", []);
        else if (!authoredAllowed)
            authored = new(false, "Authored artifacts are compared only with the source snapshot of the same imported project (shared Project Import identity). This snapshot is not linked to the current workspace's artifacts, so no authored comparison was made.", []);
        else
        {
            drift.AddRange(AuthoredDocumentationDrift.Compare(generated, authoredArtifacts));
            authored = new(true, "Compared with the generated documentation of the same project snapshot, by module scope (artifact path) where known.",
                authoredArtifacts.Select(a => $"{AuthoredDocumentationDrift.Role(a.Role)} · {GeneratedDocumentationDetector.Safe(a.DisplayName)}").ToList());
        }
        var modules = generated?.Modules.Select(m => Health(m, generated, drift)).ToList() ?? [];
        var limitations = new List<string>(generated?.Limitations ?? []) { GeneratedDocumentationText.NotSynchronized };
        return new GeneratedDocumentationDiagnosticRun
        {
            StartedAt = started, CompletedAt = DateTimeOffset.UtcNow, DurationMilliseconds = watch.ElapsedMilliseconds, State = state, StateMessage = message,
            Snapshot = binding, Modules = modules, Generators = generated?.Generators ?? [], Drift = drift, Authored = authored, Limitations = limitations,
            AnalyzerVersion = generated?.AnalyzerVersion ?? 0,
            Summary = new GeneratedDocumentationSummary
            {
                ModulesWithGeneratedDocs = modules.Count(m => m.GeneratedDocsDetected),
                GeneratedDocuments = generated?.Documents.Count(d => d.Origin == DocumentationOrigin.Generated) ?? 0,
                ModulesWithGeneratorDetected = modules.Count(m => m.GeneratorDetected),
                Fresh = modules.Count(m => m.Freshness == GeneratedDocumentationFreshnessStatus.Current),
                PotentiallyStale = modules.Count(m => m.Freshness == GeneratedDocumentationFreshnessStatus.Stale),
                FreshnessUnknown = modules.Count(m => m.Freshness is GeneratedDocumentationFreshnessStatus.Unknown or GeneratedDocumentationFreshnessStatus.NotEnoughEvidence),
                MissingExpectedDocs = modules.Sum(m => m.MissingExpectedDocs.Count),
                SourceDocumentationDiscrepancies = drift.Count(d => d.DriftType == CrossArtifactDriftType.DocumentationDrift),
                CrossArtifactDriftCandidates = drift.Count(d => d.DriftType != CrossArtifactDriftType.DocumentationDrift),
                StructuralFailures = generated?.Documents.Count(d => d.StructurallyInvalid) ?? 0,
            },
        };
    }

    internal static GeneratedDocumentationHealthResult Health(GeneratedDocumentationModule module, GeneratedDocumentationSnapshot generated, List<CrossArtifactDriftCandidate> drift)
    {
        var evidence = generated.Documents.Where(d => d.ModuleId == module.ModuleId).ToList();
        var generators = module.GeneratorIds.Select(id => generated.Generators.FirstOrDefault(g => g.Id == id)).Where(g => g is not null).ToList();
        var source = drift.Where(d => d.DriftType == CrossArtifactDriftType.DocumentationDrift && d.ModuleId == module.ModuleId).Select(d => d.Id).ToList();
        var cross = drift.Where(d => d.DriftType != CrossArtifactDriftType.DocumentationDrift && (d.ModuleId == module.ModuleId || d.ModuleId == "module:*")).Select(d => d.Id).ToList();
        var invalid = evidence.Where(e => e.StructurallyInvalid).ToList();
        var generatorDetected = generators.Count > 0;
        var (status, reason) =
            invalid.Count > 0 ? (GeneratedDocumentationHealthStatus.Fail, $"{invalid.Count} generated contract(s) could not be parsed: {string.Join(", ", invalid.Select(i => i.SafeRelativePath))}.")
            : module.Freshness == GeneratedDocumentationFreshnessStatus.Stale || module.MissingExpectedDocs.Count > 0 || source.Count > 0 || cross.Count > 0
                ? (GeneratedDocumentationHealthStatus.Warning, string.Join(" ", new[]
                {
                    module.Freshness == GeneratedDocumentationFreshnessStatus.Stale ? "Potentially stale." : null,
                    module.MissingExpectedDocs.Count > 0 ? $"{module.MissingExpectedDocs.Count} expected document(s) missing." : null,
                    source.Count > 0 ? $"{source.Count} source/documentation discrepancy candidate(s)." : null,
                    cross.Count > 0 ? $"{cross.Count} cross-artifact drift candidate(s)." : null,
                    "Needs review — not a failure.",
                }.Where(s => s is not null)))
            : !generatorDetected || module.Freshness is GeneratedDocumentationFreshnessStatus.Unknown or GeneratedDocumentationFreshnessStatus.NotEnoughEvidence
                || module.Comparisons.Any(c => c.State == GeneratedComparisonState.UnableToCompare)
                ? (GeneratedDocumentationHealthStatus.Partial, !generatorDetected ? GeneratedDocumentationText.NoGenerator
                    : module.Freshness is GeneratedDocumentationFreshnessStatus.Unknown or GeneratedDocumentationFreshnessStatus.NotEnoughEvidence ? "Freshness could not be established from trustworthy evidence."
                    : "Some comparisons could not be made with the available source evidence.")
            : (GeneratedDocumentationHealthStatus.Pass, "Generator traced, current, and no discrepancy among the structured keys compared.");
        return new GeneratedDocumentationHealthResult
        {
            Module = module.DisplayName, ModuleId = module.ModuleId, RootPath = module.RootPath, GeneratedDirectory = module.GeneratedDirectory,
            GeneratorDetected = generatorDetected,
            GeneratorSummary = generatorDetected ? string.Join("; ", generators.Select(g => $"{GeneratedDocumentationText.Label(g!.Type)}{(g.GeneratorName is null ? "" : $" ({g.GeneratorName})")} — {g.Path}"))
                : evidence.FirstOrDefault()?.Provenance.Label ?? "Likely generated / Unknown provenance",
            GeneratedDocsDetected = evidence.Any(e => e.Origin == DocumentationOrigin.Generated),
            Freshness = module.Freshness, FreshnessReason = module.FreshnessReason, LatestSourceEvidence = module.LatestSourceEvidence, LatestGeneratedEvidence = module.LatestGeneratedEvidence,
            SupportingFreshnessSignals = module.SupportingFreshnessSignals, MissingExpectedDocs = module.MissingExpectedDocs, AdditionalDocs = module.AdditionalDocs,
            SourceDiscrepancies = source, CrossArtifactDrift = cross, Comparisons = module.Comparisons, Status = status, StatusReason = reason, Evidence = evidence,
        };
    }
}
