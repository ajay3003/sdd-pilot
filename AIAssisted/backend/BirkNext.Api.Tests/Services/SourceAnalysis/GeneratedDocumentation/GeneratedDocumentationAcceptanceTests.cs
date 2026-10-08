using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using BirkNext.Api.Data;
using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.Api.Services.SourceAnalysis.GeneratedDocumentation;
using BirkNext.GeneratedDocumentation;
using BirkNext.ProjectImport;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Xunit.Abstractions;

namespace BirkNext.Api.Tests.Services.SourceAnalysis.GeneratedDocumentation;

/// <summary>
/// Opt-in acceptance run over a real local project archive (no project-specific assertions or counts): set
/// <c>GENERATED_DOCS_ACCEPTANCE_ARCHIVE</c> to a ZIP path. Without it the test is skipped with an explicit reason, so CI never needs a real
/// archive. The archive is only read. The run mirrors Project Import: one archive → snapshot with import provenance → diagnostic with the
/// authored Spec-Kit artifacts of the same archive (per module: its constitution and the newest feature's spec, plan and tasks).
/// </summary>
public sealed class GeneratedDocumentationAcceptanceTests(ITestOutputHelper output)
{
    public const string Variable = "GENERATED_DOCS_ACCEPTANCE_ARCHIVE";

    [AcceptanceArchiveFact]
    public async Task Real_archive_generated_documentation_report()
    {
        var path = Environment.GetEnvironmentVariable(Variable)!;
        var bytes = await File.ReadAllBytesAsync(path);
        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var store = new IqrSourceStore(db);
        var total = Stopwatch.StartNew();
        var read = IqrSourceArchiveReader.ReadDetailed(Path.GetFileName(path), bytes, captureDocuments: true);
        read.IsValid.Should().BeTrue();
        var importId = "import-acceptance";
        var snapshot = await store.AnalyzeValidatedAsync(null, IqrSourceStore.SourceAnalysisOwner, Path.GetFileName(path), bytes, read.Workspace!, default,
            new ProjectImportProvenance { ImportId = importId, ArchiveFileName = Path.GetFileName(path), ArchiveSha256 = read.Workspace!.Archive.Sha256, ImportedAt = DateTimeOffset.UtcNow });
        var analysis = total.ElapsedMilliseconds;
        var authored = AuthoredInputs(read.Workspace.DocumentFiles ?? []);
        var service = new GeneratedDocumentationDiagnosticService(store, new MemoryCache(new MemoryCacheOptions()));
        var diagnostic = Stopwatch.StartNew();
        var run = await service.RunAsync(new GeneratedDocumentationDiagnosticRequest { ProjectImportId = importId, AuthoredArtifacts = authored });
        diagnostic.Stop();
        var cachedRun = await service.RunAsync(new GeneratedDocumentationDiagnosticRequest { ProjectImportId = importId, AuthoredArtifacts = authored });
        var g = snapshot.GeneratedDocumentation!;
        var report = new StringBuilder();
        report.AppendLine($"Source Analysis (read+analyze) {analysis} ms; generated-documentation analysis {g.AnalysisMilliseconds} ms; diagnostic run {diagnostic.ElapsedMilliseconds} ms (cached rerun {cachedRun.DurationMilliseconds} ms, fromCache={cachedRun.FromCache})");
        report.AppendLine($"Archive timestamps reliable: {g.ArchiveTimestampsReliable} ({g.ArchiveTimestampBasis})");
        report.AppendLine($"Generators/workflow files: {g.Generators.Count}");
        foreach (var w in g.Generators) report.AppendLine($"  {w.Type} | {w.Role} | {w.Path} | name={w.GeneratorName} | declares=[{string.Join(", ", w.DeclaredOutputDirectories)}] files=[{string.Join(", ", w.DeclaredOutputFiles)}] | refs=[{string.Join(", ", w.References)}]");
        report.AppendLine($"Generated documents: {g.Documents.Count(d => d.Origin == DocumentationOrigin.Generated)} (+{g.Documents.Count(d => d.Origin != DocumentationOrigin.Generated)} of unknown origin in generated folders); authored files {g.AuthoredDocumentationFiles}; unclassified docs {g.UnclassifiedDocumentationFiles}");
        report.AppendLine($"Summary: {System.Text.Json.JsonSerializer.Serialize(run.Summary)}");
        report.AppendLine($"Authored: compared={run.Authored.Compared} ({run.Authored.Reason}) artifacts={run.Authored.Artifacts.Count}");
        foreach (var m in run.Modules)
        {
            report.AppendLine($"MODULE {m.Module} status={m.Status} generator={m.GeneratorDetected} freshness={m.Freshness} :: {m.FreshnessReason}");
            report.AppendLine($"   source={m.LatestSourceEvidence?.Basis} {m.LatestSourceEvidence?.At:yyyy-MM-dd} generated={m.LatestGeneratedEvidence?.At:yyyy-MM-dd} missing=[{string.Join(", ", m.MissingExpectedDocs)}] additional=[{string.Join(", ", m.AdditionalDocs)}] discrepancies={m.SourceDiscrepancies.Count} cross={m.CrossArtifactDrift.Count}");
            foreach (var s in m.SupportingFreshnessSignals) report.AppendLine($"   signal: {s}");
            foreach (var c in m.Comparisons) report.AppendLine($"   compare {c.Family}: {c.State} ({c.Candidates}) {c.Detail}");
            foreach (var d in m.Evidence.Where(d => d.FreshnessStatus == GeneratedDocumentationFreshnessStatus.Stale)) report.AppendLine($"   STALE {d.SafeRelativePath}: {d.FreshnessReason}");
        }
        report.AppendLine($"Drift candidates: {run.Drift.Count}");
        foreach (var group in run.Drift.GroupBy(d => (d.DriftType, d.Family, d.DifferenceKind)).OrderBy(x => x.Key.DriftType)) report.AppendLine($"  {group.Key.DriftType} / {group.Key.Family} / {group.Key.DifferenceKind}: {group.Count()}");
        foreach (var d in run.Drift.Where(d => d.DriftType != CrossArtifactDriftType.DocumentationDrift).Take(40)) report.AppendLine($"    {d.DriftType} {d.ModuleId} {d.Family} {d.DifferenceKind} {d.StructuredKey} :: {d.Explanation}");
        output.WriteLine(report.ToString());
        run.Snapshot!.SnapshotId.Should().Be(snapshot.Id);
        run.State.Should().Be(GeneratedDocumentationRunState.Ready);
        cachedRun.FromCache.Should().BeTrue();
    }

