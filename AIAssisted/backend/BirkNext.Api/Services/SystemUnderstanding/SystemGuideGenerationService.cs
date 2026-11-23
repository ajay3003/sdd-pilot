using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.Api.Services.Specifications;
using BirkNext.Api.Services.TestPlanning;
using BirkNext.SystemUnderstanding;
using BirkNext.TestCoverage;
using BirkNext.TestPlanning;
using D = BirkNext.Api.Services.TestPlanning.TestScenarioDiscoveryEngine;

namespace BirkNext.Api.Services.SystemUnderstanding;

/// <summary>
/// Runs durable System Understanding generation jobs: reads the job's source snapshot and the specification captured with its import (so a retry
/// needs no archive), runs the existing discovery providers over them (never a second set of parsers; the guide provider itself is left out so
/// the guide never feeds itself), and hands the normalised scenarios to the one generator (<see cref="SystemGuideService.GenerateCandidateAsync"/>).
/// A provider that fails makes the job fail with its diagnostics: a partial result is never presented as a complete proposal.
/// </summary>
public sealed class SystemGuideGenerationService(SystemGuideService guides, IEnumerable<ITestScenarioCandidateProvider> providers, IqrSourceStore store,
    SpecificationCatalogService? catalogs = null, ILogger<SystemGuideGenerationService>? logger = null)
{
    /// <summary>Runs pending jobs (at most <paramref name="max"/>); returns how many ran.</summary>
    public async Task<int> ProcessPendingAsync(int max, CancellationToken ct)
    {
        var ran = 0;
        while (ran < max && await guides.ClaimNextJobAsync(ct) is { } job)
        {
            ran++;
            try
            {
                var candidate = await RunAsync(job.GuideKey, job.SnapshotId, ct);
                await guides.FinishJobAsync(job.Id, candidate?.Id, null, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception e)
            {
                logger?.LogWarning("System Understanding generation job {Job} failed: {Type}", job.Id, e.GetType().Name);
                await guides.FinishJobAsync(job.Id, null, e is SystemGuideException or InvalidOperationException ? e.Message : $"Genereringen feilet ({e.GetType().Name}). Prøv igjen.", ct);
            }
        }
        return ran;
    }

    /// <summary>One generation for one guide and snapshot (also used directly by tests and a manual "generate now").</summary>
    public async Task<SystemGuideCandidateView?> RunAsync(string guideKey, Guid snapshotId, CancellationToken ct)
    {
        var snapshot = await store.FindSourceAnalysisAsync(snapshotId, ct) ?? throw new InvalidOperationException("Kildeversjonen finnes ikke lenger (fjernet eller nullstilt); genereringen kan ikke kjøres.");
        var role = guides.GuidesFor(snapshot).FirstOrDefault(g => g.GuideKey == guideKey).Role
            ?? throw new InvalidOperationException("Kildeversjonen hører ikke til denne veiledningen.");
        var specification = catalogs is null ? null : await catalogs.EvidenceAsync(snapshot.Id, null, ct, snapshot.ProjectImport?.ImportId);
        var specs = specification?.Evidence ?? [];
        List<CoverageWorkspaceEvidence?> workspaces = specs.Count == 0 ? [null] : [.. specs.Select(e => (CoverageWorkspaceEvidence?)(e.Evidence with { ArtifactScope = specs.Count > 1 ? e.Path : null }))];
        var candidates = new List<DiscoveredScenario>();
        var failed = new List<string>();
        var evaluated = new HashSet<string>(StringComparer.Ordinal);
        foreach (var provider in providers.Where(p => p.CapabilityId != D.SystemGuide))
            foreach (var workspace in provider.CapabilityId == D.Specification ? workspaces : [workspaces[0]])
            {
                try
                {
                    var output = await provider.DiscoverAsync(new ScenarioDiscoveryContext(guideKey, null, snapshot, workspace, null), ct);
                    if (output.Capability.State == DiscoveryCapabilityState.Failed) failed.Add(output.Capability.Label);
                    if (output.Capability.State == DiscoveryCapabilityState.Available) evaluated.Add(provider.CapabilityId);
                    candidates.AddRange(output.Candidates);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    logger?.LogWarning("Guide generation: capability {Capability} failed ({Type})", provider.CapabilityId, e.GetType().Name);
                    failed.Add(D.Label(provider.CapabilityId));
                }
            }
        if (failed.Count > 0)
            throw new InvalidOperationException($"Ufullstendig: {string.Join(", ", failed.Distinct())} kunne ikke leses. Ingen delvis forslag er laget; prøv igjen.");
        var final = D.Finalize(guideKey, snapshot.Id.ToString(), candidates, []);
        return await guides.GenerateCandidateAsync(guideKey, role, snapshot, final,
            specification is { } held ? $"{held.Catalog.ProjectKey}#r{held.Catalog.Revision}:{held.Catalog.Fingerprint}" : null, ct, evaluated);
    }
}

/// <summary>Runs pending System Understanding generation jobs in the background (database-backed: jobs survive restarts; nothing external is needed).</summary>
public sealed class SystemGuideGenerationWorker(IServiceScopeFactory scopes, ILogger<SystemGuideGenerationWorker> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken); } catch (OperationCanceledException) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<SystemGuideGenerationService>().ProcessPendingAsync(5, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception e) { logger.LogWarning("System Understanding generation worker pass failed: {Type}", e.GetType().Name); }
            try { await Task.Delay(Interval, stoppingToken); } catch (OperationCanceledException) { return; }
        }
    }
}
