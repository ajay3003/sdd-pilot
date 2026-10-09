using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Reflection;
using BirkNext.MarkdownDiagnostics;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.Web.Services.SampleProjects;
using Microsoft.Extensions.Configuration;

namespace BirkNext.Api.Services;

/// <summary>Runs deterministic diagnostics over generated in-memory documents. It never reads or changes workspace state.</summary>
public sealed class MarkdownDiagnosticsService(IConfiguration configuration)
{
    private static readonly (string Role, string Name, string Text)[] Fixtures = BuildFixtures();

    public ContentIntegrityRun RunContentIntegrity(CancellationToken cancellationToken = default)
    {
        var started = DateTimeOffset.UtcNow;
        var docs = new List<ContentIntegrityDocument>();
        var archiveBytes = BuildArchive();
        var archiveRead = IqrSourceArchiveReader.ReadDetailed("diagnostic-fixtures.zip", archiveBytes, captureDocuments: true, cancellationToken);
        if (!archiveRead.IsValid || archiveRead.Workspace?.DocumentFiles is null)
            throw new InvalidOperationException("The generated Markdown archive was rejected by the production Project Import archive reader.");
        var extracted = archiveRead.Workspace.DocumentFiles.ToDictionary(d => System.IO.Path.GetFileName(d.Path.Replace('\\', '/')), d => d.Content, StringComparer.OrdinalIgnoreCase);
        foreach (var fixture in Fixtures)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceBytes = Encoding.UTF8.GetBytes(fixture.Text);
            var stored = extracted[fixture.Name];
            var storedBytes = Encoding.UTF8.GetBytes(stored);
            var source = Encoding.UTF8.GetString(sourceBytes);
            var tokens = MarkdownTokenizer.Tokenize(stored);
            var sourceLines = source.Split('\n');
            var parserWarnings = ParserWarnings(stored).ToList();
            var extractorCompleted = TryRunRoleExtractor(fixture.Role, stored, parserWarnings);
            var parserEof = extractorCompleted && tokens.Count == sourceLines.Length && tokens.LastOrDefault()?.LineIndex == sourceLines.Length - 1;
            var exact = sourceBytes.AsSpan().SequenceEqual(storedBytes);
            var canonicalMatch = CanonicalHash(sourceBytes) == CanonicalHash(storedBytes);
            var truncation = !canonicalMatch || !parserEof;
            var documentStatus = truncation ? DiagnosticStatus.Fail : parserWarnings.Count > 0 || !exact ? DiagnosticStatus.Partial : DiagnosticStatus.Pass;
            var lastHeading = tokens.LastOrDefault(t => t.Kind == MarkdownTokenKind.Heading)?.Content;
            docs.Add(new(fixture.Name, fixture.Role, fixture.Name, sourceBytes.Length, source.Length, sourceLines.Length, Hash(sourceBytes), CanonicalHash(sourceBytes),
                storedBytes.Length, stored.Length, stored.Split('\n').Length, Hash(storedBytes), CanonicalHash(storedBytes), exact, canonicalMatch,
                exact ? "Exact" : canonicalMatch ? "NormalizedEquivalent" : "Mismatch", parserEof,
                parserEof ? stored.Length : tokens.Sum(t => t.RawLine.Length), parserEof ? tokens.Count : tokens.Count,
                parserEof ? sourceLines.Length : (tokens.LastOrDefault()?.LineIndex ?? -1) + 1, lastHeading, parserWarnings,
                stored.Contains("BIRKNEXT-INTEGRITY-START", StringComparison.Ordinal),
                stored.Contains("BIRKNEXT-INTEGRITY-MIDDLE", StringComparison.Ordinal),
                stored.Contains("BIRKNEXT-INTEGRITY-END", StringComparison.Ordinal), truncation, documentStatus));
        }
        var real = AppendConfiguredArchive(docs, cancellationToken);
        var completed = DateTimeOffset.UtcNow;
        var status = docs.Any(d => d.PotentialTruncation) ? DiagnosticStatus.Fail : docs.Any(d => d.Status == DiagnosticStatus.Partial) ? DiagnosticStatus.Partial : DiagnosticStatus.Pass;
        return new(Guid.NewGuid(), started, completed, status, docs, real.Used, real.Status, real.Note);
    }

    public ExplorerCoverageRun RunExplorerCoverage(CancellationToken cancellationToken = default)
    {
        var started = DateTimeOffset.UtcNow;
        var documents = new List<ExplorerCoverageDocument>();
        foreach (var fixture in Fixtures)
        {
            cancellationToken.ThrowIfCancellationRequested();
            documents.Add(AnalyzeCoverage(fixture.Role, fixture.Name, fixture.Text));
        }
        var real = AnalyzeConfiguredCoverage(documents, cancellationToken);
        var completed = DateTimeOffset.UtcNow;
        return new(Guid.NewGuid(), started, completed,
            documents.Any(d => d.MissingCount > 0) ? DiagnosticStatus.Fail : documents.Any(d => d.Status == DiagnosticStatus.Partial) ? DiagnosticStatus.Partial : DiagnosticStatus.Pass,
            documents, real.Used, real.Status, real.Note);
    }

    private (bool Used, DiagnosticStatus Status, string? Note) AppendConfiguredArchive(List<ContentIntegrityDocument> docs, CancellationToken ct)
    {
        var path = configuration["ProjectCompatibility:AcceptanceArchivePath"];
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return (false, DiagnosticStatus.NotRun, "No configured acceptance ZIP was available; generated fixtures were checked.");
        try
        {
            var info = new FileInfo(path);
            if (info.Length <= 0 || info.Length > IqrSourceArchiveReader.MaxArchiveBytes)
                return (false, DiagnosticStatus.Fail, "Configured archive is empty or exceeds the supported compressed archive limit.");
            ct.ThrowIfCancellationRequested();
            var bytes = File.ReadAllBytes(path);
            var read = IqrSourceArchiveReader.ReadDetailed("acceptance.zip", bytes, captureDocuments: true, captureRawDocuments: true, ct);
            if (!read.IsValid || read.Workspace?.DocumentFiles is null)
                return (false, DiagnosticStatus.Fail, "Configured archive was rejected by the production archive reader.");
            var extracted = read.Workspace.DocumentFiles.ToDictionary(d => NormalizePath(d.Path), d => d.Content, StringComparer.OrdinalIgnoreCase);
            var roles = ArtifactDocumentDiscovery.Classify(read.Workspace.DocumentFiles.Select(d =>
                new ArtifactDocumentDiscovery.Candidate(NormalizePath(d.Path), System.IO.Path.GetFileName(d.Path), d.Content)))
                .ToDictionary(d => NormalizePath(d.RelativePath), d => d.Role?.ToString() ?? "Unclassified", StringComparer.OrdinalIgnoreCase);
            foreach (var rawDocument in read.Workspace.RawDocumentFiles ?? [])
            {
                ct.ThrowIfCancellationRequested();
                var raw = rawDocument.Bytes;
                var decoded = new UTF8Encoding(false, false).GetString(raw).TrimStart('\uFEFF');
                var relative = NormalizePath(rawDocument.Path);
                if (!extracted.TryGetValue(relative, out var stored))
                {
                    docs.Add(new(System.IO.Path.GetFileName(relative), roles.GetValueOrDefault(relative, "Unclassified"), relative,
                        raw.Length, decoded.Length, decoded.Split('\n').Length, Hash(raw), CanonicalHash(raw),
                        0, 0, 0, "", "", false, false, "NotCapturedByImportReader", false, 0, 0, 0, null,
                        ["Document was not captured by the bounded Project Import reader (for example, a per-document/count limit)."],
                        false, false, false, false, DiagnosticStatus.Partial));
                    continue;
                }
                var storedBytes = Encoding.UTF8.GetBytes(stored);
                var tokens = MarkdownTokenizer.Tokenize(stored);
                var lines = stored.Split('\n');
                var warnings = ParserWarnings(stored).ToList();
                var role = roles.GetValueOrDefault(relative, "Unclassified");
                var extractorCompleted = TryRunRoleExtractor(role, stored, warnings);
                var exact = raw.AsSpan().SequenceEqual(storedBytes);
                var canonical = CanonicalHash(raw) == CanonicalHash(storedBytes);
                var eof = tokens.Count == lines.Length && tokens.LastOrDefault()?.LineIndex == lines.Length - 1 &&
                    (role is not ("Specification" or "Constitution" or "Plan" or "Tasks" or "Data Model") || extractorCompleted);
                var truncated = !canonical || !eof;
                var docStatus = truncated ? DiagnosticStatus.Fail : warnings.Count > 0 ? DiagnosticStatus.Partial : exact ? DiagnosticStatus.Pass : DiagnosticStatus.Partial;
                docs.Add(new(System.IO.Path.GetFileName(relative), roles.GetValueOrDefault(relative, "Unclassified"), relative,
                    raw.Length, decoded.Length, decoded.Split('\n').Length, Hash(raw), CanonicalHash(raw), storedBytes.Length,
                    stored.Length, lines.Length, Hash(storedBytes), CanonicalHash(storedBytes), exact, canonical,
                    exact ? "Exact" : canonical ? "NormalizedEquivalent" : "Mismatch", eof, eof ? stored.Length : tokens.Sum(t => t.RawLine.Length),
                    tokens.Count, eof ? lines.Length : (tokens.LastOrDefault()?.LineIndex ?? -1) + 1,
                    tokens.LastOrDefault(t => t.Kind == MarkdownTokenKind.Heading)?.Content, warnings,
                    stored.Contains("BIRKNEXT-INTEGRITY-START", StringComparison.Ordinal), stored.Contains("BIRKNEXT-INTEGRITY-MIDDLE", StringComparison.Ordinal),
                    stored.Contains("BIRKNEXT-INTEGRITY-END", StringComparison.Ordinal), truncated, docStatus));
            }
            return (true, docs.Any(d => d.PotentialTruncation) ? DiagnosticStatus.Fail : docs.Any(d => d.Status == DiagnosticStatus.Partial) ? DiagnosticStatus.Partial : DiagnosticStatus.Pass,
                "Configured archive analyzed with the production reader; archive-relative paths only are returned.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { return (false, DiagnosticStatus.Fail, $"Configured archive diagnostic failed safely ({ex.GetType().Name})."); }
    }

    private (bool Used, DiagnosticStatus Status, string? Note) AnalyzeConfiguredCoverage(List<ExplorerCoverageDocument> docs, CancellationToken ct)
    {
        var path = configuration["ProjectCompatibility:AcceptanceArchivePath"];
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return (false, DiagnosticStatus.NotRun, "No configured acceptance ZIP was available; generated fixtures were checked.");
        try
        {
            var info = new FileInfo(path);
            if (info.Length <= 0 || info.Length > IqrSourceArchiveReader.MaxArchiveBytes)
                return (false, DiagnosticStatus.Fail, "Configured archive is empty or exceeds the supported compressed archive limit.");
            var read = IqrSourceArchiveReader.ReadDetailed("acceptance.zip", File.ReadAllBytes(path), captureDocuments: true, ct);
            if (!read.IsValid || read.Workspace?.DocumentFiles is null)
                return (false, DiagnosticStatus.Fail, "Configured archive was rejected by the production archive reader.");
            var classified = ArtifactDocumentDiscovery.Classify(read.Workspace.DocumentFiles.Select(d =>
                new ArtifactDocumentDiscovery.Candidate(NormalizePath(d.Path), System.IO.Path.GetFileName(d.Path), d.Content)));
            var candidates = classified.Where(d => d.Status == ArtifactDiscoveryStatus.Detected && d.Role is not null).ToList();
            const int maxCandidates = 500;
            foreach (var candidate in candidates.Take(maxCandidates))
            {
                ct.ThrowIfCancellationRequested();
                var file = read.Workspace.DocumentFiles.First(d => NormalizePath(d.Path).Equals(NormalizePath(candidate.RelativePath), StringComparison.OrdinalIgnoreCase));
                var role = candidate.Role switch { WorkspaceArtifactType.DataModel => "Data Model", _ => candidate.Role!.Value.ToString() };
                if (role is "Specification" or "Constitution" or "Plan" or "Tasks" or "Data Model")
                    docs.Add(AnalyzeCoverage(role, System.IO.Path.GetFileName(candidate.RelativePath), file.Content, includePreview: false));
            }
            var capped = candidates.Count > maxCandidates;
            var missing = docs.Sum(d => d.MissingCount);
            var status = capped ? DiagnosticStatus.Partial : missing > 0 ? DiagnosticStatus.Fail : docs.Any(d => d.Status == DiagnosticStatus.Partial) ? DiagnosticStatus.Partial : DiagnosticStatus.Pass;
            return (true, status, $"Analyzed {Math.Min(candidates.Count, maxCandidates)} detected role candidates independently; no candidate authority was selected.{(capped ? " Candidate analysis cap reached." : "")}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { return (false, DiagnosticStatus.Fail, $"Configured archive coverage analysis failed safely ({ex.GetType().Name})."); }
    }

    private static string NormalizePath(string path) => path.Replace('\\', '/').TrimStart('/');

    private static ExplorerCoverageDocument AnalyzeCoverage(string role, string displayName, string text, bool includePreview = true)
    {
        var parsed = ParseWithProductionExplorer(role, text);
        var projections = CollectProjectionProvenance(parsed);
        var sourceNotes = parsed.GetType().GetProperty("UnmappedSourceBlocks")?.GetValue(parsed) as IEnumerable<MarkdownSourceNote>
            ?? [];
        var noteLines = sourceNotes.ToDictionary(note => (note.StartLine, note.Text.Trim()));
        var tokens = MarkdownTokenizer.Tokenize(text);
        var artifactFingerprint = MarkdownTokenizer.DocumentFingerprint(text);
        var blocks = new List<ExplorerCoverageBlock>();
        foreach (var token in tokens.Where(t => t.Kind != MarkdownTokenKind.Blank))
        {
            var isFenceDelimiter = token.Kind is MarkdownTokenKind.FencedCodeStart or MarkdownTokenKind.FencedCodeEnd;
            var isUnmodeledFenceBody = token.Kind == MarkdownTokenKind.FencedCodeLine &&
                role is "Tasks" or "Data Model";
            var fingerprint = Hash(Encoding.UTF8.GetBytes(token.RawLine.Trim()));
            var blockId = MarkdownTokenizer.CreateSourceBlockId(artifactFingerprint, token.LineIndex, token.RawLine);
            var decorative = token.Kind is MarkdownTokenKind.HorizontalRule or MarkdownTokenKind.TableSeparator;
            var representedBySourceNotes = !decorative && noteLines.ContainsKey((token.LineIndex + 1, token.RawLine.Trim()));
            var destinations = projections.Where(p => p.Sources.Any(s =>
                token.LineIndex + 1 >= s.StartLine && token.LineIndex + 1 <= s.EndLine)).ToList();
            var representedByDirectProjection = destinations.Any(p => p.ProjectionKind == "DirectContent");
            var found = decorative || representedBySourceNotes || destinations.Count > 0;
            var ignoredFenceDelimiter = !found && isFenceDelimiter;
            var unsupportedFenceBody = !found && isUnmodeledFenceBody;
            var classification = decorative || ignoredFenceDelimiter ? CoverageClassification.IntentionallyIgnored :
                unsupportedFenceBody ? CoverageClassification.Unsupported : found ?
                    representedBySourceNotes || representedByDirectProjection ? CoverageClassification.RepresentedDirectly : CoverageClassification.RepresentedStructurally : CoverageClassification.Missing;
            // Tokenization and the production role parser completed before this block is classified.
            // A missing projection is therefore an extractor/page-model gap, not parser absence.
            var parserEvidence = true;
            var projectionIds = destinations.Select(p => p.ProjectionId).Distinct(StringComparer.Ordinal).ToArray();
            blocks.Add(new(blockId, token.LineIndex + 1, token.LineIndex + 1, token.Kind.ToString(), fingerprint,
                includePreview ? Preview(token.RawLine) : "Preview suppressed for configured local archive.", classification, decorative ? null : found ? representedBySourceNotes ? "Explorer source notes (full source text)" : string.Join(", ", projectionIds) : null,
                decorative ? "Decorative table and horizontal separators do not carry domain content." : ignoredFenceDelimiter ? "Fence delimiters are Markdown presentation syntax; the fenced content is classified separately." :
                    unsupportedFenceBody ? role == "Tasks" ? "Task Explorer does not parse fenced code as task evidence." : "Data Model Explorer does not parse fenced code as entity, field, relationship, or constraint evidence." :
                    found ? representedBySourceNotes ? "The production extractor preserved this authored block in its source-notes output." : representedByDirectProjection ? "The Explorer page model preserves this source block in a rendered free-form section." : "Construction-time source ranges link this block to explicit structured projection provenance." :
                    "No construction-time structured projection provenance or direct source-note destination accounts for this block.",
                decorative ? "markdown.decorative-separator" : ignoredFenceDelimiter ? "markdown.fenced-code-delimiter" : unsupportedFenceBody ? role == "Tasks" ? "tasks.fenced-code-not-modeled" : "data-model.fenced-code-not-modeled" : null,
                CoverageEvidenceStatus.Present,
                decorative ? CoverageEvidenceStatus.NotApplicable : parserEvidence ? CoverageEvidenceStatus.Present : CoverageEvidenceStatus.Absent,
                decorative || ignoredFenceDelimiter || unsupportedFenceBody ? CoverageEvidenceStatus.NotApplicable : found ? CoverageEvidenceStatus.Present : CoverageEvidenceStatus.Absent,
                decorative || ignoredFenceDelimiter || unsupportedFenceBody ? CoverageEvidenceStatus.NotApplicable : representedBySourceNotes || projectionIds.Length > 0 ? CoverageEvidenceStatus.Present :
                    CoverageEvidenceStatus.Absent,
                decorative || ignoredFenceDelimiter || unsupportedFenceBody ? CoverageEvidenceStatus.NotApplicable : CoverageEvidenceStatus.NotVerified,
                classification == CoverageClassification.Missing ? "ProjectionEvidenceMissing" : classification == CoverageClassification.Unsupported ? "UnsupportedConstruct" :
                    classification is CoverageClassification.RepresentedStructurally or CoverageClassification.RepresentedDirectly
                        ? "RenderedComponentEvidenceNotExercised" : null,
                projectionIds));
        }
        var direct = blocks.Count(b => b.Classification == CoverageClassification.RepresentedDirectly);
        var structural = blocks.Count(b => b.Classification == CoverageClassification.RepresentedStructurally);
        var ignored = blocks.Count(b => b.Classification == CoverageClassification.IntentionallyIgnored);
        var unsupported = blocks.Count(b => b.Classification == CoverageClassification.Unsupported);
        var missing = blocks.Count(b => b.Classification == CoverageClassification.Missing);
        var renderUnverified = blocks.Any(b => b.Classification == CoverageClassification.RepresentedStructurally &&
            b.RenderEvidence == CoverageEvidenceStatus.NotVerified);
        return new(role, displayName, blocks.Count, direct, structural, ignored, unsupported, missing,
            missing > 0 ? DiagnosticStatus.Fail : unsupported > 0 || renderUnverified ? DiagnosticStatus.Partial : DiagnosticStatus.Pass,
            blocks, ExpectedDocumentId: artifactFingerprint);
    }

    private static object ParseWithProductionExplorer(string role, string text) => role switch
    {
        "Specification" => SpecExplorerService.Parse(text),
        "Constitution" => new ConstitutionAnalysisService().Parse(text),
        "Plan" => new PlanAnalysisService().Parse(text),
        "Tasks" => TaskExplorerService.Parse(text),
        "Data Model" => new DataModelAnalysisService().Parse(text),
        _ => throw new InvalidOperationException("Unsupported diagnostic fixture role.")
    };

    private static IReadOnlyList<ProjectionProvenance> CollectProjectionProvenance(object root)
    {
        var found = new Dictionary<string, ProjectionProvenance>(StringComparer.Ordinal);
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        Visit(root);
        return found.Values.ToArray();

        void Visit(object? value)
        {
            if (value is null || value is string || value.GetType().IsPrimitive || value.GetType().IsEnum) return;
            if (!value.GetType().IsValueType && !visited.Add(value)) return;
            if (value is ProjectionProvenance provenance)
            {
                found.TryAdd(provenance.ProjectionId, provenance);
                return;
            }
            if (value is System.Collections.IEnumerable enumerable)
            {
                foreach (var item in enumerable) Visit(item);
                return;
            }
            foreach (var property in value.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                if (!property.CanRead || property.GetIndexParameters().Length != 0) continue;
                object? child;
                try { child = property.GetValue(value); }
                catch { continue; }
                Visit(child);
            }
        }
    }

    private static bool TryRunRoleExtractor(string role, string text, List<string> warnings)
    {
        if (role is not ("Specification" or "Constitution" or "Plan" or "Tasks" or "Data Model"))
            return true;
        try
        {
            _ = ParseWithProductionExplorer(role, text);
            return true;
        }
        catch (Exception exception)
        {
            warnings.Add($"{role} Explorer extractor stopped with {exception.GetType().Name}.");
            return false;
        }
    }

    private static (string Role, string Name, string Text)[] BuildFixtures()
    {
        static string Wrap(string title, string body) => $"<!-- BIRKNEXT-INTEGRITY-START -->\n# {title}\nIntroductory context with æ ø å, café, 日本語 and 🧭.\n\n{body}\n\n<!-- BIRKNEXT-INTEGRITY-END -->\nFinal explanatory paragraph at EOF: the complete document must remain available.";
        var spec = "## Requirements\n" + string.Join('\n', Enumerable.Range(1, 240).Select(i => $"- REQ-{i:000}: requirement {i} retains all authored detail and references [design note](https://example.invalid/{i}).")) +
            "\n\n<!-- BIRKNEXT-INTEGRITY-MIDDLE -->\n\n## Acceptance scenarios\n" + string.Join('\n', Enumerable.Range(1, 80).Select(i => $"- Scenario {i}: Given a complete archive, When parsed, Then requirement {i} remains present.")) +
            "\n\n| Index | Value |\n| --- | --- |\n" + string.Join('\n', Enumerable.Range(1, 180).Select(i => $"| {i} | row-{i} |")) +
            "\n\n```csharp\n" + string.Join('\n', Enumerable.Range(1, 260).Select(i => $"// code-{i} with Markdown-like text: ## heading")) + "\n```\n\n- REQ-999: final structured requirement near EOF.";
        var constitution = "## Principles\n" + string.Join('\n', Enumerable.Range(1, 90).Select(i => $"### PP-{i}: Principle {i}\nPrinciple explanation {i} is preserved in full.")) +
            "\n\n<!-- BIRKNEXT-INTEGRITY-MIDDLE -->\n\n## Standards\n" + string.Join('\n', Enumerable.Range(1, 60).Select(i => $"- PS-{i}: Standard {i} remains explicit.")) +
            "\n\n## Governance\nGovernance prose after the final structured item.";
        var plan = "## Architecture\nArchitecture notes and dependencies.\n\n## Phases\n" + string.Join('\n', Enumerable.Range(1, 100).Select(i => $"### Phase {i}\nPhase {i} delivery and test strategy.")) +
            "\n\n<!-- BIRKNEXT-INTEGRITY-MIDDLE -->\n\n## Risks\n" + string.Join('\n', Enumerable.Range(1, 80).Select(i => $"- Risk {i}: dependency delay.")) +
            "\n\n## Deployment\nDeployment and rollback procedure.\nOperations dependency paragraph near EOF.";
        var tasks = "## Phase 1\nImplementation notes.\n" + string.Join('\n', Enumerable.Range(1, 420).Select(i => $"- [ ] T{i:0000} Implement task {i} [REQ-{i:000}] (parallelizable: true)")) +
            "\n\n<!-- BIRKNEXT-INTEGRITY-MIDDLE -->\n\n- [ ] T9999 Final task near EOF.\nTrailing note after final task.";
        var dataModel = "## Structures\n" + string.Join('\n', Enumerable.Range(1, 140).Select(i => $"### Entity{i}\nEntity {i} description.\n| Field | Type |\n| --- | --- |\n| Id | string |\n| Value{i} | text |")) +
            "\n\n<!-- BIRKNEXT-INTEGRITY-MIDDLE -->\n\n## Relationships\nEntities have documented relationships and assumptions.\nTrailing model explanation near EOF.";
        return
        [
            ("Specification", "requirements.md", Wrap("Service requirements", spec)),
            ("Constitution", "governance.md", Wrap("Engineering constitution", constitution)),
            ("Plan", "delivery-plan.md", Wrap("Delivery plan", plan)),
            ("Tasks", "work-items.md", Wrap("Work items", tasks)),
            ("Data Model", "domain.md", Wrap("Data Model", dataModel))
        ];
    }

    private static byte[] BuildArchive()
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var fixture in Fixtures)
            {
                var entry = archive.CreateEntry($"generated-project/{fixture.Name}", CompressionLevel.Fastest);
                using var stream = entry.Open();
                var content = Encoding.UTF8.GetBytes(fixture.Text);
                stream.Write(content);
            }
        }
        return buffer.ToArray();
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static string CanonicalHash(byte[] bytes)
    {
        var text = Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF').Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        return Hash(Encoding.UTF8.GetBytes(text));
    }
    public static string CanonicalFingerprint(string text) => CanonicalHash(Encoding.UTF8.GetBytes(text));
    public static IReadOnlyList<string> ParserWarnings(string markdown)
    {
        var tokens = MarkdownTokenizer.Tokenize(markdown);
        return tokens.Count(t => t.Kind == MarkdownTokenKind.FencedCodeStart) != tokens.Count(t => t.Kind == MarkdownTokenKind.FencedCodeEnd)
            ? ["Unclosed fenced code block; tokenizer continued through EOF as code content."] : [];
    }
    private static string Preview(string value) => value.Length <= 140 ? value : value[..137] + "…";
}
