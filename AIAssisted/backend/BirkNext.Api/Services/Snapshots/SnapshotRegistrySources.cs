using System.Text.Json;
using BirkNext.Api.Data;
using BirkNext.Api.Data.CanonicalInterpretation;
using BirkNext.Api.Data.DeliveryReporting;
using BirkNext.Snapshots;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Services.Snapshots;

using Item = (SnapshotMetadata Metadata, IReadOnlyList<SnapshotLineageEdge> Edges);

/// <summary>Source Analysis snapshots: immutable, project-independent; metadata read once from the stored evidence (archive, commit, analyzer).</summary>
public sealed class SourceAnalysisSnapshotSource(AppDbContext db) : ISnapshotSource
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    public string Name => "Source Analysis";

    public async Task<IReadOnlyList<Item>> ReadAsync(IReadOnlySet<string> registered, IReadOnlySet<string> running, CancellationToken ct)
    {
        const string owner = Integrations.SourceEvidence.IqrSourceStore.SourceAnalysisOwner;
        var ids = await db.IqrSourceSnapshots.AsNoTracking().Where(r => r.IntegrationId == owner).Select(r => r.Id).ToListAsync(ct);
        var items = new List<Item>();
        foreach (var id in ids.Where(id => !registered.Contains($"{SnapshotType.SourceAnalysis}|{id}") || running.Contains($"{SnapshotType.SourceAnalysis}|{id}")))
        {
            var row = await db.IqrSourceSnapshots.AsNoTracking().Where(r => r.Id == id).Select(r => new { r.AnalyzedAt, r.EvidenceJson }).FirstAsync(ct);
            // Read once: the selector summary is computed here and stored with the entry, so listing never loads the payload again.
            var summary = System.Text.Json.JsonSerializer.Serialize(BirkNext.Integrations.IqrSourceSnapshotSummary.From(
                System.Text.Json.JsonSerializer.Deserialize<BirkNext.Integrations.IqrSourceSnapshot>(row.EvidenceJson, Web)!), Web);
            using var doc = JsonDocument.Parse(row.EvidenceJson);
            var root = doc.RootElement;
            var commit = JsonMetadata.String(root, "commit");
            var import = JsonMetadata.String(root, "projectImport", "importId");
            var status = JsonMetadata.String(root, "status");
            var me = new SnapshotReference(SnapshotType.SourceAnalysis, id.ToString(), JsonMetadata.String(root, "archive", "fileName"));
            var metadata = new SnapshotMetadata(SnapshotType.SourceAnalysis, id.ToString(), null, "source-analysis",
                JsonMetadata.String(root, "archive", "fileName") ?? id.ToString(), row.AnalyzedAt, null,
                string.IsNullOrWhiteSpace(commit) || commit == "Unknown" ? null : commit, JsonMetadata.String(root, "analyzerVersion") is { } v ? $"analyzer {v}" : null,
                $"public.iqr_source_snapshots:{id}", JsonMetadata.String(root, "archive", "sha256"),
                status is "Partial" ? SnapshotProcessingStatus.Partial : status is "Failed" ? SnapshotProcessingStatus.Failed : SnapshotProcessingStatus.Completed,
                SnapshotRetentionClass.Working, Frozen: true, SnapshotRegistryService.MetadataVersion) { SummaryJson = summary };
            IReadOnlyList<SnapshotLineageEdge> edges = string.IsNullOrWhiteSpace(import) ? []
                : [new(me, SnapshotLineageKind.ImportedFrom, new(SnapshotType.SourceImport, import, JsonMetadata.String(root, "projectImport", "archiveFileName")), Name)];
            items.Add((metadata, edges));
        }
        return items;
    }
}

/// <summary>Delivery snapshots, comparisons and frozen weekly reports, with the exact baseline/current ids each report recorded.</summary>
public sealed class DeliverySnapshotSource(DeliveryReportingDbContext db) : ISnapshotSource
{
    public string Name => "Delivery Management";

