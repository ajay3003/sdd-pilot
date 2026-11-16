using BirkNext.Integrations;
using BirkNext.IntegrationSuites;
using BirkNext.IntegrationTopology;
using BirkNext.Web.Models;
using I = BirkNext.Web.Services.IntegrationTestingPresentation;

namespace BirkNext.Web.Services;

/// <summary>
/// Reads what Integration Testing shows, from the modules that own it: the project (shared workspace projection), the active Target Environment,
/// its Integration Topology (the read endpoint — never the explicit discovery), the Integrations catalog, the project's declared journeys,
/// Integration Test Suites of the project's own source snapshot, and Integration Quality Review runs. Nothing is written, discovered or run here;
/// a failed read is reported with its source and leaves that part empty.
/// </summary>
public sealed class IntegrationTestingWorkspace(IServiceProvider services)
{
    private T? Get<T>() where T : class => services.GetService(typeof(T)) as T;

    public async Task<I.Facts> LoadAsync(CancellationToken ct = default)
    {
        string? key = null, name = null;
        if (Get<ICurrentWorkspaceProjection>() is { } projection)
            try { var w = await projection.GetAsync(); if (!string.IsNullOrWhiteSpace(w.ProjectSlug)) (key, name) = (w.ProjectSlug, w.ProjectName ?? w.ProjectSlug); }
            catch (InvalidOperationException) { }
        FrontendAnalysisProfile? profile = null;
        if (Get<IFrontendAnalysisContextFactory>() is { } contexts)
            try { var c = await contexts.GetActiveContextAsync(); profile = c.ActiveTargetError is null && c.ActiveProfile is { Id.Length: > 0 } p ? p : null; }
            catch (InvalidOperationException) { }
        if (key is null) return I.Facts.Empty with { EnvironmentId = profile?.Id, TargetName = profile?.Name };
        // R7: topology, catalog and IQR runs belong to the environment; journeys and suites to the project. An environment bound to another
        // project contributes nothing here (identities are never merged); an unbound one is used and labelled.
        var resolution = ProjectEnvironmentResolution.Resolve(key, profile, Get<IFrontendAnalysisSettingsService>()?.Settings.Profiles ?? []);
        if (!resolution.EnvironmentFactsApply) profile = null;

        var errors = new List<string>();
        IntegrationTopologyView? topology = null;
        List<IntegrationDefinition> integrations = [];
        List<IntegrationReviewRunSummary> reviews = [];
        if (profile is not null)
        {
            // The topology client is not a DI registration; IntegrationTopologyApiService.Resolve is how every page obtains it.
            var topologyApi = IntegrationTopologyApiService.Resolve(services);
                try { var (view, error) = await topologyApi.GetAsync(profile.Id, [], ct); topology = view; if (error is not null) errors.Add($"Integration Topology: {error}"); }
                catch (Exception e) when (e is not OperationCanceledException) { errors.Add("Integration Topology could not be read."); }
            if (Get<IIntegrationCatalogApiService>() is { } catalog)
            {
                try { integrations = [.. (await catalog.GetCatalogAsync(profile, ct)).Integrations.Where(i => i.Enabled)]; }
                catch (Exception e) when (e is not OperationCanceledException) { errors.Add("The Integrations catalog could not be read."); }
                try { reviews = [.. (await catalog.HistoryAsync(profile.Id, ct)).OrderByDescending(r => r.CompletedAt)]; }
                catch (Exception e) when (e is not OperationCanceledException) { errors.Add("Integration Quality Review runs could not be read."); }
            }
        }
        List<DeclaredIntegrationJourney> journeys = [];
        if (Get<IDeclaredIntegrationJourneyApiService>() is { } journeyApi)
            try { var (list, error) = await journeyApi.ListAsync(key, ct); journeys = [.. list ?? []]; if (error is not null) errors.Add($"Integration Journeys: {error}"); }
            catch (Exception e) when (e is not OperationCanceledException) { errors.Add("Integration Journeys could not be read."); }
        List<IntegrationSuiteSummary> suites = [];
        var other = 0;
        if (Get<IIntegrationSuiteApiService>() is { } suiteApi)
            try
            {
                var (list, error) = await suiteApi.ListAsync(ct);
                if (error is not null) errors.Add($"Integration Test Suites: {error}");
                // Suites belong to a source snapshot, not to a project: show the ones generated from this project's own snapshot.
                var (snapshot, _) = ManualTestingWorkspace.ResolveSnapshot(services);
                foreach (var s in list ?? [])
                    if (snapshot is { } own && s.SnapshotId == own) suites.Add(s); else other++;
            }
            catch (Exception e) when (e is not OperationCanceledException) { errors.Add("Integration Test Suites could not be read."); }
        return new(key, name, profile?.Id, profile?.Name, topology, integrations, journeys, [.. suites.OrderByDescending(s => s.GeneratedAt)], other, reviews, errors,
            resolution.State == ProjectEnvironmentResolution.State.NoEnvironment ? null : resolution.Notice);
    }
}
