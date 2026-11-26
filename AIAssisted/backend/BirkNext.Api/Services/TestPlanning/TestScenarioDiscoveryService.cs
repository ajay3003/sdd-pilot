using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using BirkNext.Api.Services.CriticalE2E.Suggestions;
using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.Api.Services.IntegrationJourneys;
using BirkNext.Api.Services.SystemUnderstanding;
using BirkNext.Integrations;
using BirkNext.TestCoverage;
using BirkNext.TestPlanning;
using D = BirkNext.Api.Services.TestPlanning.TestScenarioDiscoveryEngine;
using E = BirkNext.Api.Services.TestPlanning.TestPlanSuggestionEngine;

namespace BirkNext.Api.Services.TestPlanning;

/// <summary>What one discovery run reads: the project, its source snapshot (null when none is linked) and the workspace documents the browser owns.</summary>
public sealed record ScenarioDiscoveryContext(string ProjectKey, string? ProjectName, IqrSourceSnapshot? Snapshot, CoverageWorkspaceEvidence? Workspace, string? EnvironmentId);

/// <summary>One registered discovery capability backed by an existing analyzer. A provider reports only what its analyzer supports.</summary>
public interface ITestScenarioCandidateProvider
{
    string CapabilityId { get; }
    Task<CandidateProviderOutput> DiscoverAsync(ScenarioDiscoveryContext context, CancellationToken ct);
}

/// <summary>Specification acceptance scenarios and requirements (parsed in the browser by the existing specification parser).</summary>
public sealed class SpecificationScenarioProvider : ITestScenarioCandidateProvider
{
    public string CapabilityId => D.Specification;
    public Task<CandidateProviderOutput> DiscoverAsync(ScenarioDiscoveryContext context, CancellationToken ct) => Task.FromResult(D.FromSpecification(context.ProjectKey, context.Workspace));
}

/// <summary>System Understanding guide scenarios — only for a guide whose source roles recognise this project's application source.</summary>
public sealed class SystemGuideScenarioProvider(SystemGuideService guides) : ITestScenarioCandidateProvider
{
    public string CapabilityId => D.SystemGuide;
    public async Task<CandidateProviderOutput> DiscoverAsync(ScenarioDiscoveryContext context, CancellationToken ct)
    {
        if (context.Snapshot is null)
            return new(D.Capability(D.SystemGuide, DiscoveryCapabilityState.NotApplicable, 0, "No source snapshot is linked to this project, so no guide can be matched to it.", "System Understanding"), []);
        var recognised = await guides.RecognisedGuideRolesAsync([context.Snapshot], ct, applicationSourceOnly: true);
        if (recognised.Count == 0)
            return new(D.Capability(D.SystemGuide, DiscoveryCapabilityState.NotApplicable, 0, "No System Understanding guide describes this project's source.", "System Understanding"), []);
        // Only the scenarios whose evidence lies in the part of the system this project's source is (see D.InProjectSource).
        var list = recognised.SelectMany(g => D.FromGuide(context.ProjectKey, g.Shown, g.Roles)).ToList();
        var steps = list.Count(c => c.CanonicalKey.Contains("/step/", StringComparison.Ordinal));
        return new(D.Capability(D.SystemGuide, list.Count == 0 ? DiscoveryCapabilityState.NoEvidence : DiscoveryCapabilityState.Available, list.Count,
            string.Join("; ", recognised.Select(g => Detail(g.Shown, g.Roles, g.RoleLabels)))
            + (steps == 0 ? "" : $". Plus {steps} step check{(steps == 1 ? "" : "s")} for the guide steps in this project's own source that no scenario covers"), "System Understanding"), list);
    }

