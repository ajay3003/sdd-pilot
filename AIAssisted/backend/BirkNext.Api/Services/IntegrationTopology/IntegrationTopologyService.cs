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
                : $"{declared.Count} component(s) declare an OpenAPI document; select Discover interfaces to read them."));
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
/// sources' own timestamps), so a stale source is visible as such.
/// </summary>
public sealed class IntegrationTopologyService(IEnumerable<ITopologyProvider> providers, IEnumerable<ITopologyProfile> profiles,
    ITrustedExecutionEnvironmentRegistry environments, IIntegrationCatalogService catalog, IReviewSourceEvidenceProvider sources, TimeProvider clock)
{
    public async Task<(IntegrationTopologyView View, TopologyBuilder Builder)> BuildAsync(string environmentId, TopologyRequest request, CancellationToken ct)
    {
        var trusted = environments.Resolve(environmentId, out var trustDetail);
        var configured = await catalog.GetAsync(environmentId, trusted?.EnvironmentType, trusted?.TargetUrl, ct);
        var snapshots = sources.SourceAnalysisEnabled ? await sources.ListAsync(environmentId, ct) : [];
        var context = new TopologyContext(environmentId, trusted, trustDetail, configured, snapshots, request);
        var builder = new TopologyBuilder();
        foreach (var provider in providers.OrderBy(item => item.Order)) await provider.ContributeAsync(context, builder, ct);
        return (builder.Build(environmentId, trusted?.DisplayName, clock.GetUtcNow(), [.. profiles.Select(profile => profile.Id)]), builder);
    }

    /// <summary>A profile's expected paths, resolved against the canonical topology (read-only).</summary>
    public async Task<IReadOnlyList<TopologyPathResult>?> PathsAsync(string environmentId, string profileId, TopologyRequest request, CancellationToken ct)
    {
        if (profiles.FirstOrDefault(item => string.Equals(item.Id, profileId, StringComparison.Ordinal)) is not { } profile) return null;
        var (view, builder) = await BuildAsync(environmentId, request, ct);
        return [.. profile.Paths.Select(path => TopologyPaths.Resolve(view, builder, profile.Id, path))];
    }
}

/// <summary>Resolves an expected path step by step: a step is resolved only by a connection some source states; expected alone never resolves it.</summary>
public static class TopologyPaths
{
    public static TopologyPathResult Resolve(IntegrationTopologyView view, TopologyBuilder builder, string profileId, TopologyExpectedPath path) =>
        new(profileId, path.Id, path.Label, [.. path.Steps.Select(step =>
        {
            var from = builder.ComponentKey(step.From);
            var to = builder.ComponentKey(step.To);
            var connections = view.Connections.Where(item => item.ProducerComponentId == from && item.ConsumerComponentId == to).ToList();
            if (connections.Count == 0)
                return new TopologyPathStep(step.Label, step.From, step.To, TopologyPathState.Unresolved, TopologyStatus.NotEvaluated, false, null,
                    $"No source states a connection {step.From} → {step.To}. Expected only; not discovered or configured.");
            if (connections.Any(item => item.Status == TopologyStatus.Conflict))
                return new TopologyPathStep(step.Label, step.From, step.To, TopologyPathState.Conflict, TopologyStatus.Conflict, false, connections[0].Id,
                    "Sources disagree about this connection; review it before relying on it.");
            var best = connections.OrderByDescending(item => item.Status).First();
            var observed = connections.Any(item => item.Status == TopologyStatus.Observed);
            return new TopologyPathStep(step.Label, step.From, step.To, TopologyPathState.Resolved, best.Status, observed, best.Id,
                $"{best.Type} over {best.Address ?? best.Protocol} · {string.Join(", ", best.Provenance.Select(item => item.Source).Distinct())}{(observed ? "" : " · not observed at runtime")}");
        })]);
}
