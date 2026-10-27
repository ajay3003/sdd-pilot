using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.GeneratedDocumentation;
using BirkNext.Integrations;
using BirkNext.SourceArchitecture;
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
    /// <summary>2: health vs consistency semantics (stored snapshots are reinterpreted on read).</summary>
    public const int Version = 2;

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
            generated.Documents.Count == 0 ? GeneratedDocumentationText.EmptyState : "Generated documentation evaluated for this snapshot.", binding, generated, request, authoredAllowed, snapshot);
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
        GeneratedDocumentationSnapshotBinding? binding, GeneratedDocumentationSnapshot? generated, GeneratedDocumentationDiagnosticRequest request, bool authoredAllowed,
        IqrSourceSnapshot? source = null)
    {
        // One interpretation for every snapshot, whatever rules it was analyzed under (inventory, provenance and identities unchanged).
        if (generated is not null) generated = GeneratedDocumentationSemantics.Reinterpret(generated);
        var drift = new List<CrossArtifactDriftCandidate>(generated?.Drift ?? []);
        var authoredArtifacts = request.AuthoredArtifacts.Where(a => !string.IsNullOrWhiteSpace(a.Content)).ToList();
        AuthoredComparisonState authored;
        if (generated is null || generated.Documents.Count == 0) authored = new(false, "No generated documentation to compare with.", []);
        else if (authoredArtifacts.Count == 0) authored = new(false, "No authored artifact (Constitution, Specification, Plan, Tasks) is selected in the current workspace.", []);
        else if (!authoredAllowed)
            authored = new(false, "Authored artifacts are compared only with the source snapshot of the same imported project (shared Project Import identity). This snapshot is not linked to the current workspace's artifacts, so no authored comparison was made.", []);
        else
        {
            drift.AddRange(AuthoredDocumentationDrift.Compare(generated, authoredArtifacts).Select(GeneratedDocumentationSemantics.Classified));
            authored = new(true, "Compared with the generated documentation of the same project snapshot, by module scope (artifact path) where known.",
                authoredArtifacts.Select(a => $"{AuthoredDocumentationDrift.Role(a.Role)} · {GeneratedDocumentationDetector.Safe(a.DisplayName)}").ToList());
        }
        var modules = generated?.Modules.Select(m => Health(m, generated, drift)).ToList() ?? [];
        var limitations = new List<string>(generated?.Limitations ?? []) { GeneratedDocumentationText.NotSynchronized, GeneratedDocumentationText.ConsistencyBoundary };
        var reviewCandidates = drift.Where(GeneratedDocumentationSemantics.IsReviewCandidate).ToList();
        return new GeneratedDocumentationDiagnosticRun
        {
            StartedAt = started, CompletedAt = DateTimeOffset.UtcNow, DurationMilliseconds = watch.ElapsedMilliseconds, State = state, StateMessage = message,
            Snapshot = binding, Modules = modules, Generators = generated?.Generators ?? [], Drift = drift, Authored = authored, Limitations = limitations,
            AnalyzerVersion = generated?.AnalyzerVersion ?? 0,
            Autodoc = source is not null && generated is not null ? Autodoc(source, generated, modules, drift) : null,
            Summary = new GeneratedDocumentationSummary
            {
                ModulesWithGeneratedDocs = modules.Count(m => m.GeneratedDocsDetected),
                GeneratedDocuments = generated?.Documents.Count(d => d.Origin == DocumentationOrigin.Generated) ?? 0,
                ModulesWithGeneratorDetected = modules.Count(m => m.GeneratorDetected),
                Fresh = modules.Count(m => m.Freshness == GeneratedDocumentationFreshnessStatus.Current),
                PotentiallyStale = modules.Count(m => m.Freshness == GeneratedDocumentationFreshnessStatus.Stale),
                FreshnessUnknown = modules.Count(m => m.Freshness is GeneratedDocumentationFreshnessStatus.Unknown or GeneratedDocumentationFreshnessStatus.NotEnoughEvidence),
                MissingExpectedDocs = modules.Sum(m => m.MissingExpectedDocs.Count),
                ApplicabilityUnresolved = modules.Sum(m => m.ApplicabilityUnresolvedDocs.Count),
                SourceDocumentationDiscrepancies = reviewCandidates.Count(d => d.DriftType == CrossArtifactDriftType.DocumentationDrift),
                AuthoredGeneratedCandidates = reviewCandidates.Count(d => GeneratedDocumentationSemantics.IsAuthoredGenerated(d.DriftType)),
                CrossArtifactDriftCandidates = reviewCandidates.Count(d => d.DriftType != CrossArtifactDriftType.DocumentationDrift),
                CorrelationUnresolved = drift.Count(d => GeneratedDocumentationSemantics.Classify(d) is GeneratedConsistencyCategory.SourceCorrelationUnresolved or GeneratedConsistencyCategory.UnableToCompare),
                SourceOnlyObservations = drift.Count(d => GeneratedDocumentationSemantics.Classify(d) == GeneratedConsistencyCategory.SourceOnly),
                UnableToCompare = modules.Sum(m => m.Comparisons.Count(c => c.State == GeneratedComparisonState.UnableToCompare)),
                StructuralFailures = generated?.Documents.Count(d => d.StructurallyInvalid) ?? 0,
            },
        };
    }

    /// <summary>
    /// Autodoc diagnostics over the same stored evidence: the complete module inventory of the snapshot (architecture components, else the
    /// non-test projects — never derived from autodoc folders), each matched to the module-scoped generated output whose root contains it.
    /// Usage comes only from that module-specific output; a repository-level workflow (e.g. a maintain-docs skill) is reported on its own and
    /// never implies module usage. Repository-root output is listed separately and is never a module or API in the denominators. Not
    /// configured is neutral; freshness and health are the existing per-module results, unchanged.
    /// </summary>
    internal static AutodocDiagnostics Autodoc(IqrSourceSnapshot source, GeneratedDocumentationSnapshot generated, IReadOnlyList<GeneratedDocumentationHealthResult> health,
        IReadOnlyList<CrossArtifactDriftCandidate> drift)
    {
        var scoped = generated.Modules.Where(m => m.Scope != "Repository" && m.RootPath.Trim('/').Length > 0)
            .OrderByDescending(m => m.RootPath.Length).ToList();
        var inventory = new List<(string Name, string Identity, string Path, string Type, string Basis, bool Api)>();
        string basis;
        if (source.Architecture is { Components.Count: > 0 } architecture)
        {
            basis = "Source Analysis architecture components";
            foreach (var component in architecture.Components.Where(c => c.ComponentType != ArchitectureComponentType.ExternalSystem))
            {
                var inbound = architecture.Interfaces.Where(i => i.ComponentId == component.Id && string.Equals(i.Direction, "Inbound", StringComparison.OrdinalIgnoreCase)).ToList();
                var rest = inbound.Any(i => i.Type.Contains("REST", StringComparison.OrdinalIgnoreCase) || i.Protocol.Equals("HTTP", StringComparison.OrdinalIgnoreCase) && !i.Type.Contains("GraphQL", StringComparison.OrdinalIgnoreCase) && !i.Type.Contains("Health", StringComparison.OrdinalIgnoreCase));
                var graphQl = inbound.Any(i => i.Type.Contains("GraphQL", StringComparison.OrdinalIgnoreCase));
                var api = component.ComponentType is ArchitectureComponentType.Api or ArchitectureComponentType.Gateway || rest || graphQl;
                var type = rest && graphQl ? "REST/GraphQL API" : graphQl ? "GraphQL API" : rest ? "REST API"
                    : api ? "API"
                    : component.ComponentType switch
                    {
                        ArchitectureComponentType.Worker or ArchitectureComponentType.BackgroundService or ArchitectureComponentType.ScheduledJob or ArchitectureComponentType.Function
                            or ArchitectureComponentType.Adapter => "Worker/service",
                        ArchitectureComponentType.Frontend => "Web/UI",
                        ArchitectureComponentType.Library => "Library",
                        _ => "Unknown",
                    };
                var typeBasis = $"{component.ComponentType} component ({component.EvidenceState}{(component.RuntimeType.Length > 0 ? $", {component.RuntimeType}" : "")})"
                    + (inbound.Count > 0 ? $"; inbound: {string.Join(", ", inbound.Select(i => i.Type).Distinct())}" : "");
                inventory.Add((component.Name, component.Id, component.SourceProject, type, typeBasis, api));
            }
        }
        else
        {
            basis = "Source Analysis projects";
            foreach (var project in source.Projects.Where(p => !p.IsTest))
                inventory.Add((project.Name, project.Path, project.Path, "Unknown", $"Project only ({project.Classification}); no architecture evidence for this snapshot.", false));
        }

        GeneratedDocumentationModule? Owner(string path)
        {
            var normalized = path.Replace('\\', '/').TrimStart('/');
            return scoped.FirstOrDefault(m => normalized.StartsWith(m.RootPath.Trim('/') + "/", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, m.RootPath.Trim('/'), StringComparison.OrdinalIgnoreCase));
        }

        AutodocModuleInventory Row(string name, string identity, string path, string type, string typeBasis, bool api, GeneratedDocumentationModule? module, bool docsOnly)
        {
            var result = module is null ? null : health.FirstOrDefault(h => h.ModuleId == module.ModuleId);
            var documents = module is null ? [] : generated.Documents.Where(d => d.ModuleId == module.ModuleId && d.Origin != DocumentationOrigin.Authored).ToList();
            if (module is null || result is null || documents.Count == 0)
                return new()
                {
                    Name = name, Identity = identity, ProjectPath = path, ModuleType = type, TypeBasis = typeBasis, ApiCapable = api, Usage = AutodocUsage.NotConfigured,
                    UsageReason = "No module-specific generated documentation output was detected for this module.", FromGeneratedDocumentationOnly = docsOnly,
                };
            var generators = module.GeneratorIds.Select(id => generated.Generators.FirstOrDefault(g => g.Id == id)).OfType<GeneratorWorkflowEvidence>().ToList();
            var invalid = documents.Count(d => d.StructurallyInvalid);
            var (usage, reason) =
                invalid > 0 ? (AutodocUsage.Error, $"{invalid} generated artifact(s) could not be parsed.")
                : generators.Count == 0 ? (AutodocUsage.Partial, "Generated output present; the generator/workflow for this module could not be traced.")
                : module.MissingExpectedDocs.Count > 0 ? (AutodocUsage.Partial, $"Generated output present; {module.MissingExpectedDocs.Count} required output(s) absent.")
                : (AutodocUsage.Configured, "Module-specific generated output with a traced generator.");
            return new()
            {
                Name = name, Identity = identity, ProjectPath = path, ModuleType = type, TypeBasis = typeBasis, ApiCapable = api, Usage = usage, UsageReason = reason,
                GeneratedOutputPresent = true, ArtifactCount = documents.Count, ArtifactKinds = [.. documents.Select(d => d.DocumentKind).Distinct().Order()],
                GeneratedModuleId = module.ModuleId, OutputPath = module.GeneratedDirectory,
                Generator = generators.Count == 0 ? "Unknown generator" : string.Join(", ", generators.Select(g => g.GeneratorName ?? "Unknown generator").Distinct()),
                WorkflowSources = [.. generators.Select(g => g.Path).Distinct()], Freshness = module.Freshness, Health = result.Status, ParseErrors = invalid,
                MissingExpectedDocs = module.MissingExpectedDocs.Count, DiscrepancyCandidates = result.SourceDiscrepancies.Count,
                LastGenerated = module.LatestGeneratedEvidence, FromGeneratedDocumentationOnly = docsOnly,
            };
        }

        var rows = inventory.Select(i => Row(i.Name, i.Identity, i.Path, i.Type, i.Basis, i.Api, Owner(i.Path), false)).ToList();
        // Generated-documentation modules no detected project/component matched stay visible (type Unknown), never dropped.
        var matched = rows.Select(r => r.GeneratedModuleId).OfType<string>().ToHashSet(StringComparer.Ordinal);
        rows.AddRange(scoped.Where(m => !matched.Contains(m.ModuleId)).OrderBy(m => m.RootPath, StringComparer.Ordinal)
            .Select(m => Row(m.DisplayName, m.ModuleId, m.RootPath, "Unknown", "Generated-documentation module; no Source Analysis project or component matched its root.", false, m, true)));
        rows = [.. rows.OrderBy(r => r.Usage == AutodocUsage.NotConfigured).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)];

        var using_ = rows.Where(r => r.Usage != AutodocUsage.NotConfigured).ToList();
        var repositoryDocuments = generated.Modules.Where(m => m.Scope == "Repository" || m.RootPath.Trim('/').Length == 0)
            .SelectMany(m => generated.Documents.Where(d => d.ModuleId == m.ModuleId && d.Origin != DocumentationOrigin.Authored)).Select(d => d.SafeRelativePath).ToList();
        var maintainDocs = generated.Generators.Where(g => (g.GeneratorName ?? "").Contains("maintain-docs", StringComparison.OrdinalIgnoreCase)
            || g.Path.Contains("maintain-docs", StringComparison.OrdinalIgnoreCase)).ToList();
        // Module-level counts come from the distinct generated modules behind the rows (several projects may share one module output).
        var outputs = using_.Select(r => r.GeneratedModuleId).Distinct().Select(id => generated.Modules.First(m => m.ModuleId == id)).ToList();
        return new AutodocDiagnostics
        {
            MaintainDocsWorkflowDetected = maintainDocs.Count > 0, WorkflowSources = [.. generated.Generators.Select(g => g.Path).Distinct()], InventoryBasis = basis,
            DetectedModules = rows.Count, ApiModules = rows.Count(r => r.ApiCapable),
            ModulesUsingAutodoc = using_.Count, ModulesWithoutAutodoc = rows.Count - using_.Count, ModulesPartial = rows.Count(r => r.Usage == AutodocUsage.Partial),
            ApisUsingAutodoc = using_.Count(r => r.ApiCapable), ApisWithoutAutodoc = rows.Count(r => r.ApiCapable && r.Usage == AutodocUsage.NotConfigured),
            GeneratedArtifacts = outputs.Sum(m => generated.Documents.Count(d => d.ModuleId == m.ModuleId && d.Origin != DocumentationOrigin.Authored)),
            Fresh = outputs.Count(m => m.Freshness == GeneratedDocumentationFreshnessStatus.Current),
            PotentiallyStale = outputs.Count(m => m.Freshness == GeneratedDocumentationFreshnessStatus.Stale),
            FreshnessUnknown = outputs.Count(m => m.Freshness is GeneratedDocumentationFreshnessStatus.Unknown or GeneratedDocumentationFreshnessStatus.NotEnoughEvidence),
            MissingExpectedDocs = outputs.Sum(m => m.MissingExpectedDocs.Count),
            DiscrepancyCandidates = drift.Count(d => d.DriftType == CrossArtifactDriftType.DocumentationDrift && GeneratedDocumentationSemantics.IsReviewCandidate(d)),
            ParseErrors = generated.Documents.Count(d => d.StructurallyInvalid),
            Modules = rows, RepositoryLevelDocuments = repositoryDocuments,
            Limitations =
            [
                "Not configured is neutral: no repository or project policy requiring generated documentation was found, and none is assumed.",
                "A repository-level workflow (for example a maintain-docs skill) does not mean every module uses it; usage counts only module-specific generated output.",
                "API capability comes from Source Analysis architecture evidence (component type and inbound REST/GraphQL interfaces); Unknown where evidence is insufficient.",
                "Generated documentation is derived evidence: raw source remains stronger implementation evidence, and nothing here verifies runtime behavior.",
            ],
        };
    }

    internal static GeneratedDocumentationHealthResult Health(GeneratedDocumentationModule module, GeneratedDocumentationSnapshot generated, List<CrossArtifactDriftCandidate> drift)
    {
        var evidence = generated.Documents.Where(d => d.ModuleId == module.ModuleId).ToList();
        var generators = module.GeneratorIds.Select(id => generated.Generators.FirstOrDefault(g => g.Id == id)).Where(g => g is not null).ToList();
        var mine = drift.Where(d => d.ModuleId == module.ModuleId || d.DriftType != CrossArtifactDriftType.DocumentationDrift && d.ModuleId == "module:*").ToList();
        // Review candidates need two comparable claims that conflict; unresolved correlation and source-only items are diagnostics only.
        var source = mine.Where(d => d.DriftType == CrossArtifactDriftType.DocumentationDrift && GeneratedDocumentationSemantics.IsReviewCandidate(d)).Select(d => d.Id).ToList();
        var cross = mine.Where(d => d.DriftType != CrossArtifactDriftType.DocumentationDrift && GeneratedDocumentationSemantics.IsReviewCandidate(d)).Select(d => d.Id).ToList();
        var unresolved = mine.Where(d => !GeneratedDocumentationSemantics.IsReviewCandidate(d)).Select(d => d.Id).ToList();
        var invalid = evidence.Where(e => e.StructurallyInvalid).ToList();
        var generatorDetected = generators.Count > 0;
        var stale = module.Freshness == GeneratedDocumentationFreshnessStatus.Stale;
        var required = module.ExpectedDocuments.Where(e => e.State == ExpectedDocumentState.RequiredMissing).Select(e => e.FileName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        // Health covers the generated documentation itself (parseability, freshness, required documents); consistency never changes it.
        var (status, reason) =
            invalid.Count > 0 ? (GeneratedDocumentationHealthStatus.Fail, $"{invalid.Count} generated contract(s) could not be parsed: {string.Join(", ", invalid.Select(i => i.SafeRelativePath))}.")
            : stale || module.MissingExpectedDocs.Count > 0
                ? (GeneratedDocumentationHealthStatus.Warning, string.Join(" ", new[]
                {
                    stale ? "Potentially stale (trustworthy, scope-relevant source evidence is newer)." : null,
                    module.MissingExpectedDocs.Count > 0 ? $"{module.MissingExpectedDocs.Count} required document(s) missing." : null,
                    "Review suggested — not a failure.",
                }.Where(s => s is not null)))
            : !generatorDetected || module.Freshness is GeneratedDocumentationFreshnessStatus.Unknown or GeneratedDocumentationFreshnessStatus.NotEnoughEvidence
                ? (GeneratedDocumentationHealthStatus.Partial, !generatorDetected ? GeneratedDocumentationText.NoGeneratorEstablished
                    : "Freshness could not be established from trustworthy evidence.")
            : (GeneratedDocumentationHealthStatus.Pass, "Generator traced, current, and no required document missing.");
        var reasons = new List<string>();
        if (required.Count > 0) reasons.Add($"Required document(s) missing: {string.Join(", ", required)} ({string.Join("; ", module.ExpectedDocuments.Where(e => e.State == ExpectedDocumentState.RequiredMissing).Select(e => e.StateBasis).Distinct())})");
        var profileMissing = module.ExpectedDocuments.Where(e => e.State == ExpectedDocumentState.ExpectedByProfile && !e.Present).Select(e => e.FileName).ToList();
        if (profileMissing.Count > 0) reasons.Add($"Expected by profile and missing: {string.Join(", ", profileMissing)}.");
        if (stale) reasons.Add("Potentially stale: trustworthy, scope-relevant source evidence is newer than the generation date.");
        if (invalid.Count > 0) reasons.Add($"{invalid.Count} generated contract(s) do not parse.");
        if (source.Count > 0) reasons.Add($"{source.Count} source consistency candidate(s): comparable generated and source claims differ.");
        if (cross.Count > 0) reasons.Add($"{cross.Count} cross-artifact consistency candidate(s): comparable authored and generated claims differ.");
        var review = required.Count > 0 ? GeneratedDocumentationReviewState.ReviewRequired
            : reasons.Count > 0 ? GeneratedDocumentationReviewState.ReviewSuggested : GeneratedDocumentationReviewState.NoReviewSuggested;
        return new GeneratedDocumentationHealthResult
        {
            Module = module.DisplayName, ModuleId = module.ModuleId, RootPath = module.RootPath, GeneratedDirectory = module.GeneratedDirectory,
            GeneratorDetected = generatorDetected,
            GeneratorSummary = generatorDetected ? string.Join("; ", generators.Select(g => $"{GeneratedDocumentationText.Label(g!.Type)}{(g.GeneratorName is null ? "" : $" ({g.GeneratorName})")} — {g.Path}"))
                : GeneratedDocumentationText.NoGeneratorEstablished,
            GeneratedDocsDetected = evidence.Any(e => e.Origin == DocumentationOrigin.Generated),
            Freshness = module.Freshness, FreshnessReason = module.FreshnessReason, LatestSourceEvidence = module.LatestSourceEvidence, LatestGeneratedEvidence = module.LatestGeneratedEvidence,
            SupportingFreshnessSignals = module.SupportingFreshnessSignals, MissingExpectedDocs = module.MissingExpectedDocs, AdditionalDocs = module.AdditionalDocs,
            ApplicabilityUnresolvedDocs = [.. GeneratedDocumentationSemantics.ApplicabilityUnresolved(module)], ExpectedDocuments = module.ExpectedDocuments,
            SourceDiscrepancies = source, CrossArtifactDrift = cross, UnresolvedCorrelations = unresolved, Comparisons = module.Comparisons, Status = status, StatusReason = reason,
            Review = review, ReviewReasons = reasons, Evidence = evidence,
        };
    }
}