    /// <summary>"Guide: 4 of 21 scenarios apply to this project's source (MU); 17 are evidenced only in M2LB and are not offered (revision 1, Draft)".</summary>
    internal static string Detail(BirkNext.SystemUnderstanding.SystemGuideRevision g, IReadOnlySet<string> roles, IReadOnlyDictionary<string, string> labels)
    {
        var all = g.Content.Scenarios;
        var outside = all.Where(s => !D.InProjectSource(g.Content, s, roles)).ToList();
        string Names(IEnumerable<string> keys) => string.Join(", ", keys.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(k => labels.GetValueOrDefault(k, k)));
        if (outside.Count == 0) return $"{g.Content.Title}: {all.Count} scenarios (revision {g.Revision}, {g.State})";
        var elsewhere = outside.SelectMany(s => D.ScenarioRoles(g.Content, s)).Where(r => !roles.Contains(r));
        return $"{g.Content.Title}: {all.Count - outside.Count} of {all.Count} scenarios apply to this project's source ({Names(roles)}); {outside.Count} "
            + $"{(outside.Count == 1 ? "is" : "are")} evidenced only in {Names(elsewhere)} and {(outside.Count == 1 ? "is" : "are")} not offered here (revision {g.Revision}, {g.State})";
    }
}

/// <summary>UI functional candidates from the existing frontend UI analyzer (Razor / HTML) via the Critical E2E suggestion engine.</summary>
public sealed class UiSourceScenarioProvider : ITestScenarioCandidateProvider
{
    public string CapabilityId => D.UiSource;
    public Task<CandidateProviderOutput> DiscoverAsync(ScenarioDiscoveryContext context, CancellationToken ct)
    {
        const string engine = "UI suggestion engine " + SuggestionEngine.RuleVersion;
        if (context.Snapshot is null)
            return Task.FromResult(new CandidateProviderOutput(D.Capability(D.UiSource, DiscoveryCapabilityState.NotApplicable, 0, "No source snapshot is linked to this project.", engine), []));
        var surface = context.Snapshot.UiSurface;
        if (surface is null || surface.Pages.Count == 0)
            return Task.FromResult(new CandidateProviderOutput(D.Capability(D.UiSource, DiscoveryCapabilityState.NoEvidence, 0,
                surface is null ? "The snapshot has no UI analysis (re-analyze the source to read pages)." : "No Razor or HTML page was found in the source.", engine), []));
        // A manual tester tests the application's own pages: framework pages (error, not found, sign-in callback) and pages of developer/demo
        // tools in the same repository are left out, and the detail says how many, so nothing disappears silently.
        var framework = surface.Pages.Where(D.IsFrameworkPage).ToList();
        var developer = surface.Pages.Where(p => !D.IsFrameworkPage(p) && D.IsDeveloperToolPage(p)).ToList();
        var offered = surface with { Pages = [.. surface.Pages.Except(framework).Except(developer)] };
        var list = D.FromUi(context.ProjectKey, SuggestionEngine.Generate(context.ProjectKey, context.Snapshot.Id, offered, []));
        var left = new List<string>();
        if (developer.Count > 0) left.Add($"{developer.Count} page{(developer.Count == 1 ? "" : "s")} of developer or demo tools ({string.Join(", ", developer.Select(p => p.Module).Where(m => m.Length > 0).Distinct(StringComparer.Ordinal).Take(3))})");
        if (framework.Count(p => p.Routes.Count > 0) is var routed and > 0) left.Add($"{routed} framework page{(routed == 1 ? "" : "s")} (error, not found, sign-in)");
        var detail = $"{surface.Pages.Count} pages analyzed; {offered.Pages.Count(p => p.Routes.Count > 0)} application page{(offered.Pages.Count(p => p.Routes.Count > 0) == 1 ? "" : "s")} with a route."
            + (left.Count == 0 ? "" : $" Not offered for manual testing: {string.Join("; ", left)}.");
        return Task.FromResult(new CandidateProviderOutput(D.Capability(D.UiSource, list.Count == 0 ? DiscoveryCapabilityState.NoEvidence : DiscoveryCapabilityState.Available, list.Count,
            list.Count == 0 && developer.Count > 0 && offered.Pages.All(p => p.Routes.Count == 0)
                ? $"No application page to test: the only pages with a route are framework pages or belong to developer/demo tools. {detail}" : detail, engine,
            ["Only pages with a parameterless @page route produce candidates.", "React and Angular templates are not analyzed."]), list));
    }
}

