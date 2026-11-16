using BirkNext.ApiReview;
using BirkNext.Applicability;
using BirkNext.CriticalE2E;
using BirkNext.PerformanceTests;
using BirkNext.Integrations;
using BirkNext.Technology;
using BirkNext.Web.Models;
using Microsoft.JSInterop;
using TT = BirkNext.Web.Services.TechnicalTestingPresentation;

namespace BirkNext.Web.Services;

/// <summary>
/// The context every Technical Testing page reads, from the authorities the specialist modules already use: the selected project (shared
/// workspace projection), its source snapshot (project evidence), the active Target Environment, Technology Coverage applicability, and the
/// stored results of Frontend Quality Review and API Quality Review (this browser), Integration Quality Review, performance runs and Critical E2E
/// flows (backend stores). Only stored state is read: nothing contacts a target, probes a provider or starts a check. A missing service or a
/// failed read leaves that area's part empty with the reason, never "none".
/// </summary>
public sealed class TechnicalTestingWorkspace(IServiceProvider services)
{
    public sealed record Snapshot(TT.Facts Facts, Guid? SnapshotId, string? EnvironmentId, int? TestDataAssets);

    private T? Get<T>() where T : class => services.GetService(typeof(T)) as T;

    public async Task<Snapshot> LoadAsync(CancellationToken ct = default)
    {
        var (key, name) = await ProjectAsync();
        FrontendAnalysisProfile? profile = null;
        if (Get<IFrontendAnalysisContextFactory>() is { } contexts)
            try
            {
                var context = await contexts.GetActiveContextAsync();
                profile = context.ActiveTargetError is null && context.ActiveProfile is { Id.Length: > 0 } active ? active : null;
            }
            catch (InvalidOperationException) { }
        var (snapshotId, basis) = ManualTestingWorkspace.ResolveSnapshot(services);
        if (key is null)
            return new(new(null, null, profile?.Name, profile?.TargetUrl, basis, [.. TT.Areas.Select(a => new AreaFacts(a, null, [], [], []))]), snapshotId, profile?.Id, null);

        // The same project ↔ environment rule as Integration Testing: an environment bound to another project contributes no results here.
        var resolution = ProjectEnvironmentResolution.Resolve(key, profile, Get<IFrontendAnalysisSettingsService>()?.Settings?.Profiles ?? []);
        var notice = resolution.State is ProjectEnvironmentResolution.State.Unbound or ProjectEnvironmentResolution.State.OtherProject ? resolution.Notice : null;
        var activeName = profile?.Name;
        if (resolution.State == ProjectEnvironmentResolution.State.OtherProject) profile = null;

        var (reviews, applicabilityError) = await ApplicabilityAsync(profile, snapshotId, ct);
        ReviewApplicability? For(TechnicalArea a) => reviews?.GetValueOrDefault(TT.ReviewId(a));

        var targetGaps = new List<string>();
        if (resolution.State == ProjectEnvironmentResolution.State.OtherProject) targetGaps.Add($"Target Environment {activeName} belongs to another project. Switch to an environment bound to this project in Target Environments.");
        else if (profile is null) targetGaps.Add("No Target Environment is active.");
        else if (string.IsNullOrWhiteSpace(profile.TargetUrl)) targetGaps.Add($"Target Environment {profile.Name} has no target URL.");

        var areas = new List<AreaFacts>
        {
            await FrontendAsync(profile, For(TechnicalArea.Frontend), targetGaps),
            await ApiAsync(profile, For(TechnicalArea.Api), targetGaps, snapshotId, ct),
            await IntegrationAsync(key, profile, For(TechnicalArea.Integration), ct),
            await PerformanceAsync(profile, For(TechnicalArea.Performance), targetGaps, snapshotId, ct),
            await CriticalE2EAsync(profile, For(TechnicalArea.CriticalE2E), ct),
        };
        int? assets = null;
        if (Get<ITestDataApiService>() is { } testData)
            try { var (catalog, _) = await testData.CatalogAsync(ct); assets = catalog?.Assets.Count; } catch (Exception e) when (e is not OperationCanceledException) { }
        return new(new(key, name, profile?.Name, profile?.TargetUrl, basis, areas, applicabilityError, notice), snapshotId, profile?.Id, assets);
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

    /// <summary>
    /// Applicability of each review for the selected project: Technology Coverage of the project's own source snapshot (the imported package
    /// in scope, else the project evidence's main package) evaluated by the shared <see cref="ApplicabilityEvaluator"/> — the same rules the
    /// sidebar and Technology Coverage use. Without a project snapshot no coverage is read (never the newest snapshot of another project).
    /// </summary>
    private async Task<(IReadOnlyDictionary<string, ReviewApplicability>? Reviews, string? Error)> ApplicabilityAsync(FrontendAnalysisProfile? profile, Guid? snapshotId, CancellationToken ct)
    {
        if (Get<ITechnologyCoverageApiService>() is not { } api) return (null, "Technology coverage is not available in this host.");
        ProjectTechnologyCoverage? coverage = null;
        try { if (snapshotId is { } s) coverage = await api.GetAsync(profile?.Id ?? "", s, ct); }
        catch (Exception e) when (e is not OperationCanceledException) { return (null, "Technology coverage could not be read, so which areas apply is not known."); }
        IReadOnlySet<WorkspaceArtifactType> present = new HashSet<WorkspaceArtifactType>();
        if (Get<ICurrentWorkspaceProjection>() is { } projection)
            try { present = await projection.GetRolePresenceAsync(); } catch (InvalidOperationException) { }
        var reviews = ApplicabilityEvaluator.EvaluateAll(TechnologyCoveragePresentation.Input(coverage, profile,
            present.Contains(WorkspaceArtifactType.Specification), present.Contains(WorkspaceArtifactType.Plan) || present.Contains(WorkspaceArtifactType.Constitution)));
        return (reviews, null);
    }

    private static List<StaticFact> FromApplicability(ReviewApplicability? a) =>
        a is null ? [] : [.. a.Evidence.Take(3).Select(e => new StaticFact(e, "technology-coverage"))];

    // Frontend Quality Review: runtime only, stored per Target Environment in this browser. Browser discovery evidence is also local.
    private async Task<AreaFacts> FrontendAsync(FrontendAnalysisProfile? profile, ReviewApplicability? a, List<string> targetGaps)
    {
        var facts = FromApplicability(a);
        var runs = new List<RuntimeResult>();
        string? error = null;
        if (profile is not null)
        {
            if (Get<FrontendQualityReviewHistory>() is { } history)
                try
                {
                    await history.LoadAsync(profile.Id);
                    runs.AddRange(history.For(profile.Id).Select(r => Frontend(r, profile)));
                }
                catch (Exception e) when (e is not OperationCanceledException) { error = "Previous Frontend Quality Review runs could not be read."; }
            if (Get<IEndpointDiscoveryService>() is { } discovery && Get<IJSRuntime>() is { } js)
                try
                {
                    await discovery.LoadAsync(js);
                    var snapshot = discovery.GetSnapshot(profile.Id);
                    if (snapshot.Pages.Count > 0)
                        facts.Add(new($"{snapshot.Pages.Count} page{(snapshot.Pages.Count == 1 ? "" : "s")} discovered in the browser (last {snapshot.UpdatedAt.ToLocalTime():yyyy-MM-dd HH:mm})",
                            TT.TargetEnvironmentsRoute + "&tab=browser"));
                }
                catch (Exception e) when (e is not OperationCanceledException) { }
        }
        return new(TechnicalArea.Frontend, a, facts, targetGaps, runs, error);
    }

    public static RuntimeResult Frontend(FrontendQualityReviewHistoryEntry r, FrontendAnalysisProfile profile)
    {
        var (state, verdict) = r.Disposition switch
        {
            FrontendQualityReleaseDisposition.Blocked => (AreaRuntime.Failed, "Blocking issues found"),
            FrontendQualityReleaseDisposition.ReviewRequired => (AreaRuntime.ReviewRequired, "Review required"),
            FrontendQualityReleaseDisposition.NoAutomatedBlockDetected => r.RequiredAssessed < r.RequiredTotal
                ? (AreaRuntime.Incomplete, "Incomplete") : (AreaRuntime.Passed, "No blocking issue detected"),
            _ => (AreaRuntime.Incomplete, "Incomplete"),
        };
        return new(state, verdict, r.CompletedAt, profile.Name, null, "Frontend Quality Review", "frontend-quality-review?tab=History",
            Detail: $"{r.RequiredAssessed} of {r.RequiredTotal} required checks assessed · {r.IssueCount} issue{(r.IssueCount == 1 ? "" : "s")}");
    }

    // API Quality Review: runtime results per Target Environment in this browser; contracts (static) in the backend contract store.
    private async Task<AreaFacts> ApiAsync(FrontendAnalysisProfile? profile, ReviewApplicability? a, List<string> targetGaps, Guid? snapshotId, CancellationToken ct)
    {
        var facts = FromApplicability(a);
        var runs = new List<RuntimeResult>();
        string? error = null;
        if (profile is not null && Get<IApiReviewHistoryService>() is { } history && Get<IJSRuntime>() is { } js)
            try
            {
                await history.LoadAsync(js);
                // API Quality Review keeps the full report of the latest run only: older runs are summaries and must not open the latest report as if it were theirs.
                runs.AddRange(history.For(profile.Id).Runs.OrderByDescending(r => r.GeneratedAt).Select((r, i) => i == 0 ? Api(r) : Api(r) with { Href = "", Detail = Api(r).Detail + " · summary only; the full report is kept for the latest run" }));
            }
            catch (Exception e) when (e is not OperationCanceledException) { error = "Previous API Quality Review runs could not be read."; }
        if (Get<IContractTestingApiService>() is { } contracts)
            try
            {
                var (projects, contractError) = await contracts.ProjectsAsync(ct);
                // Contracts belong to a repository identity; this project's is the one whose latest analysed snapshot is the project's snapshot.
                // A project with no contracts in its source has no contract evidence to show ("0 contracts" is not evidence).
                if (snapshotId is { } s && projects?.FirstOrDefault(p => p.LatestSnapshotId == s) is { Contracts: > 0 } project)
                    facts.Add(new($"{project.Contracts} contract{(project.Contracts == 1 ? "" : "s")} from the analysed source (not runtime validation)",
                        $"api-quality-review/contracts?project={Uri.EscapeDataString(project.Key)}"));
                else if (contractError is not null) error ??= "Contracts could not be read.";
            }
            catch (Exception e) when (e is not OperationCanceledException) { error ??= "Contracts could not be read."; }
        return new(TechnicalArea.Api, a, facts, targetGaps, runs, error);
    }

    public static RuntimeResult Api(ApiReviewRunSummary r)
    {
        var (state, verdict) = r switch
        {
            { Targets: 0 } or { Completed: 0, Blocked: > 0 } => (AreaRuntime.Blocked, "Blocked"),
            { High: > 0 } => (AreaRuntime.Failed, $"{r.High} high-severity finding{(r.High == 1 ? "" : "s")}"),
            _ when r.Completed < r.Targets => (AreaRuntime.Incomplete, "Incomplete"),
            { Medium: > 0 } => (AreaRuntime.ReviewRequired, $"{r.Medium} medium finding{(r.Medium == 1 ? "" : "s")}"),
            _ => (AreaRuntime.Passed, "No high or medium finding"),
        };
        return new(state, verdict, r.GeneratedAt, r.EnvironmentName, null, "API Quality Review", "api-quality-review?tab=Findings",
            Detail: $"{r.Completed} of {r.Targets} targets completed" + (r.Blocked > 0 ? $" · {r.Blocked} blocked" : ""));
    }

    // Integrations: declared journeys (static, per project) and Integration Quality Review runs (backend, per Target Environment).
    private async Task<AreaFacts> IntegrationAsync(string projectKey, FrontendAnalysisProfile? profile, ReviewApplicability? a, CancellationToken ct)
    {
        var facts = FromApplicability(a);
        var runs = new List<RuntimeResult>();
        string? error = null;
        if (Get<IDeclaredIntegrationJourneyApiService>() is { } journeys)
            try
            {
                var (list, journeyError) = await journeys.ListAsync(projectKey, ct);
                if (list is { Count: > 0 }) facts.Add(new($"{list.Count} declared integration journey{(list.Count == 1 ? "" : "s")}", "integration-journeys"));
                else if (journeyError is not null) error = "Declared journeys could not be read.";
            }
            catch (Exception e) when (e is not OperationCanceledException) { error = "Declared journeys could not be read."; }
        if (profile is not null && Get<IIntegrationCatalogApiService>() is { } iqr)
            try { runs.AddRange((await iqr.HistoryAsync(profile.Id, ct)).OrderByDescending(r => r.CompletedAt).Select(r => Integration(r, profile.Name))); }
            catch (Exception e) when (e is not OperationCanceledException) { error ??= "Integration Quality Review runs could not be read."; }
        return new(TechnicalArea.Integration, a, facts, profile is null ? ["No Target Environment is active."] : [], runs, error);
    }

    public static RuntimeResult Integration(IntegrationReviewRunSummary r, string? environment)
    {
        var (state, verdict) = r.Outcome switch
        {
            IntegrationReviewOutcome.Completed when r.Findings > 0 => (AreaRuntime.ReviewRequired, $"{r.Findings} finding{(r.Findings == 1 ? "" : "s")}"),
            IntegrationReviewOutcome.Completed => (AreaRuntime.Passed, "Completed without findings"),
            IntegrationReviewOutcome.CompletedWithLimitations => (AreaRuntime.Incomplete, "Completed with limitations"),
            IntegrationReviewOutcome.ManualReviewRequired => (AreaRuntime.ReviewRequired, "Manual review required"),
            _ => (AreaRuntime.Incomplete, "Nothing assessed"),
        };
        return new(state, verdict, r.CompletedAt, environment, null, "Integration Quality Review", $"integration-quality-review?view=review",
            Detail: $"{r.Topics} topic{(r.Topics == 1 ? "" : "s")} · {r.Findings} finding{(r.Findings == 1 ? "" : "s")}");
    }

    // Performance: runs stored in the backend per Target Environment (no provider probe: the overview endpoint is not used here).
    private async Task<AreaFacts> PerformanceAsync(FrontendAnalysisProfile? profile, ReviewApplicability? a, List<string> targetGaps, Guid? snapshotId, CancellationToken ct)
    {
        var runs = new List<RuntimeResult>();
        string? error = null;
        if (profile is not null && Get<IPerformanceTestApiService>() is { } api)
            try { runs.AddRange((await api.RunsAsync(profile.Id, null, ct)).OrderByDescending(r => r.FinishedAt ?? r.StartedAt ?? r.CreatedAt).Select(r => Performance(r, profile.Name, snapshotId))); }
            catch (Exception e) when (e is not OperationCanceledException) { error = "Performance runs could not be read."; }
        return new(TechnicalArea.Performance, a, FromApplicability(a), targetGaps, runs, error);
    }

    public static RuntimeResult Performance(PerformanceTestRun r, string? environment, Guid? currentSnapshot)
    {
        var (state, verdict) = r.State switch
        {
            PerformanceRunState.Completed => r.Verdict switch
            {
                PerformanceQualityVerdict.Pass => (AreaRuntime.Passed, "Targets met"),
                PerformanceQualityVerdict.Warning => (AreaRuntime.ReviewRequired, "Warning"),
                PerformanceQualityVerdict.Fail => (AreaRuntime.Failed, "Targets not met"),
                _ => (AreaRuntime.Incomplete, "Not assessed against targets"),
            },
            PerformanceRunState.Blocked => (AreaRuntime.Blocked, "Blocked"),
            PerformanceRunState.ExecutionFailed or PerformanceRunState.TimedOut => (AreaRuntime.Incomplete, "Did not complete"),
            PerformanceRunState.Cancelled or PerformanceRunState.Cancelling => (AreaRuntime.Incomplete, "Cancelled"),
            _ => (AreaRuntime.Running, "Running"),
        };
        var stale = r.SourceSnapshotId is { } ran && currentSnapshot is { } now && ran != now ? "Ran against an earlier source snapshot than the project's current one." : null;
        return new(state, verdict, r.FinishedAt ?? r.StartedAt ?? r.CreatedAt, environment ?? r.EnvironmentType, r.VersionLabel, "Performance Test Review", "performance-test-review",
            stale, r.DefinitionSnapshot?.Name is { Length: > 0 } scenario ? $"Scenario: {scenario}" : null);
    }

    // Critical E2E: flow definitions (static) and each flow's latest result (runtime); the release verdict needs a build and stays in the module.
    private async Task<AreaFacts> CriticalE2EAsync(FrontendAnalysisProfile? profile, ReviewApplicability? a, CancellationToken ct)
    {
        var facts = FromApplicability(a);
        var gaps = new List<string>();
        var runs = new List<RuntimeResult>();
        string? error = null;
        if (profile is null) gaps.Add("No Target Environment is active.");
        else if (Get<ICriticalE2EApiService>() is { } api)
            try
            {
                var overview = await api.OverviewAsync(new CriticalE2EOverviewRequest { ProfileId = profile.Id, EnvironmentId = profile.Id, EnvironmentName = profile.Name }, ct);
                var enabled = overview.Flows.Where(f => f.Enabled).ToList();
                if (overview.Flows.Count > 0)
                    facts.Add(new($"{enabled.Count} enabled critical flow{(enabled.Count == 1 ? "" : "s")} ({enabled.Count(f => f.RequiredForRelease)} required for release)", "critical-e2e-regression"));
                else gaps.Add("No critical flow is defined yet.");
                runs.AddRange(CriticalE2E(overview.Flows, profile.Name));
            }
            catch (Exception e) when (e is not OperationCanceledException) { error = "Critical E2E flows could not be read."; }
        return new(TechnicalArea.CriticalE2E, a, facts, gaps, runs, error);
    }

    /// <summary>Each flow's latest result, the most severe first (each flow is a different subject, so the newest alone would hide a failure).</summary>
    public static IEnumerable<RuntimeResult> CriticalE2E(IEnumerable<CriticalE2EFlowSummary> flows, string? environment) =>
        flows.Where(f => f.LastRunAt is not null && f.LastStatus != CriticalE2EStatus.NotRun)
            .Select(f =>
            {
                var (state, verdict) = f.LastStatus switch
                {
                    CriticalE2EStatus.Passed => (AreaRuntime.Passed, "Passed"),
                    CriticalE2EStatus.Failed => (AreaRuntime.Failed, "Failed"),
                    CriticalE2EStatus.Blocked => (AreaRuntime.Blocked, "Blocked"),
                    CriticalE2EStatus.Running => (AreaRuntime.Running, "Running"),
                    _ => (AreaRuntime.Incomplete, f.LastResultLabel ?? "Not proven"),
                };
                return new RuntimeResult(state, verdict, f.LastRunAt!.Value, environment, null, "Critical E2E", "critical-e2e-regression",
                    f.LastRunStale ? "The flow changed after this run, or the run is older than the flow's current definition." : null, f.Name);
            })
            .OrderBy(r => r.State switch { AreaRuntime.Failed => 0, AreaRuntime.Blocked => 1, AreaRuntime.Incomplete => 2, _ => 3 })
            .ThenByDescending(r => r.At);
}
