using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BirkNext.IntegrationTopology;
using BirkNext.LocalHttpsProxy;
using BirkNext.Web.Configuration;
using BirkNext.Web.Models;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

namespace BirkNext.Web.Services;

/// <summary>Integration Topology calls: the canonical topology (optionally with this browser's runtime observations merged), explicit discovery and a profile's expected paths.</summary>
public interface IIntegrationTopologyApiService
{
    Task<(IntegrationTopologyView? View, string? Error)> GetAsync(string environmentId, IReadOnlyList<TopologyObservation> observations, CancellationToken ct = default);
    Task<(IntegrationTopologyView? View, string? Error)> DiscoverAsync(string environmentId, IReadOnlyList<TopologyObservation> observations, CancellationToken ct = default);
    Task<(IReadOnlyList<TopologyPathResult>? Paths, string? Error)> PathsAsync(string environmentId, string profileId, IReadOnlyList<TopologyObservation> observations, CancellationToken ct = default);
    /// <summary>A profile's topology source coverage and expected relationships (read-only).</summary>
    Task<(TopologyCoverageView? Coverage, string? Error)> CoverageAsync(string environmentId, string profileId, IReadOnlyList<TopologyObservation> observations, CancellationToken ct = default) =>
        Task.FromResult<(TopologyCoverageView?, string?)>((null, "Topology coverage is not available in this host."));
}

public sealed class IntegrationTopologyApiService(HttpClient http) : IIntegrationTopologyApiService
{
    public const string Title = "Integration Topology";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>A registered client wins; otherwise one for the single configured backend address (the BackendUrl rule every client uses).</summary>
    public static IIntegrationTopologyApiService Resolve(IServiceProvider services)
    {
        if (services.GetService(typeof(IIntegrationTopologyApiService)) is IIntegrationTopologyApiService registered) return registered;
        if (services.GetService(typeof(IConfiguration)) is not IConfiguration configuration || services.GetService(typeof(IWebAssemblyHostEnvironment)) is not IWebAssemblyHostEnvironment environment
            || services.GetService(typeof(IHttpClientFactory)) is not IHttpClientFactory factory)
            return new Unavailable();
        var backendUrl = configuration["BackendUrl"] ?? "https://localhost:5000";
        BackendUrlValidator.Validate(backendUrl, environment.Environment);
        var client = factory.CreateClient(nameof(IntegrationTopologyApiService));
        client.BaseAddress = BackendUrlValidator.BaseAddress(backendUrl);
        return new IntegrationTopologyApiService(client);
    }

    /// <summary>A host without a backend client (for example a component test that registers none): every call says so, nothing is guessed.</summary>
    private sealed class Unavailable : IIntegrationTopologyApiService
    {
        private const string Message = "Integration Topology is not available in this host.";
        public Task<(IntegrationTopologyView? View, string? Error)> GetAsync(string environmentId, IReadOnlyList<TopologyObservation> observations, CancellationToken ct = default) => Task.FromResult<(IntegrationTopologyView?, string?)>((null, Message));
        public Task<(IntegrationTopologyView? View, string? Error)> DiscoverAsync(string environmentId, IReadOnlyList<TopologyObservation> observations, CancellationToken ct = default) => Task.FromResult<(IntegrationTopologyView?, string?)>((null, Message));
        public Task<(IReadOnlyList<TopologyPathResult>? Paths, string? Error)> PathsAsync(string environmentId, string profileId, IReadOnlyList<TopologyObservation> observations, CancellationToken ct = default) => Task.FromResult<(IReadOnlyList<TopologyPathResult>?, string?)>((null, Message));
        public Task<(TopologyCoverageView? Coverage, string? Error)> CoverageAsync(string environmentId, string profileId, IReadOnlyList<TopologyObservation> observations, CancellationToken ct = default) => Task.FromResult<(TopologyCoverageView?, string?)>((null, Message));
    }

    public Task<(IntegrationTopologyView? View, string? Error)> GetAsync(string environmentId, IReadOnlyList<TopologyObservation> observations, CancellationToken ct = default) =>
        PostAsync<IntegrationTopologyView>($"api/integration-topology/{Uri.EscapeDataString(environmentId)}", observations, ct);

    public Task<(IntegrationTopologyView? View, string? Error)> DiscoverAsync(string environmentId, IReadOnlyList<TopologyObservation> observations, CancellationToken ct = default) =>
        PostAsync<IntegrationTopologyView>($"api/integration-topology/{Uri.EscapeDataString(environmentId)}/discover", observations, ct);

    public async Task<(IReadOnlyList<TopologyPathResult>? Paths, string? Error)> PathsAsync(string environmentId, string profileId, IReadOnlyList<TopologyObservation> observations, CancellationToken ct = default)
    {
        var (paths, error) = await PostAsync<List<TopologyPathResult>>($"api/integration-topology/{Uri.EscapeDataString(environmentId)}/profiles/{Uri.EscapeDataString(profileId)}/paths", observations, ct);
        return (paths, error);
    }

    public Task<(TopologyCoverageView? Coverage, string? Error)> CoverageAsync(string environmentId, string profileId, IReadOnlyList<TopologyObservation> observations, CancellationToken ct = default) =>
        PostAsync<TopologyCoverageView>($"api/integration-topology/{Uri.EscapeDataString(environmentId)}/profiles/{Uri.EscapeDataString(profileId)}/coverage", observations, ct);

    private async Task<(T? Value, string? Error)> PostAsync<T>(string path, IReadOnlyList<TopologyObservation> observations, CancellationToken ct) where T : class
    {
        try
        {
            using var response = await http.PostAsJsonAsync(path, new TopologyRequest([.. observations]), Json, ct);
            if (response.IsSuccessStatusCode) return (await response.Content.ReadFromJsonAsync<T>(Json, ct), null);
            return (null, response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                ? "Discovering interfaces needs the integration-configuration permission (or local trusted execution)."
                : $"{Title}: the backend answered HTTP {(int)response.StatusCode}.");
        }
        catch (Exception e) when (BackendRequestClassifier.IsRequestFailure(e))
        { return (null, BackendRequestClassifier.FromException(Title, e, "POST api/integration-topology", ct).UserMessage); }
    }
}

/// <summary>Turns this browser's Endpoint Discovery evidence into topology observations: method, host and path of REST/GraphQL calls only.</summary>
public static class TopologyObservations
{
    public static List<TopologyObservation> From(EndpointDiscoverySnapshot snapshot) =>
        [.. snapshot.Pages.SelectMany(page => page.Endpoints).Concat(snapshot.Shared)
            .Where(item => item.Category is ObservedTrafficCategory.Rest or ObservedTrafficCategory.GraphQl && item.Host.Length > 0 && item.Path.Length > 0)
            .GroupBy(item => (item.Category, item.Method, item.Host, item.Path, item.OperationName))
            .Select(group => new TopologyObservation(group.Key.Category.ToString(), group.Key.Method, group.Key.Host, group.Key.Path.Split('?', '#')[0], group.Key.OperationName,
                group.Max(item => item.LastObservedAt)))
            .Take(2000)];
}
