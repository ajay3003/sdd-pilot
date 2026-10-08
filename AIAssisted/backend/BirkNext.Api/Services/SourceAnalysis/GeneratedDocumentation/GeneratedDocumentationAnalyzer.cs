using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.Api.Services.SourceArchitecture;
using BirkNext.GeneratedDocumentation;
using BirkNext.Integrations;
using BirkNext.Api.Services.Integrations.ApplicationMessaging;
using BirkNext.SourceArchitecture;
using BirkNext.SourceDomains;
using BirkNext.Api.Services.SourceAnalysis.Evidence;

namespace BirkNext.Api.Services.SourceAnalysis.GeneratedDocumentation;

/// <summary>
/// Source Analysis → Generated documentation. Runs ONCE at upload over the same immutable workspace and the models Source Analysis already
/// built (Architecture, Database, Contracts, Configuration): detection and provenance, module scoping, freshness and the deterministic
/// generated-vs-source comparisons. The result is bound to the snapshot id and fingerprint and versioned; consumers read it from the snapshot
/// and never rescan the archive. No document text, value or secret is stored — only paths, fingerprints, declared dates and structured keys.
/// </summary>
public static class GeneratedDocumentationAnalyzer
{
    public const int Version = 1;
    private const int MaxKeysPerList = 200;
    private const int MaxCandidatesPerFamily = 150;

    public static GeneratedDocumentationSnapshot Analyze(IqrSourceSnapshot snapshot, IqrSourceArchiveReader.Workspace workspace, CancellationToken ct = default) =>
        Analyze(snapshot, workspace, null, [], ct);

    internal static GeneratedDocumentationSnapshot Analyze(IqrSourceSnapshot snapshot, IqrSourceArchiveReader.Workspace workspace, ArchitectureInput? input,
        IReadOnlyList<ArchitectureFact> facts, CancellationToken ct = default, GeneratedDocumentationOptions? options = null)
    {
        var watch = Stopwatch.StartNew();
        options ??= GeneratedDocumentationOptions.Default;
        var allPaths = (workspace.AllPaths ?? workspace.Files.Select(f => f.Path).ToList()).Select(p => p.Replace('\\', '/')).ToList();
        var textFiles = (workspace.DocumentationCandidates ?? []).Concat(workspace.ConfigurationFiles ?? []).Concat(workspace.EvidenceFiles ?? [])
            .Concat(workspace.Files.Where(f => f.Path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)))
            .GroupBy(f => f.Path, StringComparer.Ordinal).Select(g => g.First()).ToList();
        var detection = GeneratedDocumentationDetector.Detect(textFiles, allPaths, options);
        ct.ThrowIfCancellationRequested();
        var (reliable, timestampBasis) = TimestampReliability(workspace.EntryModified, snapshot.AnalyzedAt);
        var limitations = new List<string>(detection.Limitations)
        {
            "Generated documentation is its own evidence type: it never becomes Specification, Constitution, Plan, Task or Data Model authority, and never verifies implementation.",
            "Comparisons use structured keys only (routes, messaging resources, entities, configuration keys, component names, contract operations); prose is not compared.",
        };
        if (!reliable) limitations.Add($"Archive modification times are not used for freshness: {timestampBasis}");
        if (detection.Documents.Count == 0)
            return new GeneratedDocumentationSnapshot
            {
                SourceSnapshotId = snapshot.Id, SourceFingerprint = snapshot.Archive.Sha256, AnalyzerVersion = Version, AnalyzedAt = snapshot.AnalyzedAt,
                Generators = detection.Generators, ArchiveTimestampsReliable = reliable, ArchiveTimestampBasis = timestampBasis,
                AuthoredDocumentationFiles = detection.AuthoredDocuments, UnclassifiedDocumentationFiles = detection.UnclassifiedDocuments,
                AnalysisMilliseconds = watch.ElapsedMilliseconds, Limitations = limitations,
            };

        var contracts = snapshot.EvidenceDomains?.Contracts.Contracts ?? [];
        var parseErrors = (snapshot.EvidenceDomains?.Contracts.Diagnostics ?? []).Where(d => d.Kind == "Parse error" && d.File is not null).Select(d => d.File!).ToHashSet(StringComparer.Ordinal);
        var generatedDirs = detection.Directories.Select(d => d.Path).ToList();
        var sourceText = workspace.Files.Concat(workspace.ConfigurationFiles ?? []).Concat(workspace.EvidenceFiles ?? []).GroupBy(f => f.Path, StringComparer.Ordinal).Select(g => g.First()).ToList();

        // ── Documents: keys, dates, contract links ──
        var documents = new List<(DetectedDocument Detected, GeneratedDocumentationEvidence Evidence, DocumentStructure Structure)>();
        foreach (var doc in detection.Documents)
        {
            ct.ThrowIfCancellationRequested();
            var safePath = GeneratedDocumentationDetector.Safe(doc.Path);
            var structure = new DocumentStructure(doc.Kind is GeneratedDocumentKind.ApiContract or GeneratedDocumentKind.GraphQlSchema ? "" : doc.Content);
            var related = contracts.Where(c => c.File == SourceEvidenceRedaction.SafePath(doc.Path) || c.File == safePath).ToList();
            // Structural validity: the contract parser's own parse error, or a JSON contract that is not well-formed JSON. Deterministic only.
            var invalid = doc.Origin == DocumentationOrigin.Generated && doc.Kind is GeneratedDocumentKind.ApiContract or GeneratedDocumentKind.GraphQlSchema
                && (parseErrors.Contains(SourceEvidenceRedaction.SafePath(doc.Path)) || parseErrors.Contains(safePath) || !WellFormedJson(doc));
            var keys = ExtractKeys(doc, structure, related);
            var declared = doc.Kind is GeneratedDocumentKind.Changelog ? null : DocumentStructure.DeclaredGenerationDate(doc.Content);
            DatedEvidence? generatedAt = declared is { } d ? new DatedEvidence(d.At, $"Declared in the document (line {d.Line})", safePath, d.Precision)
                : reliable && workspace.EntryModified!.TryGetValue(doc.Path, out var mtime) ? new DatedEvidence(mtime, "Archive modification time", safePath, "Second") : null;
            var warnings = new List<string>();
            if (invalid) warnings.Add("Generated contract could not be parsed (structural failure).");
            if (keys.Truncated) warnings.Add($"Structured keys truncated to {MaxKeysPerList} per list.");
            if (doc.Origin == DocumentationOrigin.Unknown) warnings.Add(doc.OriginBasis);
            documents.Add((doc, new GeneratedDocumentationEvidence
            {
                EvidenceId = "gendoc:" + safePath, SourceSnapshotId = snapshot.Id, ModuleId = ModuleId(doc.ModuleRoot), SafeRelativePath = safePath,
                DocumentKind = doc.Kind, KindBasis = doc.KindBasis, Origin = doc.Origin, OriginBasis = doc.OriginBasis, GeneratorKind = doc.Provenance.GeneratorType,
                GeneratorEvidence = doc.WorkflowIds, ContentFingerprint = GeneratedDocumentationDetector.Fingerprint(doc.Content), Bytes = System.Text.Encoding.UTF8.GetByteCount(doc.Content),
                Lines = doc.Content.Count(c => c == '\n') + 1, LastModified = generatedAt, Provenance = doc.Provenance with
                {
                    LastGeneratorEvidenceModified = reliable ? doc.WorkflowIds.Select(id => detection.Generators.FirstOrDefault(g => g.Id == id)?.Path).Where(p => p is not null)
                        .Select(p => workspace.EntryModified!.TryGetValue(p!, out var t) ? t : (DateTimeOffset?)null).Where(t => t is not null).Max() : null,
                },
                RelatedContractEvidenceIds = related.Select(c => c.Id).ToList(), Keys = keys, StructurallyInvalid = invalid, Warnings = warnings,
            }, structure));
        }

