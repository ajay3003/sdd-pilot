using BirkNext.Api.Data;
using BirkNext.ContractTesting;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Services.ContractTesting;

/// <summary>
/// Project-wide Contract Testing coverage: an aggregation of the per-contract coverage (<see cref="ContractTestService.CoverageAsync"/>, the one set
/// of attribution rules) over the contracts of one project. Each contract counts once, at its latest version (contracts continue across snapshots,
/// so a contract seen in several snapshots is never counted twice); older versions with execution evidence are listed separately as history and
/// never enter the current denominator. Totals are sums of exact numerators and denominators per metric — never averages of percentages. Readiness
/// blockers are counted only when a Target Environment is given. Reading only.
/// </summary>
public sealed class ContractProjectCoverageService(AppDbContext db, ContractRegistryService registry, ContractTestService tests, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async Task<ProjectCoverageReport?> CoverageAsync(string projectKey, ContractTestEnvironment? environment, CancellationToken ct)
    {
        var project = (await registry.ProjectsAsync(ct)).FirstOrDefault(p => p.Keys.Contains(projectKey) || p.Key == projectKey);
        var group = await registry.ProjectGroupAsync(projectKey, ct);
        var keys = group.ToList();
        var contracts = await db.ApiContracts.AsNoTracking().Where(c => c.ProjectKey != null && keys.Contains(c.ProjectKey)).OrderBy(c => c.Name).ToListAsync(ct);
        if (project is null && contracts.Count == 0) return null;
        var rows = new List<ProjectCoverageContract>();
        foreach (var c in contracts)
        {
            var versions = await db.ApiContractVersions.AsNoTracking().Where(v => v.ContractId == c.Id).OrderByDescending(v => v.Version).Select(v => new { v.Version, v.SourcePath, v.OperationCount }).ToListAsync(ct);
            if (versions.Count == 0) continue;
            var latest = versions[0].Version;
            if (await tests.CoverageAsync(c.Key, latest, ct) is not { } coverage) continue;
            var history = new List<HistoricalCoverage>();
            foreach (var older in versions.Skip(1))
                if (await tests.CoverageAsync(c.Key, older.Version, ct) is { } old && Metric(old, "operations-executed") is { Numerator: > 0 } executed)
                    history.Add(new(older.Version, executed.Denominator, executed.Numerator, Metric(old, "operations-passing")?.Numerator ?? 0, old.LatestExecutionAt));
            var views = await tests.ListAsync(c.Key, environment, ct);
            rows.Add(new(c.Key, c.Name, Enum.Parse<ApiContractProtocol>(c.Protocol), ContractRegistryService.ModuleOfSourcePath(versions[0].SourcePath), latest, coverage)
            {
                Tests = views.Count,
                TestsOnOlderVersions = views.Count(t => t.Test.ContractVersion < latest),
                ReadinessAssessed = environment is not null,
                ReadyTests = environment is null ? 0 : views.Count(t => t.Readiness.State == ContractTestReadinessState.Ready),
                BlockedTests = environment is null ? 0 : views.Count(t => t.Readiness.State != ContractTestReadinessState.Ready),
                BlockerAreas = environment is null ? [] : [.. views.SelectMany(t => t.Readiness.Items.Where(i => i.State is ReadinessItemState.Blocked or ReadinessItemState.Missing).Select(i => i.Area))
                    .GroupBy(a => a).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} ({g.Count()})")],
                Lifecycle = Enum.TryParse<ContractLifecycle>(c.Lifecycle, out var l) ? l : ContractLifecycle.Active,
                History = history,
            });
        }
        var totals = rows.SelectMany(r => r.Coverage.Metrics).GroupBy(m => m.Id)
            .Select(g => new ProjectCoverageTotal(g.Key, g.First().Label, g.First().Group, g.Sum(m => m.Numerator), g.Sum(m => m.Denominator), g.Sum(m => m.NotAssessed))).ToList();
        return new(project?.Key ?? projectKey, project?.DisplayName ?? projectKey, keys.OrderBy(k => k, StringComparer.Ordinal).ToList(), project?.LatestSnapshotId, rows, totals, _clock.GetUtcNow(),
        [
            "Current coverage counts each contract once at its latest version; older versions' executions are history, never part of the current denominator.",
            "Totals add exact numerators and denominators per metric across contracts (no averaged percentages).",
            environment is null ? "Readiness blockers are not assessed: no Target Environment was given." : $"Readiness assessed for {environment.EnvironmentId}.",
            "Kode 6/7 relevance per contract is not supported by this view (no evidence-backed classification per contract yet).",
        ]);
    }

    private static ContractCoverageMetric? Metric(ContractCoverageReport r, string id) => r.Metrics.FirstOrDefault(m => m.Id == id);
}