/// <summary>API / contract candidates from the OpenAPI and GraphQL contracts Source Analysis recorded.</summary>
public sealed class ApiContractScenarioProvider : ITestScenarioCandidateProvider
{
    public string CapabilityId => D.ApiContract;
    public Task<CandidateProviderOutput> DiscoverAsync(ScenarioDiscoveryContext context, CancellationToken ct)
    {
        if (context.Snapshot is null)
            return Task.FromResult(new CandidateProviderOutput(D.Capability(D.ApiContract, DiscoveryCapabilityState.NotApplicable, 0, "No source snapshot is linked to this project.", "Contract analyzer"), []));
        var contracts = (context.Snapshot.EvidenceDomains?.Contracts.Contracts ?? []).Where(c => c.Type is BirkNext.SourceDomains.SourceContractType.OpenApi
            or BirkNext.SourceDomains.SourceContractType.GraphQlSchema or BirkNext.SourceDomains.SourceContractType.GraphQlOperations).ToList();
        var list = D.FromContracts(context.ProjectKey, contracts).Take(600).ToList();
        return Task.FromResult(new CandidateProviderOutput(D.Capability(D.ApiContract, list.Count == 0 ? DiscoveryCapabilityState.NoEvidence : DiscoveryCapabilityState.Available, list.Count,
            contracts.Count == 0 ? "No OpenAPI or GraphQL contract was found in the source." : $"{contracts.Sum(c => c.Operations.Count)} operations in {contracts.Count} contract(s).", "Contract analyzer",
            contracts.SelectMany(c => c.Limitations)), list));
    }
}

/// <summary>Integration journeys declared for this project, and test-pack journeys whose sources are this project's application source.</summary>
public sealed class IntegrationJourneyScenarioProvider(DeclaredIntegrationJourneyStore? declared = null, IIntegrationJourneyPackRegistry? packs = null,
    IntegrationJourneyRunStore? runs = null) : ITestScenarioCandidateProvider
{
    public string CapabilityId => D.IntegrationJourneys;
    public async Task<CandidateProviderOutput> DiscoverAsync(ScenarioDiscoveryContext context, CancellationToken ct)
    {
        var mine = declared is null ? [] : await declared.ForProjectAsync(context.ProjectKey, ct);
        var recognisedPacks = context.Snapshot is null || packs is null ? [] : packs.Packs
            .Where(p => p.SourceRoles.Where(r => !r.RequiresInfrastructureAsCode).Any(r => TestPackSources.Recognizes(r, context.Snapshot!))).ToList();
        // A pack recognised through one of its sources offers the journeys that source takes part in (an MU source never offers M2LB-only journeys);
        // a recognised role that names no journeys keeps all of them.
        IReadOnlySet<string>? JourneysOf(IIntegrationJourneyPack pack)
        {
            var roles = pack.SourceRoles.Where(r => !r.RequiresInfrastructureAsCode && TestPackSources.Recognizes(r, context.Snapshot!)).ToList();
            return roles.Any(r => r.Journeys.Count == 0) ? null : roles.SelectMany(r => r.Journeys).ToHashSet(StringComparer.Ordinal);
        }
        var list = D.FromDeclaredJourneys(context.ProjectKey, mine).ToList();
        foreach (var pack in recognisedPacks)
        {
            // Cross-source: the pack's own run history in the active Target Environment (one bounded read per pack, never per scenario).
            IReadOnlyList<IntegrationJourneyRun>? history = null;
            if (runs is not null && context.EnvironmentId is { Length: > 0 } environment)
                history = await runs.ListAsync(new IntegrationJourneyHistoryQuery(environment, PackId: pack.PackId), 500, ct);
            list.AddRange(D.WithRunEvidence(D.FromPackJourneys(context.ProjectKey, pack, JourneysOf(pack)), pack, context.EnvironmentId, null, history));
        }
        var detail = $"{mine.Count} declared for this project" + (recognisedPacks.Count == 0 ? "" : $"; test pack(s) {string.Join(", ", recognisedPacks.Select(p => p.DisplayName))} recognise this source");
        return new(D.Capability(D.IntegrationJourneys, list.Count == 0 ? DiscoveryCapabilityState.NoEvidence : DiscoveryCapabilityState.Available, list.Count,
            list.Count == 0 ? "No integration journey is declared for this project (Integrations › journeys) and no test pack recognises its source." : detail, "Integration journeys"), list);
    }
}

