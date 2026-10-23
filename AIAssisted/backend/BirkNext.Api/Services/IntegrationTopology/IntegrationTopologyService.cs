using BirkNext.Api.Services.ActiveEventTesting;
using BirkNext.Api.Services.ContractAnalysis;
using BirkNext.Api.Services.Integrations;
using BirkNext.Api.Services.SourceAnalysis;
using BirkNext.IntegrationTopology;

namespace BirkNext.Api.Services.IntegrationTopology;

/// <summary>
/// OpenAPI discovery, on explicit request only: for each trusted component that declares a relative OpenAPI path, one GET of that document on
/// the component's own host (never another host, never a probe list), bounded in time and size, read with the existing OpenAPI extractor.
/// Operations become Discovered REST interfaces. A failed read is reported, never guessed around.
/// </summary>
public sealed class OpenApiTopologyProvider(IHttpClientFactory http, IOpenApiExtractor extractor, TimeProvider clock) : ITopologyProvider
{
    public const string ClientName = "integration-topology";
    private const int MaxBytes = 2 * 1024 * 1024;
    public int Order => 40;

    public async Task ContributeAsync(TopologyContext context, TopologyBuilder builder, CancellationToken ct)
    {
        var declared = context.Trusted?.Components.Where(component => !string.IsNullOrWhiteSpace(component.OpenApiPath)).ToList() ?? [];
        if (!context.Request.DiscoverContracts || declared.Count == 0)
        {
            builder.Source(new(TopologySource.OpenApi, TopologySourceState.NotRequested, declared.Count == 0
                ? "No trusted component declares an OpenAPI document path."
                : $"{declared.Count} component(s) declare an OpenAPI document; select Discover OpenAPI interfaces to read them."));
            return;
        }
        var read = new List<string>();
        foreach (var component in declared)
        {
            var key = builder.Component(component.Name, TopologyStatus.Configured, new(TopologySource.TargetEnvironment, context.EnvironmentId, "Declares an OpenAPI document."));
            if (TrustedComponentPolicy.OpenApiAddress(component) is not { } address) { read.Add($"{component.Name}: the OpenAPI path is not a relative path on the component's own host"); continue; }
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                using var response = await http.CreateClient(ClientName).GetAsync(address, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if (!response.IsSuccessStatusCode) { read.Add($"{component.Name}: HTTP {(int)response.StatusCode}"); continue; }
                if (response.Content.Headers.ContentLength > MaxBytes) { read.Add($"{component.Name}: the document is larger than 2 MB"); continue; }
                var text = await response.Content.ReadAsStringAsync(timeout.Token);
                if (text.Length > MaxBytes) { read.Add($"{component.Name}: the document is larger than 2 MB"); continue; }
                var result = extractor.Extract(text);
                if (!result.IsSuccess || result.Contract is not { } contract) { read.Add($"{component.Name}: not a readable OpenAPI document"); continue; }
                var provenance = new TopologyProvenance(TopologySource.OpenApi, address.AbsolutePath, $"{contract.Operations.Count} operation(s) in the component's OpenAPI document", clock.GetUtcNow());
                foreach (var operation in contract.Operations)
                    builder.Interface(key, TopologyInterfaceType.Rest, "HTTP", TopologyDirection.Provides, operation.Path, operation.Method, TopologyStatus.Discovered, provenance,
                        string.IsNullOrWhiteSpace(operation.Id) ? null : $"openapi:{operation.Id}");
                read.Add($"{component.Name}: {contract.Operations.Count} operation(s)");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { read.Add($"{component.Name}: the document could not be reached"); }
        }
        builder.Source(new(TopologySource.OpenApi, TopologySourceState.Used, string.Join("; ", read), clock.GetUtcNow()));
    }
}

/// <summary>Domain profiles: their expected components and explicit aliases, so differently named sources merge — never an expected connection.</summary>
public sealed class ProfileTopologyProvider(IEnumerable<ITopologyProfile> profiles) : ITopologyProvider
{
    public int Order => 0;

