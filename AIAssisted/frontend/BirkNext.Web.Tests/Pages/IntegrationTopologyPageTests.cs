using BirkNext.IntegrationTopology;
using BirkNext.LocalHttpsProxy;
using BirkNext.Web.Components;
using BirkNext.Web.Models;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// The canonical Integration Topology page and its read-only consumers (Skolenærvær expected flow, IQR integrations): components with interfaces
/// of several types and several sources, explicit discovery, conflicts shown, and no endpoint editing anywhere.
/// </summary>
public sealed class IntegrationTopologyPageTests : BunitContext
{
    private readonly FakeApi _api = new();
    private FrontendAnalysisContext _context = new() { ActiveProfile = new FrontendAnalysisProfile { Id = "dev", Name = "DEV" } };

    public IntegrationTopologyPageTests()
    {
        var factory = new Mock<IFrontendAnalysisContextFactory>();
        factory.Setup(f => f.GetActiveContextAsync()).ReturnsAsync(() => _context);
        Services.AddSingleton(factory.Object);
        Services.AddSingleton<IIntegrationTopologyApiService>(_api);
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private sealed class FakeApi : IIntegrationTopologyApiService
    {
        public int Gets { get; private set; }
        public int Discovers { get; private set; }
        public IntegrationTopologyView View { get; set; } = Sample();
        public List<TopologyPathResult> Paths { get; set; } = [];
        public Task<(IntegrationTopologyView? View, string? Error)> GetAsync(string environmentId, IReadOnlyList<TopologyObservation> observations, CancellationToken ct = default) { Gets++; return Task.FromResult<(IntegrationTopologyView?, string?)>((View, null)); }
        public Task<(IntegrationTopologyView? View, string? Error)> DiscoverAsync(string environmentId, IReadOnlyList<TopologyObservation> observations, CancellationToken ct = default) { Discovers++; return Task.FromResult<(IntegrationTopologyView?, string?)>((View, null)); }
        public Task<(IReadOnlyList<TopologyPathResult>? Paths, string? Error)> PathsAsync(string environmentId, string profileId, IReadOnlyList<TopologyObservation> observations, CancellationToken ct = default) =>
            Task.FromResult<(IReadOnlyList<TopologyPathResult>?, string?)>((Paths, null));
    }

    private static TopologyProvenance From(TopologySource source, string reference = "ref") => new(source, reference, "detail");

    private static IntegrationTopologyView Sample() => new()
    {
        EnvironmentId = "dev", EnvironmentName = "DEV", GeneratedAt = new DateTimeOffset(2026, 10, 15, 10, 0, 0, TimeSpan.Zero),
        Sources = [new(TopologySource.SourceAnalysis, TopologySourceState.Used, "1 snapshot"), new(TopologySource.OpenApi, TopologySourceState.NotRequested, "select Discover interfaces")],
        Components =
        [
            new()
            {
                Id = "report", Name = "ReportService", Location = "https://report.example.test/", Status = TopologyStatus.Observed, Provenance = [From(TopologySource.TargetEnvironment)],
                Interfaces =
                [
                    new() { Id = "i1", ComponentId = "report", Type = TopologyInterfaceType.Rest, Operation = "POST", Address = "/api/reports", Direction = TopologyDirection.Provides, Protocol = "HTTP",
                        Status = TopologyStatus.Observed, Provenance = [From(TopologySource.OpenApi), From(TopologySource.SourceAnalysis), From(TopologySource.BrowserObservation)] },
                    new() { Id = "i2", ComponentId = "report", Type = TopologyInterfaceType.GraphQl, Address = "/graphql", Direction = TopologyDirection.Provides, Protocol = "GraphQL", Status = TopologyStatus.Discovered, Provenance = [From(TopologySource.SourceAnalysis)] },
                    new() { Id = "i3", ComponentId = "report", Type = TopologyInterfaceType.Messaging, Address = "reports.received", Direction = TopologyDirection.Produces, Protocol = "ServiceBusTopic", Status = TopologyStatus.Discovered, Provenance = [From(TopologySource.SourceAnalysis)] },
                ],
            },
            new() { Id = "intake", Name = "Intake", Status = TopologyStatus.Discovered, Provenance = [From(TopologySource.SourceAnalysis)] },
        ],
        Connections =
        [
            new() { Id = "c1", ProducerComponentId = "intake", ConsumerComponentId = "report", Type = TopologyInterfaceType.Rest, Protocol = "HTTP", Address = "/api/reports", Status = TopologyStatus.Conflict,
                Provenance = [From(TopologySource.SourceAnalysis), From(TopologySource.IntegrationCatalog, "dev:http:intake")], Notes = ["Sources state different addresses for this connection: /api/reports, /api/v2/reports."] },
        ],
    };

    [Fact]
    public void DefaultView_ListsComponentsAndSources_DetailShowsInterfacesOfEveryTypeWithEverySource()
    {
        var page = Render<IntegrationTopologyPage>();
        page.FindAll("[data-testid=it-component]").Select(item => item.GetAttribute("data-name")).Should().Equal("ReportService", "Intake");
        page.FindAll("[data-testid=it-interface]").Should().BeEmpty("interfaces are details, not the default view");
        page.FindAll("[data-testid=it-source]").Should().HaveCount(2);

        page.FindAll("[data-testid=it-select]")[0].Click();
        var interfaces = page.FindAll("[data-testid=it-interface]");
        interfaces.Select(item => item.GetAttribute("data-type")).Should().BeEquivalentTo(["Rest", "GraphQl", "Messaging"]);
        interfaces.First(item => item.GetAttribute("data-type") == "Rest").TextContent.Should().Contain("POST /api/reports").And.Contain("OpenAPI").And.Contain("Source Analysis").And.Contain("Browser observation").And.Contain("Observed");
        page.Find("[data-testid=it-location]").TextContent.Should().Be("https://report.example.test/");
        page.Find("[data-testid=it-detail-connection]").TextContent.Should().Contain("Conflict").And.Contain("different addresses");
        page.FindAll("input, textarea, select").Should().BeEmpty("the topology is read-only; no endpoint is edited here");
    }

    [Fact]
    public void DiscoverInterfaces_IsAnExplicitAction()
    {
        var page = Render<IntegrationTopologyPage>();
        (_api.Gets, _api.Discovers).Should().Be((1, 0));
        page.Find("[data-testid=it-discover]").Click();
        _api.Discovers.Should().Be(1);
    }

    [Fact]
    public void WithoutATargetEnvironment_ItSaysSo()
    {
        _context = new FrontendAnalysisContext();
        Render<IntegrationTopologyPage>().Find("[data-testid=it-no-environment]").TextContent.Should().Contain("Select a Target Environment");
        _api.Gets.Should().Be(0);
    }

    [Fact]
    public void ExpectedFlowPanel_IsReadOnly_AndKeepsResolvedAndObservedApart()
    {
        _api.Paths = [new("skolenaervaer", "rapportmottak", "Rapportmottak",
        [
            new("A → B", "A", "B", TopologyPathState.Resolved, TopologyStatus.Configured, false, "c", "configured · not observed at runtime"),
            new("B → C", "B", "C", TopologyPathState.Unresolved, TopologyStatus.NotEvaluated, false, null, "No source states a connection B → C."),
        ])];
        var panel = Render<TopologyPathsPanel>(p => p.Add(x => x.EnvironmentId, "dev").Add(x => x.ProfileId, "skolenaervaer"));
        var steps = panel.FindAll("[data-testid=tp-step]");
        steps[0].TextContent.Should().Contain("Configured").And.Contain("observed: no");
        steps[1].GetAttribute("data-state").Should().Be("Unresolved");
        panel.Find("[data-testid=tp-path]").GetAttribute("data-resolved").Should().Be("false");
        panel.FindAll("input, textarea, select").Should().BeEmpty();
    }

    [Fact]
    public void IqrIntegrations_ComeFromTheTopology_WithProvenance_AndNoEditing()
    {
        var panel = Render<TopologyIntegrationConnections>(p => p.Add(x => x.EnvironmentId, "dev"));
        var row = panel.Find("[data-testid=ti-connection]");
        row.TextContent.Should().Contain("Intake → ReportService").And.Contain("dev:http:intake").And.Contain("Source Analysis").And.Contain("Integration catalog").And.Contain("Conflict");
        panel.FindAll("input, textarea, select, button").Should().BeEmpty();
    }

    [Fact]
    public void BrowserObservations_AreMethodHostAndPathOnly()
    {
        var snapshot = new EndpointDiscoverySnapshot();
        snapshot.Shared.Add(new ObservedNetworkEndpoint { Category = ObservedTrafficCategory.Rest, Method = "GET", Host = "api.example.test", Path = "/api/reports?id=1&token=secret", LastObservedAt = DateTimeOffset.UtcNow });
        snapshot.Shared.Add(new ObservedNetworkEndpoint { Category = ObservedTrafficCategory.StaticAsset, Method = "GET", Host = "cdn.example.test", Path = "/app.js" });
        var observation = TopologyObservations.From(snapshot).Should().ContainSingle().Subject;
        (observation.Method, observation.Host, observation.Path).Should().Be(("GET", "api.example.test", "/api/reports"));
    }
}
