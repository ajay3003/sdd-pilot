using BirkNext.TestCoverage;
using BirkNext.TestPlanning;
using BirkNext.Web.Models;
using BirkNext.Web.Services.Explorers;

namespace BirkNext.Web.Services;

/// <summary>
/// The context every Manual Testing page reads, from the same authorities the rest of BirkNext uses: the selected project (shared workspace
/// projection), its source snapshot (project evidence / imported package), its saved plans (Test Plan Suggestions), its last stored discovery,
/// its specification's requirements (the existing parser, as Test Coverage reads them). Manual runs are not BirkNext's: Azure DevOps Test Plans executes. Nothing is copied or cached here
/// beyond one page load; a missing service simply leaves its part empty.
/// </summary>
public sealed class ManualTestingWorkspace(IServiceProvider services)
{
    public sealed record Snapshot(
        string? ProjectKey, string? ProjectName, string? EnvironmentId, Guid? SnapshotId, string? SnapshotBasis,
        TestPlanWorkspace? Plans, ScenarioDiscoveryResult? Discovery, CoverageWorkspaceEvidence? Evidence, List<ProjectRequirementOption>? Requirements, string? Error)
    {
        public ManualTestingPresentation.Facts Facts => new(ProjectKey, ProjectName, Plans?.Drafts ?? [], Discovery, Requirements?.Count,
            Requirements is { Count: > 0 } ? null : RequirementLinkPresentation.SpecificationChoiceNeeded(Specification), AnyProjectImported);

        /// <summary>Whether any project is imported at all (decides "Import a project" or "Choose a project" when none is selected).</summary>
        public bool AnyProjectImported { get; init; } = true;

        /// <summary>The Specification role as the explorers resolve it: says whether a specification still has to be chosen (a monorepo's modules).</summary>
        public ArtifactExplorerState? Specification { get; init; }
    }

    private T? Get<T>() where T : class => services.GetService(typeof(T)) as T;

    public async Task<Snapshot> LoadAsync(bool discovery = true, bool requirements = true, CancellationToken ct = default)
    {
        var (key, name) = await ProjectAsync();
        string? environment = null;
        if (Get<IFrontendAnalysisContextFactory>() is { } contexts)
            try { environment = (await contexts.GetActiveContextAsync()).ActiveProfileOrNull?.Id; } catch (InvalidOperationException) { }
        var (snapshotId, basis) = ResolveSnapshot(services);
        if (key is null)
            return new(null, null, environment, snapshotId, basis, null, null, null, null, null)
            {
                AnyProjectImported = Get<IWorkspaceSessionService>() is not { } session || session.SddLifecycle.ProjectImports.Count > 0,
            };

        string? error = null;
        TestPlanWorkspace? plans = null;
        ScenarioDiscoveryResult? last = null;
        if (Get<ITestPlanSuggestionsApiService>() is { } api)
        {
            (plans, error) = await api.ProjectAsync(key, environment, ct);
            if (discovery) { var (d, _) = await api.LastDiscoveryAsync(key, snapshotId, ct); last = d?.ProjectKey == key ? d : null; }
        }
        else error = "Test Plan Suggestions is not available in this host.";
        var (evidence, specification) = requirements ? await EvidenceWithStateAsync(name) : (null, null);
        return new(key, name, environment, snapshotId, basis, plans, last, evidence, RequirementLinkPresentation.Options(evidence), error) { Specification = specification };
    }

    private async Task<(string? Key, string? Name)> ProjectAsync()
    {
        if (Get<ICurrentWorkspaceProjection>() is not { } projection) return (null, null);
        try
        {
            var w = await projection.GetAsync();
            return string.IsNullOrWhiteSpace(w.ProjectSlug) ? (null, null) : (w.ProjectSlug, w.ProjectName ?? w.ProjectSlug);
        }
        catch (InvalidOperationException) { return (null, null); }
    }

    /// <summary>The project's own documents, parsed by the existing specification parser (the evidence Test Coverage reads); null when unreadable.</summary>
    public async Task<CoverageWorkspaceEvidence?> EvidenceAsync(string? projectName) => (await EvidenceWithStateAsync(projectName)).Evidence;

    private async Task<(CoverageWorkspaceEvidence? Evidence, ArtifactExplorerState? Specification)> EvidenceWithStateAsync(string? projectName)
    {
        ArtifactExplorerState? specification = null;
        try
        {
            if (Get<IArtifactExplorerContext>() is { } artifacts) specification = await artifacts.GetStateAsync(WorkspaceArtifactType.Specification);
            if (Get<IWorkspaceSessionService>() is { } session)
                return (TestCoveragePresentation.WorkspaceEvidence(session, Get<IReviewContextProvider>()?.GetCurrent(), projectName, specification), specification);
        }
        catch (Exception e) when (e is not OperationCanceledException) { }
        return (null, specification);
    }

    /// <summary>The project's snapshot: the imported package of the current workspace scope, else the project evidence's main package. Never the
    /// newest snapshot of some other repository.</summary>
    public static (Guid? Id, string? Basis) ResolveSnapshot(IServiceProvider services)
    {
        try
        {
            if (services.GetService(typeof(ProjectEvidence.IProjectEvidenceService)) is not ProjectEvidence.IProjectEvidenceService evidence) return (null, null);
            var view = evidence.Peek();
            var scope = (services.GetService(typeof(IArtifactExplorerContext)) as IArtifactExplorerContext)?.CurrentScope;
            if (scope is { } s && s.StartsWith("import:", StringComparison.Ordinal) && view.Packages.FirstOrDefault(p => p.ImportId == s["import:".Length..]) is { SnapshotId: { } imported } package)
                return (imported, $"Imported package {package.DisplayName}");
            if (view.Main?.SnapshotId is { } main) return (main, $"Project evidence: {view.Main.DisplayName}");
        }
        catch (InvalidOperationException) { }
        return (null, null);
    }
}