    public Task ContributeAsync(TopologyContext context, TopologyBuilder builder, CancellationToken ct)
    {
        foreach (var profile in profiles)
            foreach (var component in profile.Components)
            {
                builder.Alias(component.Name, component.Aliases);
                builder.Component(component.Name, TopologyStatus.Expected, new(TopologySource.DomainProfile, profile.Id, component.Evidence));
            }
        return Task.CompletedTask;
    }
}

/// <summary>
/// The canonical topology of one environment: it reads the trusted environment, the integration catalog and the Source Analysis snapshots,
/// runs every provider in order, and merges their statements. Nothing is stored; every read rebuilds from the current sources (with the
/// sources' own timestamps), so a stale source is visible as such and a resolver improvement applies to existing snapshots without re-import.
/// </summary>
public sealed class IntegrationTopologyService(IEnumerable<ITopologyProvider> providers, IEnumerable<ITopologyProfile> profiles,
    ITrustedExecutionEnvironmentRegistry environments, IIntegrationCatalogService catalog, IReviewSourceEvidenceProvider sources, TimeProvider clock,
    BirkNext.Api.Services.Integrations.SourceEvidence.IIntegrationEvidenceBindingStore? bindings = null)
{
    public async Task<(IntegrationTopologyView View, TopologyBuilder Builder)> BuildAsync(string environmentId, TopologyRequest request, CancellationToken ct)
    {
        var (view, builder, _) = await BuildCoreAsync(environmentId, request, ct);
        return (view, builder);
    }

    private async Task<(IntegrationTopologyView View, TopologyBuilder Builder, TopologyContext Context)> BuildCoreAsync(string environmentId, TopologyRequest request, CancellationToken ct)
    {
        var trusted = environments.Resolve(environmentId, out var trustDetail);
        var configured = await catalog.GetAsync(environmentId, trusted?.EnvironmentType, trusted?.TargetUrl, ct);
        var snapshots = sources.SourceAnalysisEnabled ? await sources.ListAsync(environmentId, ct) : [];
        var context = new TopologyContext(environmentId, trusted, trustDetail, configured, snapshots, request);
        var builder = new TopologyBuilder();
        foreach (var provider in providers.OrderBy(item => item.Order)) await provider.ContributeAsync(context, builder, ct);
        var view = builder.Build(environmentId, trusted?.DisplayName, clock.GetUtcNow(), [.. profiles.Select(profile => profile.Id)])
            with { ResolverVersion = CrossSourceTopologyProvider.ResolverVersion, Discovery = DiscoveryPlan(trusted) };
        return (view, builder, context);
    }

    /// <summary>What "Discover OpenAPI interfaces" would read — computed from configuration only, no network call. Targets are deduplicated by
    /// component and canonical scheme, host, port and document path.</summary>
    public static TopologyDiscoveryPlan DiscoveryPlan(TrustedExecutionEnvironment? trusted)
    {
        var declared = trusted?.Components.Where(component => !string.IsNullOrWhiteSpace(component.OpenApiPath)).ToList() ?? [];
        var eligible = new List<TopologyDiscoveryTarget>();
        var ineligible = new List<TopologyDiscoveryTarget>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var component in declared)
        {
            if (TrustedComponentPolicy.OpenApiAddress(component) is { } address)
            {
                if (seen.Add($"{component.Name}|{address.Scheme}://{address.Authority}{address.AbsolutePath}")) eligible.Add(new(component.Name, address.Authority, address.AbsolutePath));
            }
            else ineligible.Add(new(component.Name, "", component.OpenApiPath ?? "", "The OpenAPI path is not a relative path on the component's own base address (same scheme, host and port)."));
        }
        return new(eligible, ineligible);
    }

    /// <summary>A profile's expected paths, resolved against the canonical topology (read-only).</summary>
    public async Task<IReadOnlyList<TopologyPathResult>?> PathsAsync(string environmentId, string profileId, TopologyRequest request, CancellationToken ct)
    {
        if (profiles.FirstOrDefault(item => string.Equals(item.Id, profileId, StringComparison.Ordinal)) is not { } profile) return null;
        var (view, builder) = await BuildAsync(environmentId, request, ct);
        return [.. profile.Paths.Select(path => TopologyPaths.Resolve(view, builder, profile.Id, path))];
    }

    /// <summary>A profile's source coverage and expected relationships, resolved against the canonical topology (read-only).</summary>
    public async Task<TopologyCoverageView?> CoverageAsync(string environmentId, string profileId, TopologyRequest request, CancellationToken ct)
    {
        if (profiles.FirstOrDefault(item => string.Equals(item.Id, profileId, StringComparison.Ordinal)) is not { } profile) return null;
        var (view, builder, context) = await BuildCoreAsync(environmentId, request, ct);
        // The canonical IQR bindings are read (never written): Bound comes from them only, never from a newest snapshot.
        var bound = bindings is null ? null : await bindings.ListAsync(environmentId, ct);
        return TopologyCoverage.Evaluate(profile, view, builder, context.Snapshots, request, clock.GetUtcNow(), bound);
    }
}

/// <summary>
/// How one component reaches another: a direct connection, or a chain through intermediate resource nodes (producer → topic → consumer,
/// application → captured table → connector). A chain is as certain as its weakest hop.
/// </summary>
public static class TopologyReach
{
    public sealed record Chain(List<TopologyConnection> Hops)
    {
        public TopologyRelationshipState State => Hops.Min(hop => hop.Relationship);
        public bool Conflict => Hops.Any(hop => hop.Status == TopologyStatus.Conflict);
    }

