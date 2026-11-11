using BirkNext.Api.Services.ContractTesting;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Services.WorkspaceTransfer;

public sealed record TransferProjectScope(string ProjectKey, string DisplayName, int Snapshots, int Contracts, int Suites, int CompositeContexts, DateTimeOffset LatestAnalysis);
public sealed record TransferEnvironmentScope(string EnvironmentId, int ConnectedSystems, int Integrations, int SourceBindings, int Flows, int Suites);
public sealed record TransferScopes(IReadOnlyList<TransferProjectScope> Projects, IReadOnlyList<TransferEnvironmentScope> Environments);

public sealed partial class WorkspacePackageService
{
    /// <summary>What can be exported, with counts read from the stores (never estimated).</summary>
    public async Task<TransferScopes> ScopesAsync(IEnumerable<string> knownEnvironmentIds, CancellationToken ct)
    {
        var snapshots = await SourceSnapshotsAsync(_db, ct);
        var contracts = await _db.ApiContracts.AsNoTracking().GroupBy(c => c.ProjectKey).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        var composites = await _db.CompositeSourceContexts.AsNoTracking().GroupBy(c => c.OwningProject).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        var suites = await _db.IntegrationSuites.AsNoTracking().Select(s => s.SnapshotId).ToListAsync(ct);
        var projects = snapshots.GroupBy(s => ContractRegistryService.ProjectKeyOf(s.Snapshot), StringComparer.Ordinal).Select(g =>
        {
            var ids = g.Select(s => s.Record.Id).ToHashSet();
            var display = g.Select(s => s.Snapshot.Repository?.DisplayName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)) ?? g.Key;
            return new TransferProjectScope(g.Key, display, g.Count(), contracts.FirstOrDefault(c => c.Key == g.Key)?.Count ?? 0, suites.Count(ids.Contains),
                composites.FirstOrDefault(c => c.Key == g.Key)?.Count ?? 0, g.Max(s => s.Record.AnalyzedAt));
        }).OrderBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();

        var envIds = new HashSet<string>(knownEnvironmentIds.Where(e => !string.IsNullOrWhiteSpace(e)), StringComparer.Ordinal);
        envIds.UnionWith(await _db.ExternalIntegrations.AsNoTracking().Select(e => e.EnvironmentId).Distinct().ToListAsync(ct));
        envIds.UnionWith(await _db.IntegrationDefinitions.AsNoTracking().Select(e => e.EnvironmentId).Distinct().ToListAsync(ct));
        envIds.UnionWith(await _db.IntegrationEvidenceBindings.AsNoTracking().Select(e => e.EnvironmentId).Distinct().ToListAsync(ct));
        var environments = new List<TransferEnvironmentScope>();
        foreach (var env in envIds.Where(e => e.Length > 0).Order(StringComparer.Ordinal))
            environments.Add(new TransferEnvironmentScope(env,
                await _db.ExternalIntegrations.CountAsync(e => e.EnvironmentId == env, ct),
                await _db.IntegrationDefinitions.CountAsync(e => e.EnvironmentId == env, ct),
                await _db.IntegrationEvidenceBindings.CountAsync(e => e.EnvironmentId == env, ct),
                _flows.Flows(env).Count,
                await _db.IntegrationSuites.CountAsync(s => s.EnvironmentId == env, ct)));
        return new TransferScopes(projects, environments);
    }
}