        // ── Modules ──
        var modules = new List<GeneratedDocumentationModule>();
        var drift = new List<CrossArtifactDriftCandidate>();
        var evidenceById = new Dictionary<string, GeneratedDocumentationEvidence>(StringComparer.Ordinal);
        foreach (var group in documents.GroupBy(d => d.Detected.ModuleRoot).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            var root = group.Key;
            var moduleId = ModuleId(root);
            bool InScope(string path) => (root.Length == 0 || path.StartsWith(root + "/", StringComparison.Ordinal))
                && !generatedDirs.Any(g => path.StartsWith(g + "/", StringComparison.Ordinal));
            // Source files of this scope: not documentation, not another generated folder, not tool/agent folders (".claude", ".github" …).
            var scopeSourceFiles = allPaths.Where(p => InScope(p) && (!GeneratedDocumentationDetector.IsDocumentFile(p) || IsSourceContract(p, contracts, generatedDirs))
                && !p.Split('/').Any(seg => seg.StartsWith('.') && seg.Length > 1)).ToList();
            var sourceKeys = SourceKeys(snapshot, input, facts, sourceText, allPaths, root, InScope);
            var dirs = detection.Directories.Where(d => d.ModuleRoot == root).ToList();

            // Freshness evidence of this scope only.
            var migrations = scopeSourceFiles.Select(p => (Path: p, Match: Regex.Match(p, @"(?:^|/)Migrations/(\d{14})_[^/]+\.cs$"))).Where(x => x.Match.Success && !x.Path.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase))
                .Select(x => DateTime.TryParseExact(x.Match.Groups[1].Value, "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at)
                    ? new DatedEvidence(new DateTimeOffset(at, TimeSpan.Zero), "Migration identifier (EF Core timestamp)", GeneratedDocumentationDetector.Safe(x.Path), "Second") : null)
                .Where(x => x is not null && x.At <= snapshot.AnalyzedAt.AddDays(1)).OrderByDescending(x => x!.At).FirstOrDefault();
            DatedEvidence? sourceTime = null;
            if (reliable)
            {
                var latest = scopeSourceFiles.Where(p => workspace.EntryModified!.ContainsKey(p)).OrderByDescending(p => workspace.EntryModified![p]).FirstOrDefault();
                if (latest is not null) sourceTime = new DatedEvidence(workspace.EntryModified![latest], "Archive modification time", GeneratedDocumentationDetector.Safe(latest), "Second");
            }
            var supporting = SupportingSignals(group.Select(g => g.Detected).ToList(), textFiles.Where(f => InScope(f.Path) && (f.Path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) || f.Path.EndsWith("Directory.Build.props", StringComparison.OrdinalIgnoreCase))).ToList(),
                workspace.Files.Where(f => InScope(f.Path) && f.Path.EndsWith("Directory.Build.props", StringComparison.OrdinalIgnoreCase)).ToList());

            var evidenceList = new List<GeneratedDocumentationEvidence>();
            foreach (var (detected, evidence, _) in group)
            {
                var (status, reason, relevant) = Freshness(detected, evidence, migrations, sourceTime, snapshot.Commit);
                var updated = evidence with
                {
                    SourceLastModified = relevant, FreshnessStatus = status, FreshnessReason = reason,
                    RelatedSourceEvidenceIds = RelatedSource(detected.Kind, sourceKeys, snapshot, root, InScope),
                };
                evidenceList.Add(updated);
                evidenceById[updated.EvidenceId] = updated;
            }
            var generated = evidenceList.Where(e => e.Origin == DocumentationOrigin.Generated).ToList();
            var assessed = generated.Where(e => e.FreshnessStatus != GeneratedDocumentationFreshnessStatus.NotApplicable).ToList();
            var moduleFreshness = assessed.Count == 0 ? GeneratedDocumentationFreshnessStatus.NotApplicable
                : assessed.Any(e => e.FreshnessStatus == GeneratedDocumentationFreshnessStatus.Stale) ? GeneratedDocumentationFreshnessStatus.Stale
                : assessed.All(e => e.FreshnessStatus == GeneratedDocumentationFreshnessStatus.Current) ? GeneratedDocumentationFreshnessStatus.Current
                : assessed.Any(e => e.FreshnessStatus == GeneratedDocumentationFreshnessStatus.Current || e.FreshnessStatus == GeneratedDocumentationFreshnessStatus.NotEnoughEvidence) && assessed.All(e => e.LastModified is not null)
                    ? GeneratedDocumentationFreshnessStatus.NotEnoughEvidence : GeneratedDocumentationFreshnessStatus.Unknown;
            var stale = assessed.Where(e => e.FreshnessStatus == GeneratedDocumentationFreshnessStatus.Stale).ToList();
            var moduleReason = moduleFreshness switch
            {
                GeneratedDocumentationFreshnessStatus.Stale => $"{stale.Count} generated document(s) predate relevant source evidence of this scope: {string.Join("; ", stale.Take(3).Select(e => $"{System.IO.Path.GetFileName(e.SafeRelativePath)} ({e.FreshnessReason})"))}",
                GeneratedDocumentationFreshnessStatus.Current => "Every assessed generated document is at least as new as the relevant dated source evidence of this scope.",
                GeneratedDocumentationFreshnessStatus.NotEnoughEvidence => $"Generation dates are declared, but this scope has no trustworthy dated source evidence for {assessed.Count(e => e.FreshnessStatus != GeneratedDocumentationFreshnessStatus.Current)} document(s). Not stale — not established.",
                GeneratedDocumentationFreshnessStatus.NotApplicable => "No generated document with a source scope to compare (history records and documents of unknown origin are not assessed).",
                _ => "The generation time of at least one document could not be established (no declared date, and archive times are not trustworthy).",
            };

            // Expected outputs: only what a detected workflow declares for this scope's generated folder, and only where the module shows the capability.
            var expected = new List<ExpectedGeneratedDocument>();
            foreach (var dir in dirs)
                foreach (var file in dir.DeclaredFiles)
                {
                    var present = group.Any(g => g.Detected.Directory == dir.Path && System.IO.Path.GetFileName(g.Detected.Path).Equals(file, StringComparison.OrdinalIgnoreCase));
                    var kind = GeneratedDocumentationDetector.KindFromName(file);
                    var capability = CapabilityFor(kind);
                    var capable = capability is null || sourceKeys.Capabilities.Contains(capability);
                    expected.Add(new ExpectedGeneratedDocument(file, kind, present, capable,
                        $"Declared by {string.Join(", ", dir.DeclaringWorkflowIds.Select(id => detection.Generators.First(g => g.Id == id).Path))}{(capability is null ? "" : capable ? $"; module shows {capability}" : $"; module shows no {capability} evidence — not expected")}"));
                }
            var missing = expected.Where(e => !e.Present && e.CapabilityPresent).Select(e => e.FileName).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
            var declaredNames = dirs.SelectMany(d => d.DeclaredFiles).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var additional = evidenceList.Where(e => dirs.Any(d => d.DeclaredFiles.Count > 0) && !declaredNames.Contains(System.IO.Path.GetFileName(e.SafeRelativePath))).Select(e => e.SafeRelativePath).ToList();

            // Generated-vs-source comparisons (DocumentationDrift).
            var comparisons = new List<GeneratedComparison>();
            var moduleDocs = group.Where(g => g.Detected.Origin == DocumentationOrigin.Generated).Select(g => (g.Detected, Evidence: evidenceById[g.Evidence.EvidenceId], g.Structure)).ToList();
            var moduleDrift = new List<CrossArtifactDriftCandidate>();
            CompareDocumentation(snapshot, moduleId, root, moduleDocs, sourceKeys, contracts, generatedDirs, sourceText.Where(f => InScope(f.Path)).ToList(), comparisons, moduleDrift);
            CompareSpecificationContracts(snapshot, moduleId, root, moduleDocs, contracts, allPaths, comparisons, moduleDrift);
            drift.AddRange(moduleDrift);

