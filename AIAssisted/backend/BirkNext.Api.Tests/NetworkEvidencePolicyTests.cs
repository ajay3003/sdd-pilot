using BirkNext.LocalHttpsProxy;
using BirkNext.Api.Services.LocalHttpsProxy;
using BirkNext.Api.Services.IntegrationQuality;
using System.Text.Json;

namespace BirkNext.Api.Tests;

public class NetworkEvidencePolicyTests
{
    [Fact]
    public async Task GraphQlAndOpenApiDiscoveryMarkEveryGeneratedRequest()
    {
        var handler = new ProbeHandler();
        using var client = new HttpClient(handler);
        var service = new BirkNext.Api.Services.WasmPerformance.WasmApiAnalysisService(client,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<BirkNext.Api.Services.WasmPerformance.WasmApiAnalysisService>.Instance);
        await service.AnalyzeAsync("https://app.test");
        Assert.Contains("/graphql", handler.Paths);
        Assert.Contains("/swagger.json", handler.Paths);
        Assert.All(handler.Markers, marker => Assert.Equal("DiscoveryProbe", marker));
    }

    private sealed class ProbeHandler : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];
        public List<string> Markers { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri!.AbsolutePath);
            Markers.Add(request.Headers.GetValues(NetworkEvidencePolicy.ProvenanceHeader).Single());
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound) { Content = new StringContent("missing") });
        }
    }

    [Theory]
    [InlineData("/", NetworkResourceKind.ApplicationPage)]
    [InlineData("/admin/operations", NetworkResourceKind.ApplicationPage)]
    [InlineData("/admin/general-roles", NetworkResourceKind.ApplicationPage)]
    [InlineData("/person", NetworkResourceKind.ApplicationPage)]
    [InlineData("/appsettings.json", NetworkResourceKind.ConfigurationResource)]
    [InlineData("/appsettings.Dev.json", NetworkResourceKind.ConfigurationResource)]
    [InlineData("/appsettings.Development.json", NetworkResourceKind.ConfigurationResource)]
    [InlineData("/appsettings.Production.json", NetworkResourceKind.ConfigurationResource)]
    [InlineData("/swagger.json", NetworkResourceKind.ApiDescriptionResource)]
    [InlineData("/openapi.json", NetworkResourceKind.ApiDescriptionResource)]
    [InlineData("/openapi/v3/swagger.json", NetworkResourceKind.ApiDescriptionResource)]
    [InlineData("/_framework/app.js", NetworkResourceKind.StaticAsset)]
    [InlineData("/_content/theme.css", NetworkResourceKind.StaticAsset)]
    [InlineData("/authentication/login-callback", NetworkResourceKind.AuthenticationCallback)]
    [InlineData("/birknext-unknown-route-probe-123", NetworkResourceKind.DiscoveryProbe)]
    public void ResourceIdentityIsIndependentOfResponseMime(string path, NetworkResourceKind expected) =>
        Assert.Equal(expected, NetworkEvidencePolicy.Classify(path, correlatedPage: true));

    [Theory]
    [InlineData("/graphql")]
    [InlineData("/v1/graphql")]
    [InlineData("/_graphql")]
    [InlineData("/graph")]
    [InlineData("/api/graphql")]
    public void GeneratedGraphQlNeverBecomesApplicationEvidence(string path)
    {
        var e = Endpoint(path) with { Category = ObservedTrafficCategory.GraphQl, Provenance = RequestProvenance.DiscoveryProbe, LastStatus = 405 };
        Assert.Equal(NetworkResourceKind.DiscoveryProbe, NetworkEvidencePolicy.ResourceOf(e));
        Assert.False(NetworkEvidencePolicy.IsApiCandidate(e));
        Assert.Equal("Proxy", NetworkEvidencePolicy.TransportLabel(e));
    }

    [Theory]
    [InlineData("/appsettings.json")]
    [InlineData("/_framework/app.js")]
    [InlineData("/swagger.json")]
    [InlineData("/authentication/login-callback")]
    [InlineData("/birknext-unknown-route-probe-123")]
    public void TechnicalResponsesCannotBecomeObservedIntegrations(string path)
    {
        var e = Endpoint(path);
        Assert.False(NetworkEvidencePolicy.IsApiCandidate(e));
    }

    [Fact]
    public void RealGraphQlIsEligibleAndSurvivesSerialization()
    {
        var e = Endpoint("/api/autorisasjon/graphql") with { Category = ObservedTrafficCategory.GraphQl };
        var restored = JsonSerializer.Deserialize<ObservedNetworkEndpoint>(JsonSerializer.Serialize(e))!;
        Assert.True(NetworkEvidencePolicy.IsApiCandidate(restored));
        Assert.Equal(RequestProvenance.ApplicationTraffic, restored.Provenance);
    }

    [Fact]
    public void HistoricalUrlAloneCannotProveWhoGeneratedGraphQl()
    {
        var old = Endpoint("/graphql") with { Provenance = RequestProvenance.Unknown, LastStatus = 404 };
        Assert.Equal(RequestProvenance.Unknown, NetworkEvidencePolicy.ProvenanceOf(old));
        Assert.False(NetworkEvidencePolicy.IsApiCandidate(old));
        Assert.Equal(RequestProvenance.DiscoveryProbe, NetworkEvidencePolicy.ProvenanceOf(old with { Path = "/birknext-unknown-route-probe-old" }));
    }

    [Fact]
    public void RegistryKeepsApplicationAndProbeCountsSeparate()
    {
        var registry = new ObservedNetworkRegistry();
        var app = Endpoint("/graphql");
        registry.Record(app);
        registry.Record(app with { Provenance = RequestProvenance.DiscoveryProbe });
        registry.Record(app);
        Assert.Equal(2, registry.Snapshot().Count);
        Assert.Equal(2, registry.Snapshot().Single(e => e.Provenance == RequestProvenance.ApplicationTraffic).Count);
        Assert.Equal(1, registry.Snapshot().Single(e => e.Provenance == RequestProvenance.DiscoveryProbe).Count);
    }

    [Fact]
    public void CaptureCarriesExplicitProvenanceWithoutChangingTransport()
    {
        foreach (var provenance in new[] { RequestProvenance.BrowserObservedTraffic, RequestProvenance.DiscoveryProbe, RequestProvenance.BirkNextDiagnostic })
        {
            var e = NetworkTrafficClassifier.Classify(new NetworkRequestMetadata { Host = "app.test", Port = 443, Method = "POST", Target = "/graphql", Provenance = provenance }, DateTimeOffset.UtcNow);
            Assert.Equal(provenance, e.Provenance);
            Assert.Equal(EndpointDiscoverySource.AuthenticatedProxyTraffic, e.Source);
        }
    }

    private static ObservedNetworkEndpoint Endpoint(string path) => new()
    {
        Host = "app.test", Path = path, Method = "GET", Category = ObservedTrafficCategory.Rest,
        Provenance = RequestProvenance.ApplicationTraffic, Count = 1
    };
}