/// <summary>The xUnit test inventory of the snapshot: associations by explicit requirement reference only (no candidates of its own).</summary>
public sealed class ExistingTestScenarioProvider : ITestScenarioCandidateProvider
{
    public string CapabilityId => D.ExistingTests;
    public Task<CandidateProviderOutput> DiscoverAsync(ScenarioDiscoveryContext context, CancellationToken ct)
    {
        var inventory = context.Snapshot?.TestInventory;
        if (inventory is null || inventory.Definitions.Count == 0)
            return Task.FromResult(new CandidateProviderOutput(D.Capability(D.ExistingTests, context.Snapshot is null ? DiscoveryCapabilityState.NotApplicable : DiscoveryCapabilityState.NoEvidence, 0,
                context.Snapshot is null ? "No source snapshot is linked to this project." : "No xUnit test was discovered in the source.", "xUnit test discovery",
                inventory?.Limitations), []));
        var links = D.TestLinks(inventory);
        var scenarioLinks = D.ScenarioLinks(inventory);
        if (context.Workspace is { Requirements.Count: 0 } && scenarioLinks.Count == 0)
            return Task.FromResult(new CandidateProviderOutput(D.Capability(D.ExistingTests, DiscoveryCapabilityState.NotApplicable, 0,
                $"{inventory.Definitions.Count} automated developer tests found. They are not manual test cases; they are shown on a scenario only when "
                + "the project's specification has a requirement they name, and this project has no specification requirements.", "xUnit test discovery"), [], links));
        var older = inventory.ScenarioReferenceRules == 0
            ? new[] { "These tests were discovered before BirkNext read scenario references; import the source again to see which tests name a scenario." } : [];
        return Task.FromResult(new CandidateProviderOutput(D.Capability(D.ExistingTests, DiscoveryCapabilityState.Available, 0,
            $"{inventory.Definitions.Count} tests discovered; {links.Select(l => l.Test.TestId).Distinct().Count()} reference a requirement explicitly, "
            + $"{scenarioLinks.Select(l => l.Test.TestId).Distinct().Count()} name an acceptance scenario. Shown on the scenarios they name (and, as supporting evidence, on the ones sharing a requirement).",
            "xUnit test discovery", ["Only xUnit [Fact]/[Theory] are discovered; NUnit, MSTest, Jest and others are not listed individually.", .. older, .. inventory.Limitations]), [], links, scenarioLinks));
    }
}

/// <summary>
/// Discovery results kept in memory so navigating back never re-runs the analyzers. Bounded; a restart restores from <see cref="ScenarioDiscoveryStore"/>.
/// Two kinds of pointer (2026-11-23): the <b>current</b> result of a project and of its application (only results whose basis is the analysis
/// baseline — <see cref="ScenarioDiscoveryResult.IsCurrent"/>), and the result per project + snapshot (any kind, for looking at a version again).
/// A historical inspection never moves a current pointer, so plans and Refresh never compare against an older version by accident.
/// </summary>
public sealed class ScenarioDiscoveryCache
{
    private const int Capacity = 64;
    private readonly ConcurrentDictionary<string, ScenarioDiscoveryResult> _byId = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _latest = new(StringComparer.Ordinal);

    private static string App(string applicationKey) => "@app|" + applicationKey;

    public void Store(ScenarioDiscoveryResult result)
    {
        _byId[result.DiscoveryId] = result;
        _latest[$"{result.ProjectKey}|{result.SnapshotKey}"] = result.DiscoveryId;
        RememberScoped(result);
        if (result.IsCurrent)
        {
            _latest[result.ProjectKey] = result.DiscoveryId;
            if (Newer(App(result.ApplicationKey), result)) _latest[App(result.ApplicationKey)] = result.DiscoveryId;
        }
        if (_byId.Count <= Capacity) return;
        var keep = _latest.Values.ToHashSet(StringComparer.Ordinal);
        foreach (var old in _byId.Values.Where(r => !keep.Contains(r.DiscoveryId)).OrderBy(r => r.DiscoveredAt).Take(_byId.Count - Capacity)) _byId.TryRemove(old.DiscoveryId, out _);
    }

