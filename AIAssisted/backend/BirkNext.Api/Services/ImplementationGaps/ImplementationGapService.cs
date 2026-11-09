using System.Text.Json;
using BirkNext.Api.Data;
using BirkNext.Api.Models;
using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.Api.Services.ManualReview;
using BirkNext.ImplementationGaps;
using BirkNext.Integrations;
using BirkNext.ManualReview;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Services.ImplementationGaps;

/// <summary>
/// Runs <see cref="ImplementationGapEngine"/> over extracted SDD inputs and a stored source snapshot, persists each evaluation (the extracted
/// inputs — never document text — and the report), and applies human review from the shared append-only <see cref="ManualReviewDecisionStore"/>
/// keyed by stable finding id, so a review survives re-evaluation. Re-evaluation adds a new evaluation; nothing is overwritten.
/// </summary>
public sealed class ImplementationGapService(AppDbContext db, IqrSourceStore sources, ManualReviewDecisionStore reviews, TimeProvider? clock = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private DateTimeOffset Now => (clock ?? TimeProvider.System).GetUtcNow();

    public static string ImportScope(string importId) => "import:" + importId;

    /// <summary>The evidence the engine reads from a snapshot: file inventory, declared types, declared packages, test definitions, projects.</summary>
    public static GapSourceEvidence FromSnapshot(IqrSourceSnapshot s)
    {
        var files = new HashSet<string>(s.TargetIndex?.Files.Select(f => f.RelativePath) ?? [], StringComparer.OrdinalIgnoreCase);
        // Dependency evidence is repository-relative ("src/X/X.csproj"; the repository may be named after the archive, not a folder); the inventory,
        // symbols and tests are archive-relative. Map to the archive path: "{repository}/{path}", the path itself, or the one inventory file ending in it.
        string ArchivePath(string repository, string path)
        {
            if (repository.Length > 0 && files.Contains($"{repository}/{path}")) return $"{repository}/{path}";
            if (files.Contains(path)) return path;
            var suffix = files.Where(f => f.EndsWith("/" + path, StringComparison.OrdinalIgnoreCase)).Take(2).ToList();
            return suffix.Count == 1 ? suffix[0] : path;
        }
        return Build(s) with
        {
            Packages = s.DependencyEvidence?.Dependencies.Select(d => new GapPackage(d.PackageName, d.CurrentValue, ArchivePath(d.Repository, d.OwnerFile),
                [.. d.ReferencedBy.Select(r => ArchivePath(d.Repository, r))])).ToList() ?? [],
        };
    }

    private static GapSourceEvidence Build(IqrSourceSnapshot s) => new()
    {
        SnapshotId = s.Id, ArchiveName = s.Archive.FileName, ArchiveSha256 = s.Archive.Sha256, SnapshotFingerprint = s.TargetIndex?.SnapshotFingerprint, AnalyzedAt = s.AnalyzedAt,
        Files = s.TargetIndex?.Files.Select(f => f.RelativePath).ToList() ?? [],
        FileInventoryComplete = s.TargetIndex?.FileInventoryComplete ?? false,
        Symbols = s.CodeRiskEvidence?.Symbols.Select(x => new GapSymbol(x.Name, x.Namespace, x.Kind, x.File, x.Line)).ToList() ?? [],
        SymbolsCaptured = s.CodeRiskEvidence is { AnalyzerVersion: >= 3 }, SymbolsTruncated = s.CodeRiskEvidence?.Truncated ?? false,
        PackagesCaptured = s.DependencyEvidence is not null,
        Tests = s.TestInventory?.Definitions.Select(d => new GapTestDefinition(d.ClassName, d.MethodName, d.FilePath, d.FullyQualifiedName)).ToList() ?? [],
        TestsCaptured = s.TestInventory is not null,
        Projects = [.. s.Projects.Select(p => new GapProject(p.Name, p.Path, p.IsTest))],
        Limitations = [.. s.Limitations.Take(10)],
    };

    public async Task<GapEvaluation> EvaluateDocumentsAsync(string scopeKey, string? projectImportId, IEnumerable<ImplementationGapExtractor.Document> documents,
        IReadOnlyList<string> skippedDocuments, Guid? snapshotId, CancellationToken ct)
    {
        var docs = ImplementationGapExtractor.Extract(documents);
        if (skippedDocuments.Count > 0) docs = docs with { Limitations = [.. docs.Limitations, .. skippedDocuments.Take(20).Select(s => $"Document not read: {s}")] };
        return await EvaluateAsync(scopeKey, projectImportId, docs, snapshotId, [], [], ct);
    }

    public async Task<GapEvaluation> EvaluateAsync(string scopeKey, string? projectImportId, GapDocumentSet docs, Guid? snapshotId,
        IReadOnlyList<GapExecutedTest> executions, IReadOnlyList<GapRuntimeObservation> runtime, CancellationToken ct)
    {
        var snapshot = snapshotId is { } id ? await sources.FindSourceAnalysisAsync(id, ct) : null;
        var evidence = snapshot is null ? null : FromSnapshot(snapshot);
        var evaluation = ImplementationGapEngine.Evaluate(docs, evidence, executions, runtime, Now, scopeKey) with { Id = Guid.NewGuid(), ProjectImportId = projectImportId };
        if (snapshotId is not null && snapshot is null) evaluation = evaluation with { Limitations = [.. evaluation.Limitations, "The source snapshot is no longer stored: implementation not assessed."] };
        db.ImplementationGapEvaluations.Add(new ImplementationGapEvaluationRecord
        {
            Id = evaluation.Id, ScopeKey = scopeKey, ProjectImportId = projectImportId, SnapshotId = snapshotId, EvaluatedAt = evaluation.EvaluatedAt,
            InputsJson = JsonSerializer.Serialize(new StoredInputs(docs, executions.ToList(), runtime.ToList()), Json), ReportJson = JsonSerializer.Serialize(evaluation, Json),
        });
        await db.SaveChangesAsync(ct);
        return await WithReviewsAsync(evaluation, ct);
    }

    public async Task<IReadOnlyList<GapEvaluationSummary>> ListAsync(string? scopeKey, CancellationToken ct)
    {
        var query = db.ImplementationGapEvaluations.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(scopeKey)) query = query.Where(r => r.ScopeKey == scopeKey);
        var rows = await query.OrderByDescending(r => r.EvaluatedAt).Take(50).ToListAsync(ct);
        return [.. rows.Select(r => (r, e: JsonSerializer.Deserialize<GapEvaluation>(r.ReportJson, Json)!))
            .Select(x => new GapEvaluationSummary(x.r.Id, x.r.ScopeKey, x.r.ProjectImportId, x.r.SnapshotId, x.e.ArchiveName, x.r.EvaluatedAt, x.e.TaskCount, x.e.Findings.Count, x.e.Features.Count))];
    }

    public async Task<GapEvaluation?> GetAsync(Guid id, CancellationToken ct)
    {
        var row = await db.ImplementationGapEvaluations.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);
        return row is null ? null : await WithReviewsAsync(JsonSerializer.Deserialize<GapEvaluation>(row.ReportJson, Json)!, ct);
    }

    /// <summary>A new evaluation of the same inputs and snapshot, with the recorded executions/runtime observations the caller supplies.</summary>
    public async Task<GapEvaluation?> ReevaluateAsync(Guid id, GapReevaluateRequest request, CancellationToken ct)
    {
        var row = await db.ImplementationGapEvaluations.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);
        if (row is null) return null;
        var inputs = JsonSerializer.Deserialize<StoredInputs>(row.InputsJson, Json)!;
        return await EvaluateAsync(row.ScopeKey, row.ProjectImportId, inputs.Documents, row.SnapshotId, request.Executions, request.RuntimeObservations, ct);
    }

    public async Task<(ManualReviewDecisionView? Decision, string? Error)> ReviewAsync(Guid id, GapReviewRequest request, string reviewer, CancellationToken ct)
    {
        var evaluation = await GetAsync(id, ct);
        if (evaluation is null) return (null, "Evaluation not found.");
        var finding = evaluation.Findings.FirstOrDefault(f => f.Id == request.FindingId);
        if (finding is null) return (null, "Finding not found in this evaluation.");
        if (!Enum.TryParse<ManualReviewDecisionValue>(request.Decision, out var decision)) return (null, "Unknown decision.");
        return await reviews.RecordAsync(new ManualReviewDecisionRequest
        {
            Module = ManualReviewModules.ImplementationGaps, ScopeKey = evaluation.ScopeKey, SubjectKey = finding.Id, Reviewer = reviewer, Decision = decision, Note = request.Note,
            EvidenceReferences = [.. finding.Sources.Take(ManualReviewRules.MaxEvidence).Select(s => s.Line is { } l ? $"{s.Path}:{l}" : s.Path)],
        }, ct);
    }

    private async Task<GapEvaluation> WithReviewsAsync(GapEvaluation evaluation, CancellationToken ct)
    {
        var current = (await reviews.ListAsync(ManualReviewModules.ImplementationGaps, evaluation.ScopeKey, ct: ct)).Where(r => r.Current).ToDictionary(r => r.SubjectKey, StringComparer.Ordinal);
        return evaluation with
        {
            Findings = [.. evaluation.Findings.Select(f => current.TryGetValue(f.Id, out var r) ? f with { ReviewStatus = r.Decision.ToString(), ReviewedBy = r.Reviewer } : f)],
        };
    }

    private sealed record StoredInputs(GapDocumentSet Documents, List<GapExecutedTest> Executions, List<GapRuntimeObservation> Runtime);
}
