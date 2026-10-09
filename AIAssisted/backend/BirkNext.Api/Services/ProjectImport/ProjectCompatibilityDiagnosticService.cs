using System.Diagnostics;
using System.IO.Compression;
using Microsoft.Extensions.Configuration;
using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.ProjectImport;
using BirkNext.Web.Services.SampleProjects;

namespace BirkNext.Api.Services.ProjectImport;

/// <summary>
/// Runs deterministic discovery scenarios against generated ZIP bytes. It deliberately calls the same bounded archive reader,
/// Project Import source detector and document classifier as production, without staging or committing an import.
/// </summary>
public sealed class ProjectCompatibilityDiagnosticService(IConfiguration configuration)
{
    private static readonly (string Path, string Content)[] Documents =
    [
        ("notes/governance.md", "# Project Constitution\n\n## Principles\n\n### I. Test first\nChanges require tests.\n"),
        ("requirements/account.md", "# Feature Specification: Account\n\n## User Scenarios & Testing\n\n### User Story 1\n\n## Requirements\n\n- **FR-001**: Accounts can be created.\n"),
        ("delivery/roadmap.md", "# Implementation Plan: Account\n\n## Technical Context\n\n**Language/Version**: C# 12\n"),
        ("work/items.md", "# Tasks: Account\n\n- [ ] T001 Create project\n- [ ] T002 Add account model\n"),
        ("architecture/domain.md", "# Data Model: Account\n\n## Entities\n\n### Account\n\n| Field | Type |\n|---|---|\n| Id | Guid |\n"),
    ];

    private static readonly (string Path, string Content)[] Source =
    [
        ("components/service-a/Service.csproj", "<Project Sdk=\"Microsoft.NET.Sdk.Web\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>"),
        ("components/service-a/Program.cs", "var builder = WebApplication.CreateBuilder(args); var app = builder.Build(); app.MapGet(\"/accounts\", () => 1); app.Run();"),
        ("contracts/accounts.graphql", "type Account { id: ID! }\n"),
        (".gitlab-ci.yml", "stages: [build]\nbuild:\n  script: dotnet build\n"),
    ];

    public ProjectCompatibilityRun Run(CancellationToken cancellationToken = default)
    {
        var started = DateTimeOffset.UtcNow;
        var results = new List<ProjectCompatibilityScenario>();
        void Check(string name, string expected, Func<string> action, bool unsupported = false)
        {
            var timer = Stopwatch.StartNew();
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var observed = action();
                var status = unsupported ? ProjectCompatibilityStatus.Partial : ProjectCompatibilityStatus.Pass;
                results.Add(new(name, status, expected, observed, unsupported ? "Unsupported technology is reported as a tool limitation." : null, timer.ElapsedMilliseconds));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                results.Add(new(name, ProjectCompatibilityStatus.Fail, expected, "The scenario could not complete safely.", ex.GetType().Name, timer.ElapsedMilliseconds));
            }
        }

        var baseline = Inspect(Archive([.. Documents, .. Source]), cancellationToken);
        Check("Baseline mixed project", "Document roles and supported source evidence are discovered.", () =>
            baseline.Valid && baseline.Roles.Contains("Specification") && baseline.SourceDetected ? "Specification and source evidence discovered." : throw new InvalidOperationException("Expected document/source evidence was not detected."));

        var renamedNames = new[] { "governance-notes.md", "requirements-x.md", "roadmap-v2.md", "backlog.md", "domain-structure.md" };
        var renamed = Documents.Select((d, i) => (renamedNames[i], d.Content)).ToArray();
        Check("Renamed Markdown documents", "Content-supported roles remain discoverable after filenames change.", () =>
        {
            var result = Inspect(Archive([.. renamed, .. Source]), cancellationToken);
            var comparison = CompareSemantics(baseline, result);
            return comparison.Equivalent ? comparison.Summary : throw new InvalidOperationException($"Role/content semantics changed after rename. {comparison.Details}");
        });

        var moved = Documents.Select((d, i) => ($"layer-{i}/nested/area/{d.Path.Split('/').Last()}", d.Content)).ToArray();
        Check("Moved and nested documents", "Content-supported roles survive alternate nested folders.", () =>
        {
            var result = Inspect(Archive([.. moved, .. Source]), cancellationToken);
            var comparison = CompareSemantics(baseline, result);
            return comparison.Equivalent ? comparison.Summary : throw new InvalidOperationException($"Role/content semantics changed after move. {comparison.Details}");
        });

