using System.Data;
using System.Text.Json;
using BirkNext.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Services.DataQuality;

/// <summary>
/// The raw inputs of one data-quality run, read once inside a single READ ONLY, REPEATABLE READ transaction (a consistent snapshot of the
/// database; nothing is written). Rows are kept as stored JSON so the reference calculation parses them with its own code, never through the
/// production services it audits.
/// </summary>
public sealed record DataQualitySnapshot
{
    public string ProjectKey { get; init; } = "";
    /// <summary>The application the project belongs to (from its newest current discovery, else its own plans); null when none is recorded.</summary>
    public string? Application { get; init; }
    public List<CatalogRow> Catalogs { get; init; } = [];
    /// <summary>Catalogs of OTHER imports of the same application (newest first): a newer one than the project's own means a stale baseline.</summary>
    public List<CatalogRow> ApplicationCatalogs { get; init; } = [];
    public List<DraftRow> Drafts { get; init; } = [];
    public List<DiscoveryRow> Discoveries { get; init; } = [];
    public List<GroupingRow> Groupings { get; init; } = [];
    public List<RunRow> Runs { get; init; } = [];
    /// <summary>Stored document text by SHA-256 (only the bodies the baseline's documents name; absent = not stored).</summary>
    public Dictionary<string, string> Bodies { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public string Marker { get; init; } = "";

    public sealed record CatalogRow(Guid Id, string ProjectKey, int Revision, string? ArchiveSha256, Guid? SnapshotId, DateTimeOffset CapturedAt, string DocumentsJson,
        string EntriesJson, string? EvidenceJson);
    public sealed record DraftRow(Guid Id, string GuideKey, string Lifecycle, DateTimeOffset UpdatedAt, string ContentJson);
    public sealed record DiscoveryRow(string DiscoveryId, string ProjectKey, string? ApplicationKey, string? BasisKind, DateTimeOffset DiscoveredAt, string ContentJson);
    public sealed record GroupingRow(string SpecificationPath, int Revision, string ContentJson);
    public sealed record RunRow(Guid Id, Guid DraftId, string CaseId, string State, string Outcome, DateTimeOffset StartedAt, DateTimeOffset? CompletedAt);
}

/// <summary>Reads a <see cref="DataQualitySnapshot"/> without writing anything, and the cheap change marker used to detect concurrent changes.</summary>
public sealed class DataQualitySnapshotReader(AppDbContext db)
{
    public async Task<DataQualitySnapshot> ReadAsync(string projectKey, bool withBodies, CancellationToken ct)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
            if (db.Database.IsNpgsql()) await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", ct);
            try { return await ReadInsideAsync(projectKey, withBodies, ct); }
            finally { await tx.RollbackAsync(CancellationToken.None); }
        });
    }

    /// <summary>Row counts and newest change times of every input (read without a transaction): equal before and after a run = no concurrent change.</summary>
    public async Task<string> MarkerAsync(string projectKey, CancellationToken ct)
    {
        var owner = "project:" + projectKey.Trim();
        var drafts = await db.TestPlanDrafts.AsNoTracking().Select(d => new { d.UpdatedAt, d.Revision }).ToListAsync(ct);
        var runs = await db.ManualTestRuns.AsNoTracking().Where(r => r.ProjectKey == projectKey).Select(r => r.UpdatedAt).ToListAsync(ct);
        var groupings = await db.StoryGroupings.AsNoTracking().Where(g => g.ProjectKey == projectKey).Select(g => g.UpdatedAt).ToListAsync(ct);
        var discoveries = await db.ScenarioDiscoveryResults.AsNoTracking().Where(d => d.ProjectKey == projectKey).Select(d => d.DiscoveredAt).ToListAsync(ct);
        var catalogs = await db.SpecificationCatalogs.AsNoTracking().Select(c => c.CapturedAt).ToListAsync(ct);
        static string Max(IEnumerable<DateTimeOffset> xs) => xs.DefaultIfEmpty().Max().ToUniversalTime().ToString("O");
        return string.Join(";", $"drafts={drafts.Count}/{drafts.Sum(d => (long)d.Revision)}/{Max(drafts.Select(d => d.UpdatedAt))}",
            $"runs={runs.Count}/{Max(runs)}", $"groupings={groupings.Count}/{Max(groupings)}", $"discoveries={discoveries.Count}/{Max(discoveries)}",
            $"catalogs={catalogs.Count}/{Max(catalogs)}", $"owner={owner.Length}");
    }

    private async Task<DataQualitySnapshot> ReadInsideAsync(string projectKey, bool withBodies, CancellationToken ct)
    {
        var marker = await MarkerAsync(projectKey, ct);
        var discoveries = (await db.ScenarioDiscoveryResults.AsNoTracking().Where(d => d.ProjectKey == projectKey).ToListAsync(ct))
            .Select(d => new DataQualitySnapshot.DiscoveryRow(d.DiscoveryId, d.ProjectKey, d.ApplicationKey, d.BasisKind, d.DiscoveredAt, d.ContentJson)).ToList();
        var allDrafts = (await db.TestPlanDrafts.AsNoTracking().Where(d => d.GuideKey.StartsWith("project:")).ToListAsync(ct))
            .Select(d => new DataQualitySnapshot.DraftRow(d.Id, d.GuideKey, d.Lifecycle, d.UpdatedAt, d.ContentJson)).ToList();
        // The application: the newest current discovery's basis, else the project's own plans (the same order the dashboard uses).
        var application = discoveries.Where(d => d.BasisKind != "Historical").OrderByDescending(d => d.DiscoveredAt).Select(d => d.ApplicationKey).FirstOrDefault(a => a is not null)
            ?? allDrafts.Select(d => Field(d.ContentJson, "projectKey") == projectKey ? Field(d.ContentJson, "applicationKey") : null).FirstOrDefault(a => a is not null);
        var drafts = allDrafts.Where(d => Field(d.ContentJson, "projectKey") is { } pk && (pk == projectKey || application is not null && Field(d.ContentJson, "applicationKey") == application)).ToList();
        var draftIds = drafts.Select(d => d.Id).ToList();
        var runs = (await db.ManualTestRuns.AsNoTracking().Where(r => draftIds.Contains(r.DraftId)).ToListAsync(ct))
            .Select(r => new DataQualitySnapshot.RunRow(r.Id, r.DraftId, r.CaseId, r.State, r.Outcome, r.StartedAt, r.CompletedAt)).ToList();
        var groupings = (await db.StoryGroupings.AsNoTracking().Where(g => g.ProjectKey == projectKey).ToListAsync(ct))
            .Select(g => new DataQualitySnapshot.GroupingRow(g.SpecificationPath, g.Revision, g.ContentJson)).ToList();
        var catalogs = await CatalogsAsync(c => c.ProjectKey == projectKey, ct);
        // Other imports of the same application: their catalogs' project keys are the projects whose discoveries name the application.
        var appCatalogs = new List<DataQualitySnapshot.CatalogRow>();
        if (application is not null)
        {
            var projects = await db.ScenarioDiscoveryResults.AsNoTracking().Where(d => d.ApplicationKey == application && d.ProjectKey != projectKey).Select(d => d.ProjectKey).Distinct().ToListAsync(ct);
            if (projects.Count > 0) appCatalogs = await CatalogsAsync(c => projects.Contains(c.ProjectKey), ct);
        }
        var bodies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (withBodies && catalogs.OrderByDescending(c => c.Revision).FirstOrDefault(c => c.EvidenceJson is not null) is { } baseline)
        {
            var shas = Documents(baseline.DocumentsJson).Select(d => d.Sha256).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var chunk in shas.Chunk(200))
                foreach (var b in await db.WorkspaceDocumentBodies.AsNoTracking().Where(b => chunk.Contains(b.Sha256)).Select(b => new { b.Sha256, b.Content }).ToListAsync(ct))
                    bodies[b.Sha256] = b.Content;
        }
        return new DataQualitySnapshot
        {
            ProjectKey = projectKey, Application = application, Catalogs = catalogs, ApplicationCatalogs = appCatalogs, Drafts = drafts, Discoveries = discoveries,
            Groupings = groupings, Runs = runs, Bodies = bodies, Marker = marker,
        };
    }

    private async Task<List<DataQualitySnapshot.CatalogRow>> CatalogsAsync(System.Linq.Expressions.Expression<Func<Models.SpecificationCatalogRecord, bool>> where, CancellationToken ct)
    {
        var rows = await db.SpecificationCatalogs.AsNoTracking().Where(where).OrderByDescending(c => c.Revision).ToListAsync(ct);
        var result = new List<DataQualitySnapshot.CatalogRow>();
        foreach (var r in rows)
        {
            var evidence = r.EvidenceJson ?? await db.SpecificationCatalogEvidence.AsNoTracking().Where(e => e.CatalogId == r.Id).Select(e => e.EvidenceJson).FirstOrDefaultAsync(ct);
            result.Add(new(r.Id, r.ProjectKey, r.Revision, r.ArchiveSha256, r.SnapshotId, r.CapturedAt, r.DocumentsJson, r.EntriesJson, evidence));
        }
        return result;
    }

    internal static List<(string Path, string Sha256)> Documents(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return [.. doc.RootElement.EnumerateArray().Select(e => (Str(e, "path") ?? "", Str(e, "sha256") ?? ""))];
        }
        catch (JsonException) { return []; }
    }

    internal static string? Field(string json, string name)
    {
        try { using var doc = JsonDocument.Parse(json); return Str(doc.RootElement, name); }
        catch (JsonException) { return null; }
    }

    internal static string? Str(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        foreach (var p in e.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : p.Value.ValueKind == JsonValueKind.Null ? null : p.Value.ToString();
        return null;
    }
}