            modules.Add(new GeneratedDocumentationModule
            {
                ModuleId = moduleId, DisplayName = root.Length == 0 ? "Repository root" : GeneratedDocumentationDetector.Safe(root), RootPath = GeneratedDocumentationDetector.Safe(root),
                GeneratedDirectory = string.Join(", ", dirs.Select(d => GeneratedDocumentationDetector.Safe(d.Path)).DefaultIfEmpty(GeneratedDocumentationDetector.Safe(group.First().Detected.Directory))),
                Scope = root.Length == 0 ? "Repository" : "Module", SourceFileCount = scopeSourceFiles.Count,
                DocumentIds = evidenceList.Select(e => e.EvidenceId).ToList(),
                GeneratorIds = dirs.SelectMany(d => d.DeclaringWorkflowIds).Distinct(StringComparer.Ordinal).ToList(),
                Freshness = moduleFreshness, FreshnessReason = moduleReason,
                LatestSourceEvidence = new[] { migrations, sourceTime }.Where(x => x is not null).OrderByDescending(x => x!.At).FirstOrDefault(),
                LatestGeneratedEvidence = evidenceList.Where(e => e.LastModified is not null).Select(e => e.LastModified!).OrderByDescending(x => x.At).FirstOrDefault(),
                SupportingFreshnessSignals = supporting, ExpectedDocuments = expected, MissingExpectedDocs = missing, AdditionalDocs = additional,
                Comparisons = comparisons, SourceKeys = sourceKeys,
            });
        }

        return new GeneratedDocumentationSnapshot
        {
            SourceSnapshotId = snapshot.Id, SourceFingerprint = snapshot.Archive.Sha256, AnalyzerVersion = Version, AnalyzedAt = snapshot.AnalyzedAt,
            Generators = detection.Generators, Documents = [.. evidenceById.Values.OrderBy(e => e.SafeRelativePath, StringComparer.Ordinal)], Modules = modules,
            Drift = [.. drift.OrderBy(d => d.ModuleId, StringComparer.Ordinal).ThenBy(d => d.DriftType).ThenBy(d => d.Family, StringComparer.Ordinal).ThenBy(d => d.StructuredKey, StringComparer.Ordinal)],
            ArchiveTimestampsReliable = reliable, ArchiveTimestampBasis = timestampBasis,
            AuthoredDocumentationFiles = detection.AuthoredDocuments, UnclassifiedDocumentationFiles = detection.UnclassifiedDocuments,
            AnalysisMilliseconds = watch.ElapsedMilliseconds, Limitations = limitations,
        };
    }

    private static bool WellFormedJson(DetectedDocument doc)
    {
        if (!doc.Path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return true;
        try { using var _ = System.Text.Json.JsonDocument.Parse(doc.Content, new System.Text.Json.JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = System.Text.Json.JsonCommentHandling.Skip }); return true; }
        catch (System.Text.Json.JsonException) { return false; }
    }

    public static string ModuleId(string root) => "module:" + (root.Length == 0 ? "/" : GeneratedDocumentationDetector.Safe(root));

    // ── Timestamps ──────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Archive times date files only when they vary: a downloaded or exported archive stamps every entry with one time, and DOS
    /// minimum or future times are placeholders. Never the current machine time.</summary>
    internal static (bool Reliable, string Basis) TimestampReliability(IReadOnlyDictionary<string, DateTimeOffset>? times, DateTimeOffset analyzedAt)
    {
        if (times is null || times.Count == 0) return (false, "the archive reader recorded no entry times.");
        if (times.Values.Any(t => t.Year < 1981 || t > analyzedAt.AddDays(1))) return (false, "some entry times are placeholders (before 1981 or in the future).");
        var top = times.Values.GroupBy(t => t.UtcDateTime.ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture)).Max(g => g.Count());
        if (times.Count >= 2 && top >= times.Count * 0.9)
            return (false, $"{top:N0} of {times.Count:N0} entries share one modification time (typical of a downloaded or exported archive).");
        if (times.Count < 2) return (false, "a single entry cannot show whether times vary.");
        return (true, $"Entry times vary ({times.Values.Select(t => t.UtcDateTime.Date).Distinct().Count():N0} distinct days); used as supporting file modification evidence.");
    }

    private static (GeneratedDocumentationFreshnessStatus, string, DatedEvidence?) Freshness(DetectedDocument doc, GeneratedDocumentationEvidence evidence, DatedEvidence? migration, DatedEvidence? sourceTime, string commit)
    {
        if (doc.Origin != DocumentationOrigin.Generated) return (GeneratedDocumentationFreshnessStatus.NotApplicable, "Not a declared generated output; freshness is not assessed.", null);
        if (doc.Kind == GeneratedDocumentKind.Changelog) return (GeneratedDocumentationFreshnessStatus.NotApplicable, "A changelog is a history record; its versions are supporting metadata only.", null);
        // A source fingerprint/commit the document declares is the strongest signal — when the snapshot's commit is known.
        var head = GeneratedDocumentationDetector.Head(doc.Content, 30);
        var reference = Regex.Match(head, @"(?i)\b(?:source[\s_-]?(?:commit|sha|revision|fingerprint)|generated\s+from(?:\s+commit)?|commit)\s*[:=]?\s*`?([0-9a-f]{7,64})\b");
        if (reference.Success && !string.IsNullOrWhiteSpace(commit) && !commit.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
        {
            var r = reference.Groups[1].Value.ToLowerInvariant(); var c = commit.ToLowerInvariant();
            return c.StartsWith(r, StringComparison.Ordinal) || r.StartsWith(c, StringComparison.Ordinal)
                ? (GeneratedDocumentationFreshnessStatus.Current, $"Declares the snapshot's own source commit ({r[..Math.Min(7, r.Length)]}).", null)
                : (GeneratedDocumentationFreshnessStatus.Stale, $"Declares source commit {r[..Math.Min(7, r.Length)]}; the snapshot is commit {c[..Math.Min(7, c.Length)]}.", null);
        }
        if (evidence.LastModified is not { } generatedAt)
            return (GeneratedDocumentationFreshnessStatus.Unknown, "No trustworthy generation time: the document declares no date and archive times are not trustworthy.", null);
        // Relevance: a data model is dated by schema migrations as well as files; other kinds only by file times of the scope.
        var relevant = doc.Kind == GeneratedDocumentKind.DataModel
            ? new[] { migration, sourceTime }.Where(x => x is not null).OrderByDescending(x => x!.At).FirstOrDefault()
            : sourceTime;
        if (relevant is null)
            return (GeneratedDocumentationFreshnessStatus.NotEnoughEvidence, doc.Kind == GeneratedDocumentKind.DataModel
                ? "Generation date known; no migration identifier or trustworthy file time dates this scope's data model."
                : "Generation date known; no trustworthy dated source evidence in this scope (archive times are uniform).", null);
        var dayPrecision = generatedAt.Precision == "Day" || relevant.Precision == "Day";
        var newer = dayPrecision ? relevant.At.UtcDateTime.Date > generatedAt.At.UtcDateTime.Date : relevant.At > generatedAt.At;
        var genText = generatedAt.At.ToString(dayPrecision ? "yyyy-MM-dd" : "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        var srcText = relevant.At.ToString(dayPrecision ? "yyyy-MM-dd" : "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        return newer
            ? (GeneratedDocumentationFreshnessStatus.Stale, $"{relevant.Basis} {srcText} ({relevant.Path}) is newer than the generation date {genText}.", relevant)
            : (GeneratedDocumentationFreshnessStatus.Current, $"Generated {genText}; latest relevant source evidence {srcText} ({relevant.Basis.ToLowerInvariant()}).", relevant);
    }

    /// <summary>Supporting (weaker than source evidence): generator changelog versions vs source version declarations, and unreleased entries.</summary>
    private static List<string> SupportingSignals(List<DetectedDocument> docs, List<SourceFile> projectFiles, List<SourceFile> props)
    {
        var signals = new List<string>();
        var changelog = docs.FirstOrDefault(d => d.Kind == GeneratedDocumentKind.Changelog && d.Origin == DocumentationOrigin.Generated);
        if (changelog is null) return signals;
        var (versions, unreleased) = DocumentStructure.Changelog(changelog.Content);
        if (versions.Count > 0)
        {
            var latest = versions.OrderByDescending(v => v.Version, Comparer<string>.Create(DocumentStructure.CompareVersions)).First();
            signals.Add($"Generator changelog latest version {GeneratedDocumentationDetector.Safe(latest.Version)}{(latest.Date is { } d ? $" ({d:yyyy-MM-dd})" : "")}.");
            var sourceVersions = projectFiles.Concat(props).SelectMany(f => Regex.Matches(f.Content, @"<(?:Version|VersionPrefix)>\s*(\d+\.\d+(?:\.\d+)?)").Select(m => (f.Path, m.Groups[1].Value))).ToList();
            if (sourceVersions.Count > 0)
            {
                var highest = sourceVersions.OrderByDescending(v => v.Value, Comparer<string>.Create(DocumentStructure.CompareVersions)).First();
                var cmp = DocumentStructure.CompareVersions(highest.Value, latest.Version);
                signals.Add(cmp > 0 ? $"Source declares version {highest.Value} ({GeneratedDocumentationDetector.Safe(highest.Path)}), newer than the changelog — supporting signal only."
                    : cmp == 0 ? $"Source version {highest.Value} matches the changelog." : $"Source version {highest.Value} ({GeneratedDocumentationDetector.Safe(highest.Path)}) is older than the changelog version — version may be CI-managed.");
            }
            else signals.Add("No source version declaration in this scope (version may be CI-managed).");
        }
        if (unreleased) signals.Add("The changelog has entries under \"Unreleased\" (changes recorded after the latest released version).");
        return signals;
    }

    // ── Keys ────────────────────────────────────────────────────────────────────────────────────────────────────────────

    private static GeneratedDocumentKeys ExtractKeys(DetectedDocument doc, DocumentStructure s, List<SourceContract> related)
    {
        var routes = new List<string>(); var resources = new List<string>(); var entities = new List<string>(); var relationships = new List<string>();
        var config = new List<string>(); var components = new List<string>(); var versions = new List<string>();
        if (doc.Kind is GeneratedDocumentKind.ApiContract or GeneratedDocumentKind.GraphQlSchema)
        {
            foreach (var op in related.SelectMany(c => c.Operations))
                if (op.Path is { } path && op.Kind is "GET" or "POST" or "PUT" or "DELETE" or "PATCH") routes.Add(StructuredKeys.Route(op.Kind, path));
        }
        else
        {
            routes.AddRange(DocumentKeyReader.Routes(s));
            if (doc.Kind == GeneratedDocumentKind.Messaging) resources.AddRange(DocumentKeyReader.MessagingResources(s));
            if (doc.Kind == GeneratedDocumentKind.DataModel) { entities.AddRange(DocumentKeyReader.Entities(s)); relationships.AddRange(DocumentKeyReader.Relationships(s)); }
            config.AddRange(DocumentKeyReader.ConfigurationKeys(s));
            if (doc.Kind is GeneratedDocumentKind.Architecture or GeneratedDocumentKind.Overview) components.AddRange(DocumentKeyReader.Components(s));
            if (doc.Kind == GeneratedDocumentKind.Changelog) versions.AddRange(DocumentStructure.Changelog(doc.Content).Versions.Select(v => v.Version));
        }
        var technologies = TechnologyVocabulary.Match(doc.Content);
        List<string> Cap(IEnumerable<string> values, ref bool truncated)
        {
            var list = values.Select(SourceEvidenceRedaction.SafePath).Distinct(StringComparer.Ordinal).ToList();
            if (list.Count > MaxKeysPerList) { truncated = true; list = list.Take(MaxKeysPerList).ToList(); }
            return list;
        }
        var t = false;
        return new GeneratedDocumentKeys
        {
            Routes = Cap(routes, ref t), MessagingResources = Cap(resources, ref t), Entities = Cap(entities, ref t), Relationships = Cap(relationships, ref t),
            ConfigurationKeys = Cap(config, ref t), Components = Cap(components, ref t), Technologies = technologies, Versions = Cap(versions, ref t), Truncated = t,
        };
    }

    // ── Source keys of one scope ────────────────────────────────────────────────────────────────────────────────────────

    private static ModuleSourceKeys SourceKeys(IqrSourceSnapshot snapshot, ArchitectureInput? input, IReadOnlyList<ArchitectureFact> facts, List<SourceFile> sourceText,
        List<string> allPaths, string root, Func<string, bool> inScope)
    {
        var projects = snapshot.Projects.Where(p => inScope(p.Path) && !p.IsTest).ToList();
        var projectPaths = projects.Select(p => p.Path).ToHashSet(StringComparer.Ordinal);
        bool ProjectInScope(string? project) => project is not null && (projectPaths.Contains(project) || inScope(project) || projects.Any(p => p.Name == project));
        var scopeFacts = facts.Where(f => ProjectInScope(f.ProjectPath) || inScope(f.Evidence.File)).ToList();
        var routes = new List<string>();
        // Health and GraphQL endpoint mappings satisfy a documented route but are never reported as "undocumented" REST operations.
        var auxiliaryRoutes = new List<string>();
        foreach (var f in scopeFacts)
        {
            if (f.Kind == "RestEndpoint" && f["method"] is { } m && f["route"] is { } r) routes.Add(StructuredKeys.Route(m, r));
            if (f.Kind == "HealthChecks" && f["route"] is { } h) auxiliaryRoutes.Add(StructuredKeys.Route("GET", h));
            if (f.Kind == "GraphQlEndpoint" && f["route"] is { } g) { auxiliaryRoutes.Add(StructuredKeys.Route("POST", g)); auxiliaryRoutes.Add(StructuredKeys.Route("GET", g)); }
        }
        foreach (var file in sourceText.Where(f => inScope(f.Path) && f.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)))
            routes.AddRange(SourceRouteReader.AttributeRoutes(file.Content));
        var components = snapshot.Architecture?.Components.Where(c => ProjectInScope(c.SourceProject)).ToList() ?? [];
        var componentIds = components.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        var resources = new List<string>();
        foreach (var channel in snapshot.Architecture?.MessagingChannels ?? [])
        {
            var inModule = channel.Producers.Concat(channel.Consumers).Any(e => componentIds.Contains(e.ComponentId)) || channel.Evidence.Any(e => inScope(e.File));
            if (!inModule) continue;
            // Resolved names only: an unresolved channel's display name ("X Service Bus entity {name}") and namespace hosts are not resource names.
            foreach (var name in new[] { channel.EntityName, channel.Subscription, channel.ConsumerGroup })
                if (IsResolvedResourceName(name)) resources.Add(name!);
        }
        foreach (var f in scopeFacts.Where(f => f.Kind is "ServiceBusSend" or "ServiceBusReceive" or "EventHubConsumer" or "EventHubProducer"))
            foreach (var name in new[] { f["entity"], f["subscription"], f["consumerGroup"] })
                if (IsResolvedResourceName(name)) resources.Add(name!);
        var configEntries = snapshot.EvidenceDomains?.Configuration.Entries.Where(e => inScope(e.File)).ToList() ?? [];
        resources.AddRange(configEntries.Where(e => e.Category == ConfigurationCategory.Messaging && e.ValueKind == ConfigurationValueKind.EntityName
            && e.Sensitivity == ConfigurationSensitivity.None && IsResolvedResourceName(e.ValuePreviewSafe)).Select(e => e.ValuePreviewSafe!));
        // One entry per logical table: names of the same table seen through a DbContext and through migrations are merged; index/key
        // objects and the migrations history table are not entities.
        var groups = new List<HashSet<string>>();
        foreach (var db in snapshot.DatabaseArchitecture?.Databases ?? [])
        {
            if (!ProjectInScope(db.SourceProject)) continue;
            foreach (var table in db.Schemas.SelectMany(s => s.Tables))
            {
                var names = new[] { table.EntityTypeName, table.LogicalName, table.PhysicalName }.Where(n => !string.IsNullOrWhiteSpace(n)
                    && !Regex.IsMatch(n!, @"^(IX|PK|FK|AK|UQ|DF|CK)_|^__EFMigrationsHistory$", RegexOptions.IgnoreCase)).Select(n => n!).ToList();
                if (names.Count == 0) continue;
                var keys = names.Select(StructuredKeys.Name).ToHashSet(StringComparer.Ordinal);
                var existing = groups.Where(g => g.Select(StructuredKeys.Name).Any(keys.Contains)).ToList();
                var merged = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
                foreach (var e in existing) { merged.UnionWith(e); groups.Remove(e); }
                groups.Add(merged);
            }
        }
        var entities = groups.Select(g => string.Join("|", g.OrderBy(n => n, StringComparer.Ordinal))).ToList();
        var capabilities = new List<string>();
        if (projects.Count > 0 || allPaths.Any(p => inScope(p) && p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))) capabilities.Add("Source");
        if (routes.Count > 0 || scopeFacts.Any(f => f.Kind is "Controllers" or "RestEndpoint")) capabilities.Add("HTTP API");
        var inputProjects = input?.Projects.Where(p => inScope(p.Path) && !p.IsTest).ToList() ?? [];
        if (scopeFacts.Any(f => f.Kind is "GraphQlServer" or "GraphQlEndpoint") || inputProjects.Any(p => p.HasPackage("HotChocolate.AspNetCore"))) capabilities.Add("GraphQL");
        // Messaging capability needs messaging code in this scope (sender/receiver/consumer/producer or a messaging framework), not just names in configuration.
        if (scopeFacts.Any(f => f.Kind is "ServiceBusSend" or "ServiceBusReceive" or "EventHubConsumer" or "EventHubProducer" or "Wolverine")
            || (snapshot.Architecture?.MessagingChannels.Any(c => c.Producers.Concat(c.Consumers).Any(e => componentIds.Contains(e.ComponentId))) ?? false)) capabilities.Add("Messaging");
        if (entities.Count > 0 || scopeFacts.Any(f => f.Kind == "DbContext")) capabilities.Add("Data model");
        if (allPaths.Any(p => inScope(p) && p.EndsWith(".razor", StringComparison.OrdinalIgnoreCase))) capabilities.Add("UI routes");
        var truncated = false;
        List<string> Cap(IEnumerable<string> values, int max = 2000)
        {
            var list = values.Select(SourceEvidenceRedaction.SafePath).Where(v => v.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToList();
            if (list.Count > max) { truncated = true; list = list.Take(max).ToList(); }
            return list;
        }
        return new ModuleSourceKeys
        {
            Routes = Cap(routes), AuxiliaryRoutes = Cap(auxiliaryRoutes), RoutesComplete = routes.Count > 0, MessagingResources = Cap(resources), Entities = Cap(entities),
            ConfigurationKeys = Cap(configEntries.Select(e => e.NormalizedKey)), Projects = Cap(snapshot.Projects.Where(p => inScope(p.Path)).Select(p => p.Name)),
            DeployableComponents = Cap(components.Where(c => c.ComponentType is not (ArchitectureComponentType.Library or ArchitectureComponentType.Unknown or ArchitectureComponentType.ExternalSystem)).Select(c => c.LogicalName)),
            Capabilities = capabilities, Truncated = truncated,
        };
    }

    private static bool IsResolvedResourceName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && !name.StartsWith('$') && !name.Any(char.IsWhiteSpace) && !name.StartsWith('_') && !name.EndsWith('_')
        && !name.Contains("://", StringComparison.Ordinal) && !Regex.IsMatch(name, @"(?i)\.(servicebus|eventhub|azure|windows)\.|\.net$|unresolved");

    private static string? CapabilityFor(GeneratedDocumentKind kind) => kind switch
    {
        GeneratedDocumentKind.ApiContract => "HTTP API",
        GeneratedDocumentKind.GraphQlSchema => "GraphQL",
        GeneratedDocumentKind.Messaging => "Messaging",
        GeneratedDocumentKind.DataModel => "Data model",
        GeneratedDocumentKind.Routes => "UI routes",
        _ => "Source",
    };

    private static List<string> RelatedSource(GeneratedDocumentKind kind, ModuleSourceKeys keys, IqrSourceSnapshot snapshot, string root, Func<string, bool> inScope)
    {
        var components = snapshot.Architecture?.Components.Where(c => inScope(c.SourceProject)).Select(c => c.Id) ?? [];
        return kind switch
        {
            GeneratedDocumentKind.DataModel => (snapshot.DatabaseArchitecture?.Databases.Where(d => inScope(d.SourceProject)).Select(d => "database:" + d.Id) ?? []).Take(20).ToList(),
            GeneratedDocumentKind.Messaging => (snapshot.Architecture?.MessagingChannels.Where(c => c.Evidence.Any(e => inScope(e.File))).Select(c => c.Id) ?? []).Take(20).ToList(),
            _ => components.Take(20).ToList(),
        };
    }

    private static bool IsSourceContract(string path, IReadOnlyList<SourceContract> contracts, List<string> generatedDirs) =>
        !generatedDirs.Any(g => path.StartsWith(g + "/", StringComparison.Ordinal)) && contracts.Any(c => c.File == SourceEvidenceRedaction.SafePath(path));

    // ── Generated vs source (DocumentationDrift) ──────────────────────────────────────────────────────────────────────────

    private static void CompareDocumentation(IqrSourceSnapshot snapshot, string moduleId, string root,
        List<(DetectedDocument Detected, GeneratedDocumentationEvidence Evidence, DocumentStructure Structure)> docs, ModuleSourceKeys source,
        IReadOnlyList<SourceContract> contracts, List<string> generatedDirs, List<SourceFile> scopeSource, List<GeneratedComparison> comparisons, List<CrossArtifactDriftCandidate> drift)
    {
        var sink = new DriftSink(snapshot.Id, moduleId, CrossArtifactDriftType.DocumentationDrift);
        DriftEvidenceRef Doc(GeneratedDocumentationEvidence e) => new("Generated documentation", $"{GeneratedDocumentationText.Label(e.DocumentKind)} · {System.IO.Path.GetFileName(e.SafeRelativePath)}", e.SafeRelativePath, 0, e.EvidenceId, e.ContentFingerprint);
        GeneratedDocumentationFreshnessStatus? Worst(IEnumerable<GeneratedDocumentationEvidence> es) =>
            es.Any(e => e.FreshnessStatus == GeneratedDocumentationFreshnessStatus.Stale) ? GeneratedDocumentationFreshnessStatus.Stale : es.Select(e => (GeneratedDocumentationFreshnessStatus?)e.FreshnessStatus).FirstOrDefault();
        var sourceScope = root.Length == 0 ? "the repository" : $"scope {GeneratedDocumentationDetector.Safe(root)}/";
        // A repository-level overview summarises; it is not expected to list every route, resource or table of every module. Only what it
        // states is checked (documented-but-not-found); "in source but not documented" is reported for module scopes only.
        var moduleScope = root.Length > 0;

        // 1. HTTP routes: documented operations vs source route evidence (minimal APIs, health/GraphQL mappings, controller and endpoint attributes).
        var docRoutes = docs.SelectMany(d => d.Evidence.Keys.Routes.Where(r => !r.StartsWith("ROUTE ", StringComparison.Ordinal)).Select(r => (Route: r, d.Evidence))).ToList();
        if (docRoutes.Count == 0) comparisons.Add(new("HTTP routes", GeneratedComparisonState.NotApplicable, "No generated document states HTTP routes.", 0));
        else if (source.Routes.Count == 0)
            comparisons.Add(new("HTTP routes", GeneratedComparisonState.UnableToCompare, $"No HTTP route evidence in {sourceScope} (routes may come from a library outside this snapshot or from patterns not read).", 0));
        else
        {
            var before = sink.Count;
            foreach (var group in docRoutes.GroupBy(x => x.Route).Where(g => !source.Routes.Concat(source.AuxiliaryRoutes).Any(s => StructuredKeys.SameRoute(s, g.Key))))
                sink.Add(drift, "HTTP routes", group.Key, DriftDifferenceKind.MissingOnRight, ArchitectureEvidenceState.Confirmed,
                    group.Select(x => Doc(x.Evidence)).DistinctBy(x => x.Path).ToList(), [new("Source routes", $"{source.Routes.Count} route(s) in {sourceScope}", GeneratedDocumentationDetector.Safe(root))],
                    $"Generated documentation states {group.Key}; no matching route was found in the source evidence of {sourceScope}.",
                    "The route may be provided by a referenced library, a gateway or a pattern BirkNext does not read — or the documentation may describe a removed route.", Worst(group.Select(x => x.Evidence)));
            var hasContract = moduleScope && docs.Any(d => d.Detected.Kind == GeneratedDocumentKind.ApiContract);
            if (hasContract)
                foreach (var route in source.Routes.Where(s => !s.StartsWith("ROUTE ", StringComparison.Ordinal) && !docRoutes.Any(d => StructuredKeys.SameRoute(s, d.Route))))
                    sink.Add(drift, "HTTP routes", route, DriftDifferenceKind.MissingOnLeft, ArchitectureEvidenceState.Confirmed,
                        docs.Where(d => d.Detected.Kind == GeneratedDocumentKind.ApiContract).Select(d => Doc(d.Evidence)).ToList(),
                        [new("Source route", route, GeneratedDocumentationDetector.Safe(root))],
                        $"Source declares {route}; the generated API contract of this scope does not document it.",
                        "Generated documentation may predate the route, or the route may be intentionally undocumented (health, internal).", Worst(docs.Select(d => d.Evidence)));
            comparisons.Add(new("HTTP routes", sink.Count > before ? GeneratedComparisonState.PotentiallyDrifted : GeneratedComparisonState.Equivalent,
                $"{docRoutes.Select(r => r.Route).Distinct().Count()} documented route(s) vs {source.Routes.Count} source route(s).", sink.Count - before));
        }

        // 2. UI routes: page routes need component source, which Source Analysis does not read.
        if (docs.Any(d => d.Evidence.Keys.Routes.Any(r => r.StartsWith("ROUTE ", StringComparison.Ordinal))))
            comparisons.Add(new("UI routes", GeneratedComparisonState.UnableToCompare, "Page routes are documented, but component files (.razor and similar) are not read by Source Analysis.", 0));

        // 3/4. Contracts: a generated contract vs a non-generated contract of the same type in the same scope (e.g. a committed schema snapshot).
        foreach (var (family, type) in new[] { ("API contract", SourceContractType.OpenApi), ("GraphQL schema", SourceContractType.GraphQlSchema) })
        {
            var generatedContracts = docs.SelectMany(d => d.Evidence.RelatedContractEvidenceIds.Select(id => (Contract: contracts.FirstOrDefault(c => c.Id == id), d.Evidence))).Where(x => x.Contract?.Type == type).ToList();
            if (generatedContracts.Count == 0) continue;
            if (docs.Any(d => d.Evidence.StructurallyInvalid && d.Evidence.RelatedContractEvidenceIds.Count == 0 && (type == SourceContractType.OpenApi ? d.Detected.Kind == GeneratedDocumentKind.ApiContract : d.Detected.Kind == GeneratedDocumentKind.GraphQlSchema)))
                comparisons.Add(new(family, GeneratedComparisonState.UnableToCompare, "A generated contract could not be parsed.", 0));
            var sourceContracts = contracts.Where(c => c.Type == type && (root.Length == 0 || c.File.StartsWith(GeneratedDocumentationDetector.Safe(root) + "/", StringComparison.Ordinal))
                && !generatedDirs.Any(g => c.File.StartsWith(GeneratedDocumentationDetector.Safe(g) + "/", StringComparison.Ordinal)) && !SpecKitContract(c.File)).ToList();
            if (sourceContracts.Count == 0)
            {
                comparisons.Add(new(family, GeneratedComparisonState.UnableToCompare, type == SourceContractType.OpenApi
                    ? "No source-derived OpenAPI document in this scope; documented operations are compared with source routes instead."
                    : $"No source-derived GraphQL schema (e.g. a committed schema snapshot) in {sourceScope}; code-first schemas are not reconstructed.", 0));
                continue;
            }
            var before = sink.Count;
            foreach (var (generatedContract, evidence) in generatedContracts)
                foreach (var sourceContract in sourceContracts)
                    foreach (var change in SourceEvidenceDiff.ContractChanges(sourceContract, generatedContract!))
                    {
                        var kind = change.Kind switch { SourceEvidenceChangeKind.Added => DriftDifferenceKind.MissingOnRight, SourceEvidenceChangeKind.Removed => DriftDifferenceKind.MissingOnLeft, _ => DriftDifferenceKind.ValueMismatch };
                        var key = change.Key.Contains('|') ? change.Key[(change.Key.IndexOf('|') + 1)..] : change.Key;
                        sink.Add(drift, family, $"{change.Area}: {key}", kind, ArchitectureEvidenceState.Confirmed, [Doc(evidence)],
                            [new("Source contract", sourceContract.Name, sourceContract.File, sourceContract.Line, sourceContract.Id)],
                            $"{change.Area} {change.Detail} — {(kind == DriftDifferenceKind.MissingOnRight ? "in the generated contract only" : kind == DriftDifferenceKind.MissingOnLeft ? "in the source contract only" : "differs between the contracts")}.",
                            "A generated contract and a source contract of the same scope disagree; either may be outdated. Source is the stronger implementation evidence.", evidence.FreshnessStatus);
                    }
            comparisons.Add(new(family, sink.Count > before ? GeneratedComparisonState.PotentiallyDrifted : GeneratedComparisonState.Equivalent,
                $"{generatedContracts.Count} generated vs {sourceContracts.Count} source contract(s).", sink.Count - before));
        }

        // 5. Messaging resources.
        var docResources = docs.SelectMany(d => d.Evidence.Keys.MessagingResources.Select(r => (Resource: r, d.Evidence))).ToList();
        if (docResources.Count > 0)
        {
            if (source.MessagingResources.Count == 0)
                comparisons.Add(new("Messaging resources", GeneratedComparisonState.UnableToCompare, $"No messaging resource names resolved from source or configuration in {sourceScope}.", 0));
            else
            {
                var before = sink.Count;
                var sourceNames = source.MessagingResources.Select(StructuredKeys.Resource).ToHashSet(StringComparer.Ordinal);
                foreach (var group in docResources.GroupBy(x => StructuredKeys.Resource(x.Resource)).Where(g => !sourceNames.Contains(g.Key) && !LiteralInSource(g.First().Resource, scopeSource)))
                    sink.Add(drift, "Messaging resources", group.First().Resource, DriftDifferenceKind.MissingOnRight, ArchitectureEvidenceState.StronglySupported,
                        group.Select(x => Doc(x.Evidence)).DistinctBy(x => x.Path).ToList(), [new("Source/configuration", $"{source.MessagingResources.Count} resource name(s) in {sourceScope}", GeneratedDocumentationDetector.Safe(root))],
                        $"Generated documentation names messaging resource '{group.First().Resource}'; neither source messaging evidence nor configuration of {sourceScope} names it.",
                        "Documented ≠ declared ≠ observed at runtime. The name may come from another module, an environment-specific setting or be outdated.", Worst(group.Select(x => x.Evidence)));
                var docNames = docResources.Select(x => StructuredKeys.Resource(x.Resource)).ToHashSet(StringComparer.Ordinal);
                foreach (var resource in source.MessagingResources.Where(r => moduleScope && !docNames.Contains(StructuredKeys.Resource(r))))
                    sink.Add(drift, "Messaging resources", resource, DriftDifferenceKind.MissingOnLeft, ArchitectureEvidenceState.StronglySupported,
                        docs.Where(d => d.Detected.Kind == GeneratedDocumentKind.Messaging).Select(d => Doc(d.Evidence)).DefaultIfEmpty(Doc(docResources[0].Evidence)).ToList(),
                        [new("Source/configuration", resource, GeneratedDocumentationDetector.Safe(root))],
                        $"Source or configuration of {sourceScope} names messaging resource '{resource}'; no generated messaging document names it.",
                        "The resource may be new since the documentation was generated, or intentionally undocumented.", Worst(docs.Select(d => d.Evidence)));
                comparisons.Add(new("Messaging resources", sink.Count > before ? GeneratedComparisonState.PotentiallyDrifted : GeneratedComparisonState.Equivalent,
                    $"{docNames.Count} documented vs {sourceNames.Count} source/configuration resource name(s).", sink.Count - before));
            }
        }

        // 6. Data model: documented entities vs Database analysis of the same scope.
        var docEntities = docs.Where(d => d.Detected.Kind == GeneratedDocumentKind.DataModel).SelectMany(d => d.Evidence.Keys.Entities.Select(e => (Entity: e, d.Evidence))).ToList();
        if (docEntities.Count > 0)
        {
            if (source.Entities.Count == 0)
                comparisons.Add(new("Data model", GeneratedComparisonState.UnableToCompare, $"No tables or entities resolved by the Database analysis in {sourceScope}.", 0));
            else
            {
                var before = sink.Count;
                var sourceNames = source.Entities.Select(e => e.Split('|').Select(StructuredKeys.Name).ToHashSet(StringComparer.Ordinal)).ToList();
                bool Known(string entity) => entity.Split('|').Select(StructuredKeys.Name).Any(n => sourceNames.Any(s => s.Contains(n)));
                foreach (var group in docEntities.GroupBy(x => x.Entity, StringComparer.OrdinalIgnoreCase).Where(g => !Known(g.Key)))
                    sink.Add(drift, "Data model", group.Key.Replace("|", " / "), DriftDifferenceKind.MissingOnRight, ArchitectureEvidenceState.StronglySupported,
                        group.Select(x => Doc(x.Evidence)).DistinctBy(x => x.Path).ToList(), [new("Database analysis", $"{source.Entities.Count} table(s)/entities in {sourceScope}", GeneratedDocumentationDetector.Safe(root))],
                        $"Generated data-model documentation names '{group.Key.Replace("|", " / ")}'; the Database analysis of {sourceScope} has no table or entity of that name.",
                        "The entity may have been renamed or removed, or be mapped in a way the Database analysis does not resolve.", Worst(group.Select(x => x.Evidence)));
                var docNames = docEntities.SelectMany(x => x.Entity.Split('|')).Select(StructuredKeys.Name).ToHashSet(StringComparer.Ordinal);
                foreach (var entity in source.Entities.Where(e => moduleScope && !e.Split('|').Select(StructuredKeys.Name).Any(docNames.Contains)))
                    sink.Add(drift, "Data model", entity.Replace("|", " / "), DriftDifferenceKind.MissingOnLeft, ArchitectureEvidenceState.StronglySupported,
                        docs.Where(d => d.Detected.Kind == GeneratedDocumentKind.DataModel).Select(d => Doc(d.Evidence)).ToList(),
                        [new("Database analysis", entity.Replace("|", " / "), GeneratedDocumentationDetector.Safe(root))],
                        $"The Database analysis of {sourceScope} has '{entity.Replace("|", " / ")}'; the generated data-model documentation does not name it.",
                        "The table may be new since generation, a staging/infrastructure table, or intentionally undocumented.", Worst(docs.Select(d => d.Evidence)));
                comparisons.Add(new("Data model", sink.Count > before ? GeneratedComparisonState.PotentiallyDrifted : GeneratedComparisonState.Equivalent,
                    $"{docEntities.Select(e => e.Entity).Distinct(StringComparer.OrdinalIgnoreCase).Count()} documented vs {source.Entities.Count} source table(s)/entities.", sink.Count - before));
            }
        }

        // 7. Configuration keys: documented keys vs configuration files and literal key reads in source. Values are never compared or shown.
        var docKeys = docs.SelectMany(d => d.Evidence.Keys.ConfigurationKeys.Select(k => (Key: k, d.Evidence))).ToList();
        if (docKeys.Count > 0)
        {
            if (source.ConfigurationKeys.Count == 0 && scopeSource.All(f => !f.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)))
                comparisons.Add(new("Configuration keys", GeneratedComparisonState.UnableToCompare, $"No configuration files or C# source in {sourceScope}.", 0));
            else
            {
                var before = sink.Count;
                var sourceKeys = source.ConfigurationKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
                bool Known(string key)
                {
                    var normalized = StructuredKeys.ConfigurationKey(key);
                    // Array elements ("Section:Items[0]:Name") are not walked by the configuration key inventory: the array's own key counts.
                    var arrayRoot = Regex.Match(normalized, @"^(.*?)\[\d+\]");
                    if (arrayRoot.Success && sourceKeys.Contains(arrayRoot.Groups[1].Value.TrimEnd(':'))) return true;
                    return sourceKeys.Contains(normalized) || sourceKeys.Any(k => k.StartsWith(normalized + ":", StringComparison.Ordinal)) || LiteralInSource(key, scopeSource)
                        || LiteralInSource(key.Replace(":", "__"), scopeSource);
                }
                foreach (var group in docKeys.GroupBy(x => StructuredKeys.ConfigurationKey(x.Key)).Where(g => !Known(g.First().Key)).Take(MaxCandidatesPerFamily))
                    sink.Add(drift, "Configuration keys", group.First().Key, DriftDifferenceKind.MissingOnRight, ArchitectureEvidenceState.Inferred,
                        group.Select(x => Doc(x.Evidence)).DistinctBy(x => x.Path).ToList(), [new("Configuration evidence", $"{sourceKeys.Count} key(s) in {sourceScope}", GeneratedDocumentationDetector.Safe(root))],
                        $"Generated documentation names configuration key '{group.First().Key}'; no configuration file of {sourceScope} declares it and no source file reads it literally.",
                        "The key may be optional, read through a section/options binding, supplied by the environment, or outdated. Values are never compared or shown.", Worst(group.Select(x => x.Evidence)));
                comparisons.Add(new("Configuration keys", sink.Count > before ? GeneratedComparisonState.PotentiallyDrifted : GeneratedComparisonState.Equivalent,
                    $"{docKeys.Select(k => k.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count()} documented key(s) checked against {sourceKeys.Count} configuration key(s) and literal reads.", sink.Count - before));
            }
        }

        // 8. Architecture components: dotted project identifiers of this scope's naming root vs source projects; deployable components not named.
        var docComponents = docs.Where(d => d.Detected.Kind is GeneratedDocumentKind.Architecture or GeneratedDocumentKind.Overview).SelectMany(d => d.Evidence.Keys.Components.Select(c => (Component: c, d.Evidence))).ToList();
        var prefix = NamingRoot(source.Projects);
        if (docs.Any(d => d.Detected.Kind is GeneratedDocumentKind.Architecture or GeneratedDocumentKind.Overview))
        {
            if (prefix is null)
                comparisons.Add(new("Architecture components", GeneratedComparisonState.UnableToCompare, "Source projects of this scope share no dotted naming root; documented component names cannot be attributed.", 0));
            else
            {
                var before = sink.Count;
                foreach (var group in docComponents.Where(c => c.Component.StartsWith(prefix + ".", StringComparison.Ordinal) && !source.Projects.Any(p => c.Component.Equals(p, StringComparison.OrdinalIgnoreCase)
                        || c.Component.StartsWith(p + ".", StringComparison.OrdinalIgnoreCase))).GroupBy(c => c.Component, StringComparer.OrdinalIgnoreCase))
                    sink.Add(drift, "Architecture components", group.Key, DriftDifferenceKind.NameMismatch, ArchitectureEvidenceState.StronglySupported,
                        group.Select(x => Doc(x.Evidence)).DistinctBy(x => x.Path).ToList(), [new("Source projects", $"{source.Projects.Count} project(s) named {prefix}.*", GeneratedDocumentationDetector.Safe(root))],
                        $"Generated documentation names '{group.Key}' under this scope's naming root ({prefix}); no source project of that name (or containing it) exists.",
                        "The project may have been renamed or removed, or the name may refer to something outside this snapshot.", Worst(group.Select(x => x.Evidence)));
                var architectureText = string.Join("\n", docs.Where(d => d.Detected.Kind is GeneratedDocumentKind.Architecture or GeneratedDocumentKind.Overview).Select(d => d.Detected.Content));
                foreach (var component in source.DeployableComponents.Where(c => moduleScope && architectureText.IndexOf(c, StringComparison.OrdinalIgnoreCase) < 0))
                    sink.Add(drift, "Architecture components", component, DriftDifferenceKind.MissingOnLeft, ArchitectureEvidenceState.StronglySupported,
                        docs.Where(d => d.Detected.Kind is GeneratedDocumentKind.Architecture or GeneratedDocumentKind.Overview).Select(d => Doc(d.Evidence)).ToList(),
                        [new("Architecture analysis", component, GeneratedDocumentationDetector.Safe(root))],
                        $"Source Analysis found deployable component '{component}' in {sourceScope}; no generated overview or architecture document names it.",
                        "The component may be new since generation or described under another name.", Worst(docs.Select(d => d.Evidence)));
                comparisons.Add(new("Architecture components", sink.Count > before ? GeneratedComparisonState.PotentiallyDrifted : GeneratedComparisonState.Equivalent,
                    $"{docComponents.Select(c => c.Component).Distinct(StringComparer.OrdinalIgnoreCase).Count()} documented identifier(s); {source.DeployableComponents.Count} deployable component(s).", sink.Count - before));
            }
        }
    }

    /// <summary>Spec-Kit contracts (specs/&lt;feature&gt;/contracts/ next to a spec.md) are AUTHORED specification contracts: comparing them with a
    /// generated contract of the same scope is SpecificationDrift. Operations/fields the specification declares but the generated contract lacks,
    /// and fields whose type differs. Extra generated operations are not reported (a feature contract covers part of an API).</summary>
    private static void CompareSpecificationContracts(IqrSourceSnapshot snapshot, string moduleId, string root,
        List<(DetectedDocument Detected, GeneratedDocumentationEvidence Evidence, DocumentStructure Structure)> docs, IReadOnlyList<SourceContract> contracts, List<string> allPaths,
        List<GeneratedComparison> comparisons, List<CrossArtifactDriftCandidate> drift)
    {
        var sink = new DriftSink(snapshot.Id, moduleId, CrossArtifactDriftType.SpecificationDrift);
        var safeRoot = GeneratedDocumentationDetector.Safe(root);
        var authored = contracts.Where(c => SpecKitContract(c.File) && (root.Length == 0 || c.File.StartsWith(safeRoot + "/", StringComparison.Ordinal))
            && allPaths.Any(p => p.EndsWith("/spec.md", StringComparison.OrdinalIgnoreCase) && GeneratedDocumentationDetector.Safe(p) == FeatureFolder(c.File) + "/spec.md")).ToList();
        if (authored.Count == 0) return;
        var before = sink.Count;
        var compared = 0;
        foreach (var (detected, evidence, _) in docs)
        {
            foreach (var generated in evidence.RelatedContractEvidenceIds.Select(id => contracts.FirstOrDefault(c => c.Id == id)).Where(c => c is not null))
            {
                foreach (var spec in authored.Where(a => a.Type == generated!.Type))
                {
                    compared++;
                    DriftEvidenceRef Left() => new("Specification contract", $"{spec.Type} · {System.IO.Path.GetFileName(spec.File)}", spec.File, spec.Line, spec.Id);
                    DriftEvidenceRef Right() => new("Generated documentation", $"{GeneratedDocumentationText.Label(evidence.DocumentKind)} · {System.IO.Path.GetFileName(evidence.SafeRelativePath)}", evidence.SafeRelativePath, 0, evidence.EvidenceId, evidence.ContentFingerprint);
                    var generatedOps = generated!.Operations.ToList();
                    foreach (var op in spec.Operations.Where(o => !generatedOps.Any(g => SameOperation(o, g))).Take(MaxCandidatesPerFamily))
                        sink.Add(drift, "Specification contract", $"{spec.File} | {op.Name}", DriftDifferenceKind.MissingOnRight, ArchitectureEvidenceState.Confirmed, [Left()], [Right()],
                            $"The specification contract declares '{op.Name}'; the generated contract of the same scope does not.",
                            "The specification may describe intent not (yet) implemented, a later feature may have changed the operation, or the generated documentation may be outdated. The generated document does not supersede the specification.",
                            evidence.FreshnessStatus);
                    var generatedTypes = generated.Types.ToDictionary(t => t.Name, StringComparer.Ordinal);
                    foreach (var type in spec.Types.Where(t => generatedTypes.ContainsKey(t.Name)))
                        foreach (var field in type.Fields)
                        {
                            var match = generatedTypes[type.Name].Fields.FirstOrDefault(f => f.Name == field.Name);
                            if (match is null)
                                sink.Add(drift, "Specification contract", $"{spec.File} | {type.Name}.{field.Name}", DriftDifferenceKind.MissingOnRight, ArchitectureEvidenceState.Confirmed, [Left()], [Right()],
                                    $"The specification contract declares field {type.Name}.{field.Name}; the generated contract's {type.Name} has no such field.",
                                    "Requires review: either artifact may be outdated; BirkNext does not decide which.", evidence.FreshnessStatus);
                            else if (!string.Equals(match.Type, field.Type, StringComparison.Ordinal))
                                sink.Add(drift, "Specification contract", $"{spec.File} | {type.Name}.{field.Name}", DriftDifferenceKind.ValueMismatch, ArchitectureEvidenceState.Confirmed,
                                    [Left() with { Value = field.Type }], [Right() with { Value = match.Type }],
                                    $"{type.Name}.{field.Name} is {field.Type} in the specification contract and {match.Type} in the generated contract.",
                                    "Requires review: either artifact may be outdated; BirkNext does not decide which.", evidence.FreshnessStatus);
                        }
                }
            }
        }
        if (compared > 0)
            comparisons.Add(new("Specification contracts", sink.Count > before ? GeneratedComparisonState.PotentiallyDrifted : GeneratedComparisonState.Equivalent,
                $"{authored.Count} specification contract(s) compared with generated contracts of the same type.", sink.Count - before));
    }

    private static bool SameOperation(ContractOperation spec, ContractOperation generated)
    {
        if (spec.Path is not null && generated.Path is not null && spec.Kind.Length > 0 && generated.Kind.Length > 0)
            return StructuredKeys.SameRoute(StructuredKeys.Route(spec.Kind, spec.Path), StructuredKeys.Route(generated.Kind, generated.Path));
        return string.Equals(spec.Name, generated.Name, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool SpecKitContract(string file) => Regex.IsMatch(file, @"(?i)(^|/)specs/[^/]+/contracts/");
    private static string FeatureFolder(string file) { var m = Regex.Match(file, @"(?i)^(.*?specs/[^/]+)/contracts/"); return m.Success ? m.Groups[1].Value : ""; }

    private static bool LiteralInSource(string value, List<SourceFile> files)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length < 3) return false;
        var quoted = "\"" + value + "\"";
        return files.Any(f => f.Content.Contains(quoted, StringComparison.Ordinal));
    }

    /// <summary>The dotted naming root shared by a scope's projects ("Shop.Orders" for Shop.Orders.Api + Shop.Orders.Domain), with at least two segments.</summary>
    internal static string? NamingRoot(IReadOnlyList<string> projects)
    {
        if (projects.Count == 0) return null;
        var parts = projects.Select(p => p.Split('.')).ToList();
        var common = new List<string>();
        for (var i = 0; i < parts.Min(p => p.Length); i++)
        {
            var segment = parts[0][i];
            if (parts.All(p => p[i].Equals(segment, StringComparison.OrdinalIgnoreCase))) common.Add(segment); else break;
        }
        if (projects.Count == 1 && common.Count == parts[0].Length) common.RemoveAt(common.Count - 1);
        return common.Count >= 2 ? string.Join('.', common) : null;
    }
}