    /// <summary>Per module folder: its constitution and the newest feature folder's spec, plan and tasks (by feature number) — the same files a
    /// user would select in the explorers; each keeps its archive path so it is scoped to its module.</summary>
    private static List<AuthoredArtifactInput> AuthoredInputs(List<BirkNext.Api.Services.Integrations.ApplicationMessaging.SourceFile> documents)
    {
        var inputs = new List<AuthoredArtifactInput>();
        AuthoredArtifactInput Input(string role, BirkNext.Api.Services.Integrations.ApplicationMessaging.SourceFile f) =>
            new(role, "artifact:" + f.Path, Path.GetFileName(f.Path), f.Path, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(f.Content))).ToLowerInvariant(), f.Content);
        foreach (var constitution in documents.Where(d => d.Path.EndsWith(".specify/memory/constitution.md", StringComparison.OrdinalIgnoreCase)))
            inputs.Add(Input("Constitution", constitution));
        var features = documents.Select(d => Regex.Match(d.Path, @"^(.*specs/(\d{3})-[^/]+)/(spec|plan|tasks)\.md$")).Where(m => m.Success)
            .GroupBy(m => m.Groups[1].Value[..m.Groups[1].Value.LastIndexOf("specs/", StringComparison.Ordinal)]);
        foreach (var module in features)
        {
            var newest = module.OrderByDescending(m => m.Groups[2].Value, StringComparer.Ordinal).First().Groups[1].Value;
            foreach (var role in new[] { ("spec", "Specification"), ("plan", "Plan"), ("tasks", "Tasks") })
                if (documents.FirstOrDefault(d => d.Path == $"{newest}/{role.Item1}.md") is { } file) inputs.Add(Input(role.Item2, file));
        }
        return inputs;
    }
}

/// <summary>Skipped with an explicit reason unless <see cref="GeneratedDocumentationAcceptanceTests.Variable"/> names an existing ZIP.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class AcceptanceArchiveFactAttribute : FactAttribute
{
    public AcceptanceArchiveFactAttribute()
    {
        var path = Environment.GetEnvironmentVariable(GeneratedDocumentationAcceptanceTests.Variable);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) Skip = $"Opt-in acceptance run: set {GeneratedDocumentationAcceptanceTests.Variable} to a local project ZIP.";
    }
}
