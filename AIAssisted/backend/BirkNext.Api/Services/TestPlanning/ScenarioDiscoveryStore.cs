using System.Text.Json;
using BirkNext.Api.Data;
using BirkNext.Api.Models;
using BirkNext.TestPlanning;
using Microsoft.EntityFrameworkCore;
using E = BirkNext.Api.Services.TestPlanning.TestPlanSuggestionEngine;

namespace BirkNext.Api.Services.TestPlanning;

/// <summary>
/// Durable copy of discovery results (table <c>scenario_discovery_results</c>) behind the in-memory <see cref="ScenarioDiscoveryCache"/>: a
/// project's last discovery survives an API restart, so a tester never has to discover again only to reopen or extend saved work. The stored
/// result is the engine's own output, unchanged; restoring puts it back into the cache. Only the newest results per project are kept — and the
/// newest <b>current</b> result (basis = analysis baseline) is always kept, so historical inspections can never push it out (2026-11-23).
/// </summary>
public sealed class ScenarioDiscoveryStore(AppDbContext db, ScenarioDiscoveryCache cache, ILogger<ScenarioDiscoveryStore>? logger = null)
{
    public const int KeptPerProject = 5;
    private const string Historical = nameof(DiscoveryBasisKind.Historical);

    public async Task SaveAsync(ScenarioDiscoveryResult result, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(result, E.Json);
        var kind = result.Basis?.Kind.ToString();
        var application = result.Basis is null ? null : result.ApplicationKey;
        var row = await db.ScenarioDiscoveryResults.SingleOrDefaultAsync(r => r.DiscoveryId == result.DiscoveryId, ct);
        if (row is null)
            db.ScenarioDiscoveryResults.Add(new ScenarioDiscoveryRecord
            {
                DiscoveryId = result.DiscoveryId, ProjectKey = result.ProjectKey, SnapshotKey = result.SnapshotKey ?? "", RuleVersion = result.RuleVersion, ContentJson = json,
                DiscoveredAt = result.DiscoveredAt, ApplicationKey = application, BasisKind = kind, BasisFingerprint = result.Basis?.Fingerprint,
            });
        else { row.ContentJson = json; row.DiscoveredAt = result.DiscoveredAt; row.ApplicationKey = application; row.BasisKind = kind; row.BasisFingerprint = result.Basis?.Fingerprint; }
        await db.SaveChangesAsync(ct);
        var rows = await db.ScenarioDiscoveryResults.Where(r => r.ProjectKey == result.ProjectKey).OrderByDescending(r => r.DiscoveredAt).ToListAsync(ct);
        var newestCurrent = rows.FirstOrDefault(r => r.BasisKind != Historical)?.DiscoveryId;
        var old = rows.Skip(KeptPerProject).Where(r => r.DiscoveryId != newestCurrent).ToList();
        if (old.Count > 0) { db.ScenarioDiscoveryResults.RemoveRange(old); await db.SaveChangesAsync(ct); }
    }

    /// <summary>Puts the project's newest stored <b>current</b> result (and the newest for <paramref name="snapshotKey"/>, any kind, when given) back
    /// into the cache when the cache does not hold one (after a restart). Never another project's result; never a historical result as current.</summary>
    public async Task RestoreAsync(string projectKey, string? snapshotKey, CancellationToken ct)
    {
        if (cache.Latest(projectKey) is not null && (snapshotKey is null || cache.Latest(projectKey, snapshotKey) is not null)) return;
        var rows = db.ScenarioDiscoveryResults.AsNoTracking().Where(r => r.ProjectKey == projectKey);
        var forSnapshot = snapshotKey is null ? null : await rows.Where(r => r.SnapshotKey == snapshotKey).OrderByDescending(r => r.DiscoveredAt).FirstOrDefaultAsync(ct);
        var newest = await rows.Where(r => r.BasisKind == null || r.BasisKind != Historical).OrderByDescending(r => r.DiscoveredAt).FirstOrDefaultAsync(ct);
        // The snapshot's result first, then the project's newest current one, so the project pointer ends at the current result.
        foreach (var row in new[] { forSnapshot, newest }.Where(r => r is not null).DistinctBy(r => r!.DiscoveryId))
            if (Read(row!) is { } result && result.ProjectKey == projectKey) cache.Store(result);
    }

    /// <summary>Puts the newest current result of an application (over all its imports) back into the cache when it holds none.</summary>
    public async Task RestoreApplicationAsync(string applicationKey, CancellationToken ct)
    {
        if (cache.CurrentForApplication(applicationKey) is not null) return;
        var row = await db.ScenarioDiscoveryResults.AsNoTracking().Where(r => r.ApplicationKey == applicationKey && r.BasisKind != Historical)
            .OrderByDescending(r => r.DiscoveredAt).FirstOrDefaultAsync(ct);
        if (row is not null && Read(row) is { } result) cache.Store(result);
    }

    /// <summary>A stored result by id, only for its own project.</summary>
    public async Task<ScenarioDiscoveryResult?> FindAsync(string projectKey, string discoveryId, CancellationToken ct)
    {
        if (cache.Find(projectKey, discoveryId) is { } cached) return cached;
        var row = await db.ScenarioDiscoveryResults.AsNoTracking().SingleOrDefaultAsync(r => r.DiscoveryId == discoveryId && r.ProjectKey == projectKey, ct);
        if (row is null || Read(row) is not { } result || result.ProjectKey != projectKey) return null;
        cache.Remember(result);
        return result;
    }

    private ScenarioDiscoveryResult? Read(ScenarioDiscoveryRecord row)
    {
        try { return JsonSerializer.Deserialize<ScenarioDiscoveryResult>(row.ContentJson, E.Json); }
        catch (JsonException e) { logger?.LogWarning(e, "Stored discovery result {DiscoveryId} could not be read; it is ignored", row.DiscoveryId); return null; }
    }
}