        Check("Multiple archive roots", "No single top-level project folder is required.", () =>
        {
            var result = Inspect(Archive([.. Documents.Select((d, i) => ($"root-{i}/{d.Path}", d.Content)), .. Source]), cancellationToken);
            return result.Valid && result.SourceDetected && result.Roles.Contains("Specification") ? "Documents and source discovered across multiple roots." : throw new InvalidOperationException("Multiple roots were not handled.");
        });

        Check("Ambiguous specification", "Both candidates are reported; no arbitrary authority is selected.", () =>
        {
            var docs = Inspect(Archive([.. Documents, ("alternate/requirements.md", Documents[1].Content)]), cancellationToken);
            var duplicateCandidates = ArtifactDocumentDiscovery.Classify(docs.Documents.Select(d => new ArtifactDocumentDiscovery.Candidate(d.Path, System.IO.Path.GetFileName(d.Path), d.Content)));
            var specifications = duplicateCandidates.Where(d => d.Role == BirkNext.Web.Services.WorkspaceArtifactType.Specification).ToList();
            return specifications.Count >= 2 ? $"{specifications.Count} specification candidates retained without a selected authority." : throw new InvalidOperationException("Ambiguous candidates were not retained.");
        });

        Check("Ambiguous plan", "Both plan candidates are retained until a user chooses an authority.", () =>
        {
            var plan = Documents.Single(d => d.Path.EndsWith("roadmap.md", StringComparison.Ordinal));
            var inspected = Inspect(Archive([.. Documents, ("alternate/release-notes.md", plan.Content)]), cancellationToken);
            var candidates = ArtifactDocumentDiscovery.Classify(inspected.Documents.Select(d => new ArtifactDocumentDiscovery.Candidate(d.Path, System.IO.Path.GetFileName(d.Path), d.Content)));
            var plans = candidates.Count(d => d.Role == BirkNext.Web.Services.WorkspaceArtifactType.Plan);
            return plans >= 2 ? $"{plans} plan candidates retained; no authority is selected." : throw new InvalidOperationException("Ambiguous plans were not retained.");
        });

        Check("Documents only", "Documents remain available when source is absent.", () =>
        {
            var result = Inspect(Archive(Documents), cancellationToken);
            return result.Valid && !result.SourceDetected && result.Roles.Contains("Specification") ? "Document roles discovered; source is neutrally absent." : throw new InvalidOperationException("Documents-only archive was not handled.");
        });

        Check("Source only", "Source remains available when documents are absent.", () =>
        {
            var result = Inspect(Archive(Source), cancellationToken);
            return result.Valid && result.SourceDetected && result.Roles.Length == 0 ? "Source evidence discovered; no document roles reported." : throw new InvalidOperationException("Source-only archive was not handled.");
        });

        Check("Missing document role", "Missing roles do not prevent unrelated discovery.", () =>
        {
            var result = Inspect(Archive([.. Documents.Where(d => !d.Path.EndsWith("roadmap.md", StringComparison.Ordinal)), .. Source]), cancellationToken);
            return result.Valid && result.SourceDetected && !result.Roles.Contains("Plan") ? "Source and remaining document roles discovered; Plan absent." : throw new InvalidOperationException("Missing role affected unrelated discovery.");
        });

        Check("Unsupported technology", "Unsupported languages are surfaced without hiding supported evidence.", () =>
        {
            var result = Inspect(Archive([.. Documents, .. Source, ("legacy/Main.java", "class Main {}"), ("legacy/pom.xml", "<project />")]), cancellationToken);
            return result.Valid && result.SourceDetected && result.UnsupportedFiles > 0 ? $"Source found; {result.UnsupportedFiles} unsupported source file(s) identified." : throw new InvalidOperationException("Unsupported source was not reported.");
        }, unsupported: true);

        Check("Duplicate document", "Identical logical documents are represented deterministically, not selected by arbitrary path order.", () =>
        {
            var duplicated = Inspect(Archive([.. Documents, ("copies/specification-copy.md", Documents[1].Content)]), cancellationToken);
            var candidates = ArtifactDocumentDiscovery.Classify(duplicated.Documents.Select(d => new ArtifactDocumentDiscovery.Candidate(d.Path, System.IO.Path.GetFileName(d.Path), d.Content)));
            var sameContent = candidates.Where(d => d.Fingerprint == candidates.First(c => c.Role == BirkNext.Web.Services.WorkspaceArtifactType.Specification).Fingerprint).ToArray();
            return sameContent.Length == 2 && sameContent.Count(d => d.DuplicateOf is not null) == 1
                ? "Both matching documents retained and the duplicate is marked deterministically." : throw new InvalidOperationException("Duplicate document handling changed.");
        });