    public async Task<IReadOnlyList<Item>> ReadAsync(IReadOnlySet<string> registered, IReadOnlySet<string> running, CancellationToken ct)
    {
        var items = new List<Item>();
        var snapshots = await (from s in db.Snapshots.AsNoTracking()
                               join src in db.ReportSources.AsNoTracking() on s.ReportSourceId equals src.Id
                               select new { s.Id, s.ProjectId, s.Label, s.ImportedAt, s.EffectiveAt, s.ParserVersion, s.ParseStatus, src.Fingerprint, src.GitCommit }).ToListAsync(ct);
        foreach (var s in snapshots.Where(s => !registered.Contains($"{SnapshotType.Delivery}|{s.Id}")))
            items.Add((new SnapshotMetadata(SnapshotType.Delivery, s.Id.ToString(), s.ProjectId.ToString(), "delivery-snapshot", s.Label, s.ImportedAt, s.EffectiveAt,
                s.GitCommit, s.ParserVersion is { } p ? $"parser {p}" : null, $"delivery_reporting.report_snapshot:{s.Id}", s.Fingerprint,
                s.ParseStatus.Contains("fail", StringComparison.OrdinalIgnoreCase) ? SnapshotProcessingStatus.Failed
                    : s.ParseStatus.Contains("partial", StringComparison.OrdinalIgnoreCase) ? SnapshotProcessingStatus.Partial : SnapshotProcessingStatus.Completed,
                SnapshotRetentionClass.Working, Frozen: false, SnapshotRegistryService.MetadataVersion), []));

        var comparisons = await db.Comparisons.AsNoTracking().Select(c => new { c.Id, c.ProjectId, c.BaselineSnapshotId, c.CurrentSnapshotId, c.CreatedAt }).ToListAsync(ct);
        foreach (var c in comparisons.Where(c => !registered.Contains($"{SnapshotType.Delivery}|{c.Id}")))
        {
            var me = new SnapshotReference(SnapshotType.Delivery, c.Id.ToString(), "Comparison");
            var edges = new List<SnapshotLineageEdge>();
            if (c.BaselineSnapshotId is { } b) edges.Add(new(me, SnapshotLineageKind.ComparedAgainst, new(SnapshotType.Delivery, b.ToString()), Name));
            if (c.CurrentSnapshotId is { } cur) edges.Add(new(me, SnapshotLineageKind.DerivedFrom, new(SnapshotType.Delivery, cur.ToString()), Name));
            items.Add((new SnapshotMetadata(SnapshotType.Delivery, c.Id.ToString(), c.ProjectId.ToString(), "delivery-comparison", "Comparison", c.CreatedAt, null, null, null,
                $"delivery_reporting.snapshot_comparison:{c.Id}", null, SnapshotProcessingStatus.Completed, SnapshotRetentionClass.FrozenReportEvidence, Frozen: true,
                SnapshotRegistryService.MetadataVersion), edges));
        }

        var reports = await db.WeeklyReports.AsNoTracking()
            .Select(r => new { r.Id, r.ProjectId, r.ComparisonId, r.BaselineSnapshotId, r.CurrentSnapshotId, r.Title, r.GeneratedAt, r.PeriodEnd, r.ContentVersion }).ToListAsync(ct);
        foreach (var r in reports.Where(r => !registered.Contains($"{SnapshotType.GeneratedReport}|{r.Id}")))
        {
            // The rule sets the report was calculated with are inside its frozen content; read once, never recalculated.
            var content = await db.WeeklyReports.AsNoTracking().Where(x => x.Id == r.Id).Select(x => x.ContentJson).FirstAsync(ct);
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(content) ? "{}" : content);
            var versions = string.Join(" · ", new[]
            {
                JsonMetadata.String(doc.RootElement, "calculationVersion") is { } calc ? $"calculation {calc}" : null,
                JsonMetadata.String(doc.RootElement, "interpretationRuleSet"),
            }.OfType<string>());
            var me = new SnapshotReference(SnapshotType.GeneratedReport, r.Id.ToString(), r.Title);
            var edges = new List<SnapshotLineageEdge> { new(me, SnapshotLineageKind.GeneratedReportFrom, new(SnapshotType.Delivery, r.ComparisonId.ToString(), "Comparison"), Name) };
            if (r.BaselineSnapshotId is { } b) edges.Add(new(me, SnapshotLineageKind.ComparedAgainst, new(SnapshotType.Delivery, b.ToString()), Name));
            if (r.CurrentSnapshotId is { } cur) edges.Add(new(me, SnapshotLineageKind.GeneratedReportFrom, new(SnapshotType.Delivery, cur.ToString()), Name));
            items.Add((new SnapshotMetadata(SnapshotType.GeneratedReport, r.Id.ToString(), r.ProjectId.ToString(), "delivery-report", r.Title, r.GeneratedAt, r.PeriodEnd, null,
                versions.Length > 0 ? versions : $"content {r.ContentVersion}", $"delivery_reporting.weekly_report:{r.Id}", null, SnapshotProcessingStatus.Completed,
                SnapshotRetentionClass.FrozenReportEvidence, Frozen: true, SnapshotRegistryService.MetadataVersion), edges));
        }
        return items;
    }
}