    /// <summary>One end of a reach query: specific components, or any resource of a kind ("servicebus", "eventhub", "connector", "table").</summary>
    public sealed record End(IReadOnlyCollection<string>? Components, string? ResourceKind)
    {
        public static End Of(string component) => new([component], null);
        public static End Kind(string kind) => new(null, kind);
        public bool Matches(string id) => Components?.Contains(id) == true || ResourceKind is { } kind && id.StartsWith(TopologyBuilder.ResourceKey(kind, ""), StringComparison.Ordinal);
    }

    /// <summary>Chains from → to that pass only through resource nodes (at most <paramref name="maxResources"/>). <paramref name="via"/> (when the
    /// start is a resource kind) keeps only resources that end feeds.</summary>
    public static List<Chain> Chains(IntegrationTopologyView view, End from, End to, End? via = null, int maxResources = 2)
    {
        var kinds = view.Components.ToDictionary(item => item.Id, item => item.Kind, StringComparer.Ordinal);
        bool IsResource(string id) => kinds.GetValueOrDefault(id) == TopologyComponentKind.Resource;
        var outgoing = view.Connections.GroupBy(item => item.ProducerComponentId).ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
        var starts = view.Components.Select(item => item.Id).Where(from.Matches).ToList();
        if (via is not null)
            starts = [.. starts.Where(start => view.Connections.Any(hop => hop.ConsumerComponentId == start && via.Matches(hop.ProducerComponentId)))];
        var chains = new List<Chain>();
        void Walk(string at, List<TopologyConnection> path, int resources)
        {
            foreach (var hop in outgoing.GetValueOrDefault(at) ?? [])
            {
                if (hop.ConsumerComponentId == path.FirstOrDefault()?.ProducerComponentId || path.Any(item => item.ConsumerComponentId == hop.ConsumerComponentId)) continue;
                var next = new List<TopologyConnection>(path) { hop };
                if (to.Matches(hop.ConsumerComponentId)) { chains.Add(new(next)); continue; }
                if (IsResource(hop.ConsumerComponentId) && resources < maxResources) Walk(hop.ConsumerComponentId, next, resources + 1);
            }
        }
        foreach (var start in starts) Walk(start, [], 0);
        return chains;
    }

    public static List<Chain> Chains(IntegrationTopologyView view, string from, string to) => Chains(view, End.Of(from), End.Of(to));

    public static Chain? Best(IEnumerable<Chain> chains) => chains.OrderBy(chain => chain.Conflict).ThenByDescending(chain => chain.State).ThenBy(chain => chain.Hops.Count).FirstOrDefault();
}

/// <summary>Resolves an expected path step by step: a step is resolved only by a connection some source states; expected alone never resolves it.</summary>
public static class TopologyPaths
{
    public static TopologyPathResult Resolve(IntegrationTopologyView view, TopologyBuilder builder, string profileId, TopologyExpectedPath path) =>
        new(profileId, path.Id, path.Label, [.. path.Steps.Select(step =>
        {
            var chains = TopologyReach.Chains(view, builder.ComponentKey(step.From), builder.ComponentKey(step.To));
            if (chains.Count == 0)
                return new TopologyPathStep(step.Label, step.From, step.To, TopologyPathState.Unresolved, TopologyStatus.NotEvaluated, false, null,
                    $"No source states a connection {step.From} → {step.To}. Expected only; not discovered or configured.");
            if (chains.Any(chain => chain.Conflict))
                return new TopologyPathStep(step.Label, step.From, step.To, TopologyPathState.Conflict, TopologyStatus.Conflict, false, chains[0].Hops[0].Id,
                    "Sources disagree about this connection; review it before relying on it.");
            var best = TopologyReach.Best(chains)!;
            var status = best.Hops.Min(hop => hop.Status);
            var observed = best.Hops.All(hop => hop.Status == TopologyStatus.Observed);
            return new TopologyPathStep(step.Label, step.From, step.To, TopologyPathState.Resolved, status, observed, best.Hops[0].Id,
                $"{best.Hops[0].Type} over {(best.Hops.Count > 1 ? string.Join(" → ", best.Hops.Take(best.Hops.Count - 1).Select(hop => view.Components.FirstOrDefault(c => c.Id == hop.ConsumerComponentId)?.Name ?? hop.ConsumerComponentId)) : best.Hops[0].Address ?? best.Hops[0].Protocol)}"
                + $" · {string.Join(", ", best.Hops.SelectMany(hop => hop.Provenance).Select(item => item.Source).Distinct())}{(observed ? "" : " · not observed at runtime")}");
        })]);
}