        Check("Malformed Markdown and unknown files", "Malformed documents do not crash other discovery; unknown files are ignored or reported safely.", () =>
        {
            var entries = Documents.Concat(new (string Path, string Content)[]
            {
                ("odd/broken.md", "# Broken\n| incomplete table\n```csharp\nclass X {"),
                ("misc/readme.txt", "not a supported artifact"),
                ("misc/blob.bin", "\0\u0001\u0002"),
                ("misc/config.toml", "[app]\nname = 'example'")
            }).ToArray();
            var result = Inspect(Archive(entries), cancellationToken);
            return result.Valid && result.Roles.Contains("Specification") ? "Supported documents were still classified; unrelated extensions were safely ignored." : throw new InvalidOperationException("Malformed/unknown files interrupted discovery.");
        });

        Check("Renamed source module", "Supported technology detection does not depend on business module labels.", () =>
        {
            var renamedSource = Source.Select(f => (f.Path.Replace("components/service-a", "modules/green-field", StringComparison.Ordinal), f.Content)).ToArray();
            var original = Inspect(Archive(Source), cancellationToken);
            var renamedResult = Inspect(Archive(renamedSource), cancellationToken);
            var oldTech = BirkNext.Api.Services.SourceAnalysis.Technology.TechnologyInventory.Detect(original.Workspace!).Technologies.Select(t => t.TechnologyId).OrderBy(x => x).ToArray();
            var newTech = BirkNext.Api.Services.SourceAnalysis.Technology.TechnologyInventory.Detect(renamedResult.Workspace!).Technologies.Select(t => t.TechnologyId).OrderBy(x => x).ToArray();
            return renamedResult.Valid && oldTech.SequenceEqual(newTech) ? "Detected technology identities are unchanged after module rename." : throw new InvalidOperationException("Technology detection changed after module rename.");
        });

        Check("Moved contract and pipeline", "Supported contract and pipeline evidence is discovered outside conventional folders.", () =>
        {
            var entries = Source.Select(f => (f.Path.EndsWith("accounts.graphql", StringComparison.Ordinal) ? "misc/api-schema.graphql" : f.Path.EndsWith(".gitlab-ci.yml", StringComparison.Ordinal) ? "automation/.gitlab-ci.yml" : f.Path, f.Content)).ToArray();
            var result = Inspect(Archive(entries), cancellationToken);
            var evidence = result.Workspace?.EvidenceFiles?.Select(f => f.Path).ToArray() ?? [];
            var configurations = result.Workspace?.ConfigurationFiles?.Select(f => f.Path).ToArray() ?? [];
            var pipelineDetected = BirkNext.Api.Services.SourceAnalysis.Technology.TechnologyInventory.Detect(result.Workspace!).Technologies.Any(t => t.TechnologyId == "pipeline.gitlab");
            return evidence.Contains("misc/api-schema.graphql") && configurations.Contains("automation/.gitlab-ci.yml") && pipelineDetected
                ? "Moved GraphQL contract and GitLab pipeline are still discovered by production inventory." : throw new InvalidOperationException("Moved source evidence was missed.");
        });

        Check("Second archive isolation", "A later diagnostic archive contains no evidence from the earlier archive.", () =>
        {
            var projectA = Inspect(Archive([.. Documents, .. Source, ("components/secret-a/OnlyA.cs", "class OnlyA {}")]), cancellationToken);
            var projectB = Inspect(Archive([("other/OnlyB.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />"), ("other/OnlyB.cs", "class OnlyB {}")]), cancellationToken);
            var paths = projectB.Workspace?.AllPaths ?? [];
            return projectA.Workspace?.AllPaths?.Any(p => p.Contains("OnlyA", StringComparison.Ordinal)) == true
                && !paths.Any(p => p.Contains("OnlyA", StringComparison.Ordinal)) && paths.Any(p => p.Contains("OnlyB", StringComparison.Ordinal))
                ? "The second virtual workspace contains only its own archive entries." : throw new InvalidOperationException("Archive evidence leaked between runs.");
        });