/// <summary>Frozen canonical interpretations: the source snapshots each was interpreted from, and the one it supersedes.</summary>
public sealed class InterpretationSnapshotSource(CanonicalInterpretationDbContext db) : ISnapshotSource
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public string Name => "Canonical interpretation";

    public async Task<IReadOnlyList<Item>> ReadAsync(IReadOnlySet<string> registered, IReadOnlySet<string> running, CancellationToken ct)
    {
        var rows = await db.Snapshots.AsNoTracking().ToListAsync(ct);
        var items = new List<Item>();
        foreach (var s in rows.Where(s => !registered.Contains($"{SnapshotType.Interpretation}|{s.Id}")))
        {
            var me = new SnapshotReference(SnapshotType.Interpretation, s.Id.ToString(), $"Interpretation {s.CreatedAt:yyyy-MM-dd HH:mm}");
            var edges = (JsonSerializer.Deserialize<List<SnapshotReference>>(s.SourceSnapshotsJson, Json) ?? [])
                .Select(src => new SnapshotLineageEdge(me, SnapshotLineageKind.InterpretedUsing, src, Name)).ToList();
            if (s.PreviousId is { } previous) edges.Add(new(me, SnapshotLineageKind.Supersedes, new(SnapshotType.Interpretation, previous.ToString()), Name));
            items.Add((new SnapshotMetadata(SnapshotType.Interpretation, s.Id.ToString(), s.Scope, "canonical-interpretation", me.Label!, s.CreatedAt, null, null,
                s.RuleSetVersions, $"canonical_interpretation.interpretation_snapshot:{s.Id}", s.ContentFingerprint, SnapshotProcessingStatus.Completed,
                SnapshotRetentionClass.Historical, Frozen: true, SnapshotRegistryService.MetadataVersion), edges));
        }
        return items;
    }
}

/// <summary>Review runs that record source snapshot ids in columns (Impact Analysis, Code Risk, Test Coverage) or in their result (IQR).</summary>
public sealed class ReviewRunSnapshotSource(AppDbContext db) : ISnapshotSource
{
    public string Name => "Review runs";

    public async Task<IReadOnlyList<Item>> ReadAsync(IReadOnlySet<string> registered, IReadOnlySet<string> running, CancellationToken ct)
    {
        var items = new List<Item>();
        bool New(Guid id) => !registered.Contains($"{SnapshotType.Review}|{id}");
        IReadOnlyList<SnapshotLineageEdge> Edges(SnapshotReference me, Guid? baseline, Guid current) =>
        [
            .. baseline is { } b && b != Guid.Empty ? new[] { new SnapshotLineageEdge(me, SnapshotLineageKind.ComparedAgainst, new(SnapshotType.SourceAnalysis, b.ToString()), Name) } : [],
            .. current != Guid.Empty ? new[] { new SnapshotLineageEdge(me, SnapshotLineageKind.DerivedFrom, new(SnapshotType.SourceAnalysis, current.ToString()), Name) } : [],
        ];
        SnapshotMetadata Meta(Guid id, string? project, string module, string label, DateTimeOffset at, string storage, SnapshotProcessingStatus status = SnapshotProcessingStatus.Completed) =>
            new(SnapshotType.Review, id.ToString(), project, module, label, at, null, null, null, storage, null, status, SnapshotRetentionClass.Historical, Frozen: true, SnapshotRegistryService.MetadataVersion);

        foreach (var r in (await db.ImpactAnalysisRuns.AsNoTracking().Select(r => new { r.Id, r.ProjectId, r.ProjectDisplayName, r.BaselineSnapshotId, r.CurrentSnapshotId, r.CreatedAt }).ToListAsync(ct)).Where(r => New(r.Id)))
        {
            var meta = Meta(r.Id, string.IsNullOrWhiteSpace(r.ProjectId) ? null : r.ProjectId, "impact-analysis", $"Impact Analysis · {r.ProjectDisplayName}", r.CreatedAt, $"public.impact_analysis_runs:{r.Id}");
            items.Add((meta, Edges(meta.Reference, r.BaselineSnapshotId, r.CurrentSnapshotId)));
        }
        foreach (var r in (await db.AiCodeReviewRuns.AsNoTracking().Select(r => new { r.Id, r.Label, r.BaselineSnapshotId, r.CurrentSnapshotId, r.CompletedAt }).ToListAsync(ct)).Where(r => New(r.Id)))
        {
            var meta = Meta(r.Id, null, "code-risk-review", $"Code Risk Review · {r.Label}", r.CompletedAt, $"public.ai_code_review_runs:{r.Id}");
            items.Add((meta, Edges(meta.Reference, r.BaselineSnapshotId, r.CurrentSnapshotId)));
        }
        foreach (var r in (await db.TestCoverageReviewRuns.AsNoTracking().Select(r => new { r.Id, r.Label, r.BaselineSnapshotId, r.CurrentSnapshotId, r.CompletedAt }).ToListAsync(ct)).Where(r => New(r.Id)))
        {
            var meta = Meta(r.Id, null, "test-coverage-review", $"Test Coverage Review · {r.Label}", r.CompletedAt, $"public.test_coverage_review_runs:{r.Id}");
            items.Add((meta, Edges(meta.Reference, r.BaselineSnapshotId, r.CurrentSnapshotId)));
        }
        foreach (var r in (await db.IntegrationReviewRuns.AsNoTracking().Select(r => new { r.Id, r.EnvironmentId, r.CompletedAt, r.Outcome }).ToListAsync(ct)).Where(r => New(r.Id)))
        {
            // The run's own source scopes name the snapshots it reviewed (read once from the stored result).
            var json = await db.IntegrationReviewRuns.AsNoTracking().Where(x => x.Id == r.Id).Select(x => x.ResultJson).FirstAsync(ct);
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            var meta = Meta(r.Id, r.EnvironmentId, "integration-quality-review", $"Integration Quality Review · {r.Outcome}", r.CompletedAt, $"public.integration_review_runs:{r.Id}",
                r.Outcome.Contains("fail", StringComparison.OrdinalIgnoreCase) || r.Outcome.Contains("error", StringComparison.OrdinalIgnoreCase) ? SnapshotProcessingStatus.Failed : SnapshotProcessingStatus.Completed);
            var edges = JsonMetadata.Array(doc.RootElement, "sourceScopes").Select(s => JsonMetadata.String(s, "snapshotId")).OfType<string>().Distinct()
                .Select(id => new SnapshotLineageEdge(meta.Reference, SnapshotLineageKind.DerivedFrom, new(SnapshotType.SourceAnalysis, id), Name)).ToList();
            items.Add((meta, edges));
        }
        return items;
    }
}