    private bool Newer(string key, ScenarioDiscoveryResult result) =>
        !_latest.TryGetValue(key, out var id) || !_byId.TryGetValue(id, out var held) || held.DiscoveredAt <= result.DiscoveredAt;

    private readonly ConcurrentDictionary<string, bool> _applicationsRestored = new(StringComparer.Ordinal);

    /// <summary>True the first time an application's stored results are restored in this process (later calls skip the read).</summary>
    public bool FirstApplicationRestore(string applicationKey) => _applicationsRestored.TryAdd(applicationKey, true);

    private static string Scoped(string projectKey, string scope) => $"{projectKey}|@scope|{scope}";

    /// <summary>Holds a result as the newest for its selection scope (module + specification) without making it the project's latest.</summary>
    public void RememberScoped(ScenarioDiscoveryResult result)
    {
        if (result.SelectionScope is not { } scope || !result.IsCurrent) return;
        _byId[result.DiscoveryId] = result;
        var key = Scoped(result.ProjectKey, scope);
        if (Newer(key, result)) _latest[key] = result.DiscoveryId;
    }

    /// <summary>The newest current result computed for exactly this selection scope; null when none (never another module's or specification's).</summary>
    public ScenarioDiscoveryResult? LatestForScope(string projectKey, string scope) =>
        _latest.TryGetValue(Scoped(projectKey, scope), out var id) && _byId.TryGetValue(id, out var r) && r.ProjectKey == projectKey && r.SelectionScope == scope ? r : null;

    /// <summary>The project's current results held in the cache (every selection scope), newest first — to compare a saved case with a discovery
    /// of its own specification when another specification is selected now.</summary>
    public IReadOnlyList<ScenarioDiscoveryResult> CurrentResults(string projectKey) =>
        [.. _byId.Values.Where(r => r.IsCurrent && string.Equals(r.ProjectKey, projectKey, StringComparison.Ordinal)).OrderByDescending(r => r.DiscoveredAt)];

    private readonly ConcurrentDictionary<string, bool> _scopesRestored = new(StringComparer.Ordinal);

    /// <summary>True the first time a project's stored per-scope results are restored in this process.</summary>
    public bool FirstScopedRestore(string projectKey) => _scopesRestored.TryAdd(projectKey, true);

    /// <summary>Holds a result (read back from storage) for lookup by id without making it the project's latest.</summary>
    public void Remember(ScenarioDiscoveryResult result) => _byId.TryAdd(result.DiscoveryId, result);

    /// <summary>A result by id, only for the project it belongs to (never another project's).</summary>
    public ScenarioDiscoveryResult? Find(string projectKey, string discoveryId) =>
        _byId.TryGetValue(discoveryId, out var r) && string.Equals(r.ProjectKey, projectKey, StringComparison.Ordinal) ? r : null;

    /// <summary>The project's current result (no snapshot), or its result for one snapshot (any kind).</summary>
    public ScenarioDiscoveryResult? Latest(string projectKey, string? snapshotKey = null) =>
        _latest.TryGetValue(snapshotKey is null ? projectKey : $"{projectKey}|{snapshotKey}", out var id) && _byId.TryGetValue(id, out var r) && r.ProjectKey == projectKey ? r : null;

    /// <summary>The newest current result of an application (over every import of it).</summary>
    public ScenarioDiscoveryResult? CurrentForApplication(string applicationKey) =>
        _latest.TryGetValue(App(applicationKey), out var id) && _byId.TryGetValue(id, out var r) && r.IsCurrent ? r : null;
}