        var acceptancePath = configuration["ProjectCompatibility:AcceptanceArchivePath"];
        var realFixtureUsed = false;
        if (!string.IsNullOrWhiteSpace(acceptancePath) && File.Exists(acceptancePath))
        {
            var timer = Stopwatch.StartNew();
            try
            {
                var file = new FileInfo(acceptancePath);
                if (file.Length > IqrSourceArchiveReader.MaxArchiveBytes)
                    results.Add(new("Optional real-project acceptance", ProjectCompatibilityStatus.Fail, "Configured ZIP is within the supported archive size limit.",
                        "The configured archive exceeds the supported size limit.", DurationMilliseconds: timer.ElapsedMilliseconds));
                else
                {
                    var result = Inspect(File.ReadAllBytes(acceptancePath), cancellationToken);
                    realFixtureUsed = true;
                    var status = !result.Valid ? ProjectCompatibilityStatus.Fail : result.UnsupportedFiles > 0 ? ProjectCompatibilityStatus.Partial : ProjectCompatibilityStatus.Pass;
                    results.Add(new("Optional real-project acceptance", status, "Configured local archive is safely parsed and its supported project evidence is discovered.",
                        result.Valid ? $"{result.Roles.Length} document role(s); source detected: {result.SourceDetected}; unsupported source files: {result.UnsupportedFiles}." : "The archive was not accepted by the production validator.",
                        DurationMilliseconds: timer.ElapsedMilliseconds));
                    if (result.Valid)
                    {
                        var mutationTimer = Stopwatch.StartNew();
                        var mutated = Inspect(MutateMarkdownArchive(File.ReadAllBytes(acceptancePath)), cancellationToken);
                        var comparison = CompareSemantics(result, mutated);
                        var mutationStatus = comparison.Equivalent ? status : comparison.OnlyCanonicalFilenameEvidence
                            ? ProjectCompatibilityStatus.Partial : ProjectCompatibilityStatus.Fail;
                        results.Add(new("Optional real-project Markdown mutation", mutationStatus,
                            "Renamed and nested Markdown files preserve discovered document role/content semantics.",
                            comparison.Summary, comparison.Details,
                            DurationMilliseconds: mutationTimer.ElapsedMilliseconds));
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                results.Add(new("Optional real-project acceptance", ProjectCompatibilityStatus.Fail, "Configured local archive is safely parsed.",
                    "The configured archive could not be processed.", ex.GetType().Name, timer.ElapsedMilliseconds));
            }
        }
        else
            results.Add(new("Optional real-project acceptance", ProjectCompatibilityStatus.NotRun, "Optional configured local ZIP can be evaluated when available.",
                "No local acceptance archive is configured.", "Not required for the generated compatibility scenarios."));

        var completed = DateTimeOffset.UtcNow;
        var overall = results.Any(s => s.Status == ProjectCompatibilityStatus.Fail) ? ProjectCompatibilityStatus.Fail
            : results.Any(s => s.Status == ProjectCompatibilityStatus.Partial) ? ProjectCompatibilityStatus.Partial : ProjectCompatibilityStatus.Pass;
        return new(Guid.NewGuid(), started, completed, overall, results, realFixtureUsed);
    }

    private sealed record DocumentSemantic(string Fingerprint, string RelativePath, string Status, string? Role, string Confidence,
        string CandidateRoles, string CandidateConfidence, bool ExactCanonicalFilenameSignal, string Signals);

    private sealed record SemanticComparison(bool Equivalent, bool OnlyCanonicalFilenameEvidence, string Summary, string? Details);

    private sealed record Inspection(bool Valid, bool SourceDetected, int UnsupportedFiles, string[] Roles, string[] RoleFingerprints,
        IReadOnlyList<DocumentSemantic> DocumentSemantics, string[] TechnologyIds,
        IReadOnlyList<(string Path, string Content)> Documents, IqrSourceArchiveReader.Workspace? Workspace);

    private static SemanticComparison CompareSemantics(Inspection left, Inspection right)
    {
        var leftByFingerprint = left.DocumentSemantics.GroupBy(d => d.Fingerprint, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderBy(ClassificationKey, StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
        var rightByFingerprint = right.DocumentSemantics.GroupBy(d => d.Fingerprint, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderBy(ClassificationKey, StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
        var changed = new List<(string Fingerprint, DocumentSemantic[] Original, DocumentSemantic[] Mutated)>();
        var confidenceChanges = new List<(string Fingerprint, DocumentSemantic[] Original, DocumentSemantic[] Mutated)>();
        foreach (var fingerprint in leftByFingerprint.Keys.Union(rightByFingerprint.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var original = leftByFingerprint.GetValueOrDefault(fingerprint, []).OrderBy(ClassificationKey, StringComparer.Ordinal).ToArray();
            var mutated = rightByFingerprint.GetValueOrDefault(fingerprint, []).OrderBy(ClassificationKey, StringComparer.Ordinal).ToArray();
            if (!original.Select(OutcomeKey).Order(StringComparer.Ordinal).SequenceEqual(mutated.Select(OutcomeKey).Order(StringComparer.Ordinal), StringComparer.Ordinal))
                changed.Add((fingerprint, original, mutated));
            else if (!original.Select(ClassificationKey).SequenceEqual(mutated.Select(ClassificationKey), StringComparer.Ordinal))
                confidenceChanges.Add((fingerprint, original, mutated));
        }

        var sourceSame = left.SourceDetected == right.SourceDetected && left.UnsupportedFiles == right.UnsupportedFiles
            && left.TechnologyIds.SequenceEqual(right.TechnologyIds, StringComparer.Ordinal);
        if (changed.Count == 0 && confidenceChanges.Count == 0 && sourceSame)
            return new(true, false, "Document role, classification/ambiguity, candidate-role and source technology semantics are unchanged; path and display-name differences are provenance only.", null);

        var canonicalConfidenceOnly = changed.Count == 0 && confidenceChanges.Count > 0 && sourceSame && confidenceChanges.All(change =>
            change.Original.Length > 0 && change.Original.All(d => d.ExactCanonicalFilenameSignal)
            && change.Mutated.Length > 0 && change.Mutated.All(d => !d.ExactCanonicalFilenameSignal));
        var canonicalRoleLossOnly = changed.Count > 0 && sourceSame && changed.All(change =>
            change.Original.Length == change.Mutated.Length && change.Original.Length > 0
            && change.Original.All(d => d.ExactCanonicalFilenameSignal && d.Role is not null)
            && change.Mutated.All(d => !d.ExactCanonicalFilenameSignal && (d.Role is null || d.Role == change.Original[0].Role)));
        var details = changed.Select(change =>
            $"fingerprint {change.Fingerprint[..Math.Min(12, change.Fingerprint.Length)]}: original [{string.Join(" | ", change.Original.Select(Describe))}], mutated [{string.Join(" | ", change.Mutated.Select(Describe))}]")
            .Concat(confidenceChanges.Take(8).Select(change =>
                $"confidence-only fingerprint {change.Fingerprint[..Math.Min(12, change.Fingerprint.Length)]}: original [{string.Join(" | ", change.Original.Select(Describe))}], mutated [{string.Join(" | ", change.Mutated.Select(Describe))}]"))
            .Concat(sourceSame ? [] : [$"source: original detected={left.SourceDetected}, unsupported={left.UnsupportedFiles}, technologies={string.Join(",", left.TechnologyIds)}; mutated detected={right.SourceDetected}, unsupported={right.UnsupportedFiles}, technologies={string.Join(",", right.TechnologyIds)}"])
            .ToArray();
        var summary = canonicalRoleLossOnly
            ? $"{changed.Count} content fingerprint(s) lost an assigned role after renaming because the original exact canonical filename was required to cross the role threshold; mutated content still has no conflicting role assignment. Other classification changes: {confidenceChanges.Count}. Source semantics are unchanged. This remains Partial because discovery is filename-dependent for those documents."
            : canonicalConfidenceOnly
            ? $"Final detected/unassigned state, role, and ambiguity candidate-role set are unchanged for all documents. {confidenceChanges.Count} content fingerprint(s) have a lower/changed confidence label after the exact canonical filename signal was removed; source semantics are unchanged. This is an expected filename-evidence strength difference, not a discovery loss."
            : changed.Count == 0
                ? $"Final role/ambiguity outcomes are unchanged, but confidence evidence changed for {confidenceChanges.Count} content fingerprint(s); source semantics {(sourceSame ? "were unchanged" : "also changed")}. See per-fingerprint evidence details."
                : $"Semantic discovery changed for {changed.Count} content fingerprint(s); source semantics {(sourceSame ? "were unchanged" : "also changed")}. See the per-fingerprint comparison details.";
        return new(false, canonicalRoleLossOnly || canonicalConfidenceOnly || (changed.Count == 0 && confidenceChanges.Count > 0), summary, string.Join(Environment.NewLine, details));
    }

    private static string OutcomeKey(DocumentSemantic d) => $"{d.Status}|{d.Role}|{string.Join(",", d.CandidateRoles.Split(',', StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal))}";
    private static string ClassificationKey(DocumentSemantic d) => $"{OutcomeKey(d)}|{d.Confidence}|{d.CandidateConfidence}";
    private static string Describe(DocumentSemantic d) =>
        $"{SafeLabel(d.RelativePath)} => {d.Status}/{d.Role ?? "unassigned"}/{d.Confidence}, candidate roles=[{d.CandidateRoles}], candidate confidence=[{d.CandidateConfidence}], exact canonical filename signal={d.ExactCanonicalFilenameSignal}";
    private static string SafeLabel(string path) => path.Replace('\\', '/').TrimStart('/');

    private static Inspection Inspect(byte[] bytes, CancellationToken ct)
    {
        var read = IqrSourceArchiveReader.ReadDetailed("diagnostic.zip", bytes, captureDocuments: true, ct);
        if (!read.IsValid) return new(false, false, 0, [], [], [], [], [], null);
        var workspace = read.Workspace!;
        var docs = (workspace.DocumentFiles ?? []).Select(d => (d.Path, d.Content)).ToArray();
        var classified = ArtifactDocumentDiscovery.Classify(docs.Select(d => new ArtifactDocumentDiscovery.Candidate(d.Path, System.IO.Path.GetFileName(d.Path), d.Content)));
        var roles = classified.Where(d => d.Status == BirkNext.Web.Services.SampleProjects.ArtifactDiscoveryStatus.Detected && d.Role.HasValue).ToArray();
        var source = ProjectImportService.DetectSource(workspace);
        var semantics = classified.Select(d => new DocumentSemantic(d.Fingerprint ?? "", d.RelativePath, d.Status.ToString(), d.Role?.ToString(), d.Confidence.ToString(),
            string.Join(",", d.Candidates.Select(c => c.Role.ToString()).Order(StringComparer.Ordinal)),
            string.Join(",", d.Candidates.Select(c => $"{c.Role}:{c.Confidence}").Order(StringComparer.Ordinal)),
            d.Reasons.Any(reason => reason.StartsWith("Exact canonical filename ", StringComparison.Ordinal)),
            string.Join("; ", d.Reasons.Select(reason => reason.Replace(Environment.NewLine, " "))))).ToArray();
        var technologies = BirkNext.Api.Services.SourceAnalysis.Technology.TechnologyInventory.Detect(workspace).Technologies
            .Select(t => t.TechnologyId).Order(StringComparer.Ordinal).ToArray();
        return new(true, source.Detected, source.UnsupportedSourceFiles, roles.Select(d => d.Role!.Value.ToString()).Distinct().ToArray(),
            roles.Select(d => $"{d.Role}:{d.Fingerprint}").ToArray(), semantics, technologies, docs, workspace);
    }

    private static byte[] Archive(IEnumerable<(string Path, string Content)> files)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (path, content) in files.OrderBy(f => f.Path, StringComparer.Ordinal))
            {
                using var writer = new StreamWriter(zip.CreateEntry(path).Open());
                writer.Write(content);
            }
        return output.ToArray();
    }

    private static byte[] MutateMarkdownArchive(byte[] archiveBytes)
    {
        using var input = new ZipArchive(new MemoryStream(archiveBytes), ZipArchiveMode.Read);
        using var output = new MemoryStream();
        using (var mutated = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            var index = 0;
            foreach (var entry in input.Entries.OrderBy(e => e.FullName, StringComparer.Ordinal))
            {
                var path = entry.FullName;
                if (path.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                {
                    using var markdownSource = entry.Open();
                    using var content = new MemoryStream();
                    markdownSource.CopyTo(content);
                    var text = System.Text.Encoding.UTF8.GetString(content.ToArray());
                    var classification = ArtifactDocumentDiscovery.Classify(
                        [new ArtifactDocumentDiscovery.Candidate(path, System.IO.Path.GetFileName(path), text)]).Single();
                    // Use the exact production document discovery result. Preserve ancillary Markdown which production
                    // intentionally excludes by path/name; mutate every actual candidate, including unresolved ones.
                    if (classification.Status != ArtifactDiscoveryStatus.Unclassified)
                        path = $"relocated/area-{index++}/renamed-document-{index}.md";
                    using var markdownTarget = mutated.CreateEntry(path).Open();
                    content.Position = 0;
                    content.CopyTo(markdownTarget);
                    continue;
                }
                using var target = mutated.CreateEntry(path).Open();
                using var source = entry.Open();
                source.CopyTo(target);
            }
        }
        return output.ToArray();
    }
}