/// <summary>Executions: Performance runs (with the source snapshot each ran against) and Critical E2E runs (with the commit they ran on).</summary>
public sealed class ExecutionSnapshotSource(AppDbContext db, CriticalE2E.ICriticalE2EStore? criticalE2E = null) : ISnapshotSource
{
    public string Name => "Executions";

    public async Task<IReadOnlyList<Item>> ReadAsync(IReadOnlySet<string> registered, IReadOnlySet<string> running, CancellationToken ct)
    {
        var items = new List<Item>();
        var runs = await db.PerformanceTestRuns.AsNoTracking().Select(r => new { r.Id, r.EnvironmentId, r.DefinitionId, r.State, r.CreatedAt }).ToListAsync(ct);
        foreach (var r in runs.Where(r => !registered.Contains($"{SnapshotType.Execution}|{r.Id}") || running.Contains($"{SnapshotType.Execution}|{r.Id}")))
        {
            var json = await db.PerformanceTestRuns.AsNoTracking().Where(x => x.Id == r.Id).Select(x => x.DocumentJson).FirstAsync(ct);
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            var meta = new SnapshotMetadata(SnapshotType.Execution, r.Id.ToString(), r.EnvironmentId, "performance-test-review", $"Performance run · {r.DefinitionId}", r.CreatedAt, null, null,
                JsonMetadata.String(doc.RootElement, "definitionVersion") is { } v ? $"definition v{v}" : null, $"public.performance_test_runs:{r.Id}",
                JsonMetadata.String(doc.RootElement, "comparisonFingerprint"), Status(r.State), SnapshotRetentionClass.Historical, Frozen: !IsRunning(r.State), SnapshotRegistryService.MetadataVersion);
            var edges = new List<SnapshotLineageEdge>();
            if (JsonMetadata.String(doc.RootElement, "sourceSnapshotId") is { Length: > 0 } source)
                edges.Add(new(meta.Reference, SnapshotLineageKind.DerivedFrom, new(SnapshotType.SourceAnalysis, source), Name));
            items.Add((meta, edges));
        }
        foreach (var r in criticalE2E?.History("", 200) ?? [])
        {
            var key = $"{SnapshotType.Execution}|{r.RunId}";
            if (registered.Contains(key) && !running.Contains(key)) continue;
            items.Add((new SnapshotMetadata(SnapshotType.Execution, r.RunId, r.EnvironmentId, "critical-e2e", $"Critical E2E · {r.FlowName}", r.StartedAt, null, null, null,
                $"App_Data/critical-e2e/history.json:{r.RunId}", null, Status(r.Status.ToString()), SnapshotRetentionClass.Historical, Frozen: r.CompletedAt is not null,
                SnapshotRegistryService.MetadataVersion), []));
        }
        return items;
    }

    static bool IsRunning(string state) => state is "Running" or "Queued" or "Cancelling" or "Pending";

    static SnapshotProcessingStatus Status(string state) => state switch
    {
        "Running" or "Queued" or "Cancelling" or "Pending" => SnapshotProcessingStatus.Running,
        "Failed" or "Blocked" or "Error" => SnapshotProcessingStatus.Failed,
        "Cancelled" or "Inconclusive" or "Partial" => SnapshotProcessingStatus.Partial,
        "NotRun" => SnapshotProcessingStatus.Unknown,
        _ => SnapshotProcessingStatus.Completed,
    };
}