/// <summary>
/// Orchestrates the registered discovery capabilities for one project over one consistent source basis (<see cref="ScenarioSourceBasisResolver"/>):
/// every source of the basis is read in the version the analysis baseline names (each source-based provider runs once per source), the
/// specification comes from the import of that version, a failing provider is reported and never hides the others, candidates are normalised,
/// consolidated (exact duplicates only) and checked for conflicting expected results, and the result is remembered. Runs only when asked.
/// </summary>
public sealed partial class TestScenarioDiscoveryService(IqrSourceStore sources, IEnumerable<ITestScenarioCandidateProvider> providers, ScenarioDiscoveryCache cache,
    TimeProvider? clock = null, ILogger<TestScenarioDiscoveryService>? logger = null, ScenarioDiscoveryStore? store = null, ScenarioSourceBasisResolver? basisResolver = null)
{
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._:\-]{0,199}$")]
    private static partial Regex ValidKey();

    public static string Key(string? projectKey) => projectKey is { } k && ValidKey().IsMatch(k.Trim()) ? k.Trim()
        : throw new TestPlanException(400, "project-required", "Select a project first: discovery always runs for one project.");

    public async Task<ScenarioDiscoveryResult> DiscoverAsync(ScenarioDiscoveryRequest request, CancellationToken ct)
    {
        var projectKey = Key(request.ProjectKey);
        var limitations = new List<string>();
        IReadOnlyList<IqrSourceSnapshot> snapshots;
        IReadOnlyList<CoverageWorkspaceEvidence?> specifications;
        DiscoverySourceBasis? basis = null;
        if (basisResolver is not null)
        {
            var resolved = await basisResolver.ResolveAsync(projectKey, request.SnapshotId, request.MemberSnapshotIds, request.Inspect, request.Workspace, request.UseWorkingSpecification, ct);
            basis = resolved.Basis;
            snapshots = resolved.Snapshots;
            specifications = resolved.Specifications.Count > 0 ? [.. resolved.Specifications] : [null];
            limitations.AddRange(resolved.Limitations);
            limitations.AddRange(resolved.Basis.Problems);
            if (resolved.Basis.Members.Count == 0) limitations.Add("No source snapshot is linked to this project: only workspace documents were read.");
        }
        else
        {
            // Without the resolver (focused unit tests): the one requested snapshot and the browser's documents, as before.
            IqrSourceSnapshot? snapshot = null;
            if (request.SnapshotId is { } id)
            {
                snapshot = await sources.FindSourceAnalysisAsync(id, ct);
                if (snapshot is null) limitations.Add("The project's source snapshot was not found (removed or reset); source-based capabilities were skipped.");
            }
            else limitations.Add("No source snapshot is linked to this project: only workspace documents were read.");
            snapshots = snapshot is null ? [] : [snapshot];
            specifications = [request.Workspace];
        }

        var primary = snapshots.FirstOrDefault();
        var name = request.ProjectName?.Trim();
        var capabilities = new List<DiscoveryCapability>();
        var candidates = new List<DiscoveredScenario>();
        var links = new List<RequirementTestLink>();
        var scenarioLinks = new List<ScenarioTestLink>();
        foreach (var provider in providers)
        {
            // The specification provider reads each specification of the basis; every other provider reads each source of the basis (or none).
            List<ScenarioDiscoveryContext> contexts = provider.CapabilityId == D.Specification
                ? [.. specifications.Select(spec => new ScenarioDiscoveryContext(projectKey, name, primary, spec, request.EnvironmentId))]
                : snapshots.Count == 0 ? [new ScenarioDiscoveryContext(projectKey, name, null, specifications[0], request.EnvironmentId)]
                : [.. snapshots.Select(s => new ScenarioDiscoveryContext(projectKey, name, s, specifications[0], request.EnvironmentId))];
            var outputs = new List<DiscoveryCapability>();
            foreach (var context in contexts)
            {
                try
                {
                    var output = await provider.DiscoverAsync(context, ct);
                    outputs.Add(output.Capability);
                    var origin = provider.CapabilityId == D.Specification ? basis?.SpecificationReference ?? "" : context.Snapshot?.Id.ToString() ?? "";
                    candidates.AddRange(output.Candidates.Select(c => c.SnapshotKey.Length > 0 ? c : c with { SnapshotKey = origin }));
                    if (output.TestLinks is { } l) links.AddRange(l);
                    if (output.ScenarioLinks is { } sl) scenarioLinks.AddRange(sl);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    logger?.LogWarning(e, "Test scenario discovery capability {Capability} failed for project {Project}", provider.CapabilityId, projectKey);
                    outputs.Add(D.Capability(provider.CapabilityId, DiscoveryCapabilityState.Failed, 0, "The analyzer could not read this project's evidence. Other capabilities are unaffected.", provider.CapabilityId));
                }
            }
            capabilities.Add(D.MergeCapabilities(outputs));
        }
        var snapshotKey = primary?.Id.ToString() ?? "";
        var final = D.Finalize(projectKey, snapshotKey, candidates, links, scenarioLinks);
        if (candidates.Count > D.MaxCandidates && D.DroppedByCap(projectKey, candidates) is { Count: > 0 } dropped)
            limitations.Add($"Only {D.MaxCandidates} candidates are kept (specification and documented tests first); not kept: "
                + string.Join(", ", dropped.OrderBy(d => D.CapPriority(d.Key)).Select(d => $"{d.Value} {d.Key}")) + ".");
        var fingerprint = E.Sha(string.Join("\n", final.Select(c => c.CandidateId + ":" + c.Fingerprint)));
        // The selection it was computed for is part of its identity: two modules with the same scenarios are still two results.
        var selectionScope = DiscoveryScope.Of(request.Workspace);
        var result = new ScenarioDiscoveryResult
        {
            DiscoveryId = E.Sha($"{projectKey}|{selectionScope}|{basis?.Fingerprint ?? snapshotKey}|{fingerprint}|{D.RuleVersion}")[..24], ProjectKey = projectKey, ProjectName = name,
            SelectionScope = selectionScope,
            SnapshotKey = snapshotKey,
            TestsReadForScenarios = snapshots.Any(s => s.TestInventory is { ScenarioReferenceRules: >= 1, Definitions.Count: > 0 }),
            SnapshotLabel = primary is null ? null : $"{primary.Archive.FileName} · {primary.AnalyzedAt:yyyy-MM-dd HH:mm}",
            RuleVersion = D.RuleVersion, DiscoveredAt = (clock ?? TimeProvider.System).GetUtcNow(),
            Capabilities = [.. capabilities.Select(c => c.State == DiscoveryCapabilityState.Available ? c with { Candidates = final.Count(x => x.CapabilityId == c.Id) } : c)],
            Candidates = final, Limitations = [.. limitations.Distinct(StringComparer.Ordinal)], Basis = basis,
        };
        cache.Store(result);
        if (store is not null)
        {
            try { await store.SaveAsync(result, ct); }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger?.LogWarning(e, "The discovery result of project {Project} could not be stored; it is kept in memory only", projectKey);
                result = result with { Limitations = [.. result.Limitations, "This result could not be stored: it is lost when BirkNext restarts."] };
                cache.Store(result);
            }
        }
        return result;
    }

    /// <summary>The last result for the project (and snapshot, when given); null when discovery has not run since BirkNext started.</summary>
    public ScenarioDiscoveryResult? Last(string projectKey, Guid? snapshotId) => cache.Latest(Key(projectKey), snapshotId?.ToString());

    /// <summary>The last result for the project (and snapshot), restored from storage after a restart; null when the project was never discovered.
    /// Without a snapshot: the project's current result — a historical inspection is never returned as current.</summary>
    public async Task<ScenarioDiscoveryResult?> LastAsync(string projectKey, Guid? snapshotId, CancellationToken ct, string? scope = null)
    {
        var key = Key(projectKey);
        if (scope is not null)
        {
            // Only a result computed for this exact module + specification (2026-11-25): another selection's scenarios are never returned as current.
            var scoped = cache.LatestForScope(key, scope) ?? (store is not null ? await store.FindScopedAsync(key, scope, ct) : null);
            return scoped;
        }
        if (store is not null) await store.RestoreAsync(key, snapshotId?.ToString(), ct);
        return cache.Latest(key, snapshotId?.ToString());
    }
}
