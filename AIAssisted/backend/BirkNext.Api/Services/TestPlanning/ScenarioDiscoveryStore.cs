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

    /// <summary>Puts the newest current result of an application (over all its imports) back into the cache, once per process. A pointer the cache
    /// already holds is no proof it is the newest: after a restart, restoring one import's own (older) result sets it first. The cache only ever
    /// moves the pointer forward, so this never replaces a newer result.</summary>
    public async Task RestoreApplicationAsync(string applicationKey, CancellationToken ct)
    {
        if (!cache.FirstApplicationRestore(applicationKey)) return;
        var row = await db.ScenarioDiscoveryResults.AsNoTracking().Where(r => r.ApplicationKey == applicationKey && r.BasisKind != Historical)
            .OrderByDescending(r => r.DiscoveredAt).FirstOrDefaultAsync(ct);
        if (row is not null && Read(row) is { } result) cache.Store(result);
    }

    /// <summary>The application's previous version: the newest stored current result whose source content differs from <paramref name="content"/>
    /// (the set of member content fingerprints). A re-import of the same content is never "the previous version". Null when there is none.</summary>
    public async Task<ScenarioDiscoveryResult?> PreviousVersionAsync(string applicationKey, IReadOnlySet<string> content, CancellationToken ct)
    {
        var rows = await db.ScenarioDiscoveryResults.AsNoTracking().Where(r => r.ApplicationKey == applicationKey && r.BasisKind != Historical)
            .OrderByDescending(r => r.DiscoveredAt).Take(50).ToListAsync(ct);
        foreach (var row in rows)
            if (Read(row) is { Basis: { } basis } result && !Content(basis).SetEquals(content)) return result;
        return null;
    }

    /// <summary>The source content a basis was read from (member content fingerprints, else snapshot ids for older results).</summary>
    public static HashSet<string> Content(DiscoverySourceBasis basis) =>
        [.. basis.Members.Where(m => m.Resolved).Select(m => m.ContentFingerprint ?? m.SnapshotId!.Value.ToString())];

    /// <summary>Puts the project's stored current results of every selection scope back into the cache, once per process, so a saved case can be
    /// compared with a discovery of its own specification while another is selected (2026-11-25). Never a historical result; never the latest pointer.</summary>
    public async Task RestoreScopesAsync(string projectKey, CancellationToken ct)
    {
        if (!cache.FirstScopedRestore(projectKey)) return;
        var rows = await db.ScenarioDiscoveryResults.AsNoTracking().Where(r => r.ProjectKey == projectKey && (r.BasisKind == null || r.BasisKind != Historical))
            .OrderByDescending(r => r.DiscoveredAt).Take(KeptPerProject + 1).ToListAsync(ct);
        foreach (var row in rows)
            if (Read(row) is { } result && result.ProjectKey == projectKey) cache.RememberScoped(result);
    }

    /// <summary>The newest stored current result computed for exactly this selection scope (module + specification); null when none.</summary>
    public async Task<ScenarioDiscoveryResult?> FindScopedAsync(string projectKey, string scope, CancellationToken ct)
    {
        var rows = await db.ScenarioDiscoveryResults.AsNoTracking().Where(r => r.ProjectKey == projectKey && (r.BasisKind == null || r.BasisKind != Historical))
            .OrderByDescending(r => r.DiscoveredAt).Take(KeptPerProject + 1).ToListAsync(ct);
        foreach (var row in rows)
            if (Read(row) is { } result && result.ProjectKey == projectKey && result.SelectionScope == scope)
            {
                cache.RememberScoped(result);
                return result;
            }
        return null;
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