/// <summary>Collects drift candidates, one per logical mismatch (drift type + module + family + key + difference), with stable ids.</summary>
internal sealed class DriftSink(Guid snapshotId, string moduleId, CrossArtifactDriftType type)
{
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    public int Count { get; private set; }

    public void Add(List<CrossArtifactDriftCandidate> target, string family, string key, DriftDifferenceKind kind, ArchitectureEvidenceState confidence,
        List<DriftEvidenceRef> left, List<DriftEvidenceRef> right, string explanation, string whyReview, GeneratedDocumentationFreshnessStatus? freshness,
        string? artifactFingerprint = null, List<string>? related = null)
    {
        var identity = $"{type}|{moduleId}|{family}|{key}|{kind}";
        if (!_seen.Add(identity))
        {
            var existing = target.FirstOrDefault(c => c.Id == Id(identity));
            if (existing is not null)
            {
                existing.LeftEvidence.AddRange(left.Where(l => existing.LeftEvidence.All(e => e.Path != l.Path)));
                existing.RightEvidence.AddRange(right.Where(r => existing.RightEvidence.All(e => e.Path != r.Path || e.Label != r.Label)));
            }
            return;
        }
        Count++;
        target.Add(new CrossArtifactDriftCandidate
        {
            Id = Id(identity), DriftType = type, ModuleId = moduleId, Family = family, StructuredKey = SourceEvidenceRedaction.SafePath(key), DifferenceKind = kind, Confidence = confidence,
            LeftEvidence = left, RightEvidence = right, SourceSnapshotId = snapshotId, ArtifactFingerprint = artifactFingerprint, Explanation = explanation, WhyReview = whyReview,
            GeneratedFreshness = freshness, RelatedFindings = related ?? [],
        });
    }

    private static string Id(string identity) => "drift:" + GeneratedDocumentationDetector.Fingerprint(identity)[..20];
}
