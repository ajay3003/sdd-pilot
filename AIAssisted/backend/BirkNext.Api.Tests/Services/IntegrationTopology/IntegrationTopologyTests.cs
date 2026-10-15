using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BirkNext.Api.Services.ActiveEventTesting;
using BirkNext.Api.Services.ContractAnalysis;
using BirkNext.Api.Services.IntegrationJourneys.Packs.SkoleNaervaer;
using BirkNext.Api.Services.IntegrationTopology;
using BirkNext.Api.Tests.Services.IntegrationJourneys;
using BirkNext.Integrations;
using BirkNext.IntegrationTopology;
using BirkNext.SourceArchitecture;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace BirkNext.Api.Tests.Services.IntegrationTopology;

/// <summary>
/// The generic Integration Topology: per-component interface inventories and evidence-based connections merged from several providers, one
/// canonical element per interface with every source's provenance, configured ≠ discovered ≠ observed, conflicts shown, nothing guessed,
/// nothing fetched unless discovery is requested explicitly — and then only from a trusted component's own host.
/// </summary>
public sealed class IntegrationTopologyTests : IAsyncLifetime
{
    private const string Env = BirkNext.Api.Tests.Services.ActiveEventTesting.ActiveEventTestHarness.Env;
    private readonly IntegrationJourneyHarness _harness = new();
    private readonly FakeHttp _http = new();

    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync() => await _harness.DisposeAsync();

    private sealed class FakeHttp : HttpMessageHandler, IHttpClientFactory
    {
        public List<Uri> Calls { get; } = [];
        public string? OpenApi { get; set; }
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls.Add(request.RequestUri!);
            return Task.FromResult(OpenApi is null ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(OpenApi, Encoding.UTF8, "application/json") });
        }
    }

    private const string ReportApi = """
        {"openapi":"3.0.1","info":{"title":"Report","version":"1"},"paths":{
          "/api/reports/{id}":{"get":{"operationId":"getReport","parameters":[{"name":"id","in":"path","required":true,"schema":{"type":"string"}}],"responses":{"200":{"description":"ok"}}}},
          "/api/reports":{"post":{"operationId":"submitReport","responses":{"200":{"description":"ok"}}}}}}
        """;

    private void Trust(params (string Name, string? BaseUrl, string? OpenApiPath)[] components)
    {
        for (var i = 0; i < components.Length; i++)
        {
            _harness.Active.Settings[$"TargetEnvironments:Trusted:0:Components:{i}:Name"] = components[i].Name;
            _harness.Active.Settings[$"TargetEnvironments:Trusted:0:Components:{i}:BaseUrl"] = components[i].BaseUrl;
            _harness.Active.Settings[$"TargetEnvironments:Trusted:0:Components:{i}:OpenApiPath"] = components[i].OpenApiPath;
        }
    }

    private void Source(string archive, ArchitectureSnapshot architecture) =>
        _harness.Sources.Snapshots.Add(new IqrSourceSnapshot { Archive = new SourceArchive(archive, "sha", 1), AnalyzedAt = DateTimeOffset.UtcNow, Architecture = architecture });

    private void Integration(string id, IntegrationKind kind, string producer, string consumer, string address) => _harness.Active.Catalog.Extra.Add(new IntegrationDefinition
    {
        Id = id, EnvironmentId = Env, DisplayName = id, Kind = kind, Enabled = true, Producer = producer, EndpointOrTopic = address, Consumer = new IntegrationConsumer { DisplayName = consumer },
    });

    private IntegrationTopologyService Service(params ITopologyProfile[] profiles) => new(
        [new ProfileTopologyProvider(profiles), new TargetEnvironmentTopologyProvider(), new SourceArchitectureTopologyProvider(), new IntegrationCatalogTopologyProvider(),
         new OpenApiTopologyProvider(_http, new OpenApiExtractor(NullLogger<OpenApiExtractor>.Instance), TimeProvider.System), new ObservationTopologyProvider()],
        profiles, TrustedExecutionEnvironmentRegistry.From(_harness.Active.Configuration()), _harness.Active.Catalog, _harness.Sources, TimeProvider.System);

    private async Task<IntegrationTopologyView> Build(TopologyRequest? request = null, params ITopologyProfile[] profiles) =>
        (await Service(profiles).BuildAsync(Env, request ?? new TopologyRequest(null), default)).View;

    private static TopologyComponent Component(IntegrationTopologyView view, string name) => view.Components.Single(item => item.Name == name);

    private static ArchitectureSnapshot ReportService() => new()
    {
        Components = [new() { Id = "c1", Name = "ReportService", ComponentType = ArchitectureComponentType.Api, SourceProject = "Report.Api" },
                      new() { Id = "c2", Name = "Intake", ComponentType = ArchitectureComponentType.Worker, SourceProject = "Intake.Worker" }],
        Interfaces = [new() { Id = "i1", ComponentId = "c1", Type = "HTTP route", Name = "POST /api/reports", Protocol = "HTTP", Direction = "Inbound", RouteOrTopic = "/api/reports" },
                      new() { Id = "i2", ComponentId = "c1", Type = "GraphQL endpoint", Name = "/graphql", Protocol = "GraphQL", Direction = "Inbound", RouteOrTopic = "/graphql" }],
        Dependencies = [new() { Id = "d1", FromComponentId = "c2", ToId = "c1", TargetReference = "/api/reports", DependencyType = ArchitectureDependencyType.Http, Protocol = "HTTP" }],
        MessagingChannels = [new() { Id = "m1", Name = "reports.received", EntityName = "reports.received", Type = MessagingChannelType.ServiceBusTopic,
            Producers = [new("c1", ChannelRole.Producer, ArchitectureEvidenceState.Confirmed, "Wolverine", [])], Consumers = [new("c2", ChannelRole.Consumer, ArchitectureEvidenceState.Confirmed, "Wolverine", [])] }],
    };

    // ── Interfaces: discovery, merge and observation ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task OpenApiSourceAndBrowser_StateTheSameInterface_OneCanonicalInterfaceWithThreeProvenances_Observed()
    {
        Trust(("ReportService", "https://report.example.test/", "/swagger/v1/swagger.json"));
        Source("report-service.zip", ReportService());
        _http.OpenApi = ReportApi;

        var view = await Build(new TopologyRequest([new("Rest", "POST", "report.example.test", "/api/reports/", null, DateTimeOffset.UtcNow)], DiscoverContracts: true));

        var post = Component(view, "ReportService").Interfaces.Where(item => item.Type == TopologyInterfaceType.Rest && item.Operation == "POST").Should().ContainSingle().Subject;
        post.Provenance.Select(item => item.Source).Should().BeEquivalentTo([TopologySource.OpenApi, TopologySource.SourceAnalysis, TopologySource.BrowserObservation]);
        post.Status.Should().Be(TopologyStatus.Observed);
        Component(view, "ReportService").Interfaces.Single(item => item.Operation == "GET").Should().Match<TopologyInterface>(item => item.Status == TopologyStatus.Discovered && item.Address == "/api/reports/{id}");
        _http.Calls.Should().ContainSingle().Which.ToString().Should().Be("https://report.example.test/swagger/v1/swagger.json");
    }

    [Fact]
    public async Task DiscoveryAlone_IsNeverObserved_AndNothingIsFetchedUnlessRequested()
    {
        Trust(("ReportService", "https://report.example.test/", "/swagger/v1/swagger.json"));
        Source("report-service.zip", ReportService());
        _http.OpenApi = ReportApi;

        var view = await Build();
        _http.Calls.Should().BeEmpty("OpenAPI documents are read only by the explicit discovery action");
        view.Sources.Single(item => item.Source == TopologySource.OpenApi).State.Should().Be(TopologySourceState.NotRequested);
        view.Components.SelectMany(item => item.Interfaces).Should().NotContain(item => item.Status == TopologyStatus.Observed);
        view.Sources.Single(item => item.Source == TopologySource.BrowserObservation).State.Should().Be(TopologySourceState.NotRequested);
    }

    [Fact]
    public async Task GraphQlMessagingAndFileInterfaces_AreFirstClass_AndASourceOnlyComponentNeedsNoUiOrOpenApi()
    {
        Source("report-service.zip", ReportService());
        Integration("export", IntegrationKind.File, "ReportService", "Archive", "reports-export/");
        var view = await Build();

        Component(view, "ReportService").Interfaces.Should().Contain(item => item.Type == TopologyInterfaceType.GraphQl && item.Address == "/graphql");
        Component(view, "ReportService").Interfaces.Should().Contain(item => item.Type == TopologyInterfaceType.Messaging && item.Direction == TopologyDirection.Produces && item.Address == "reports.received");
        Component(view, "Intake").Interfaces.Should().Contain(item => item.Type == TopologyInterfaceType.Messaging && item.Direction == TopologyDirection.Consumes);
        Component(view, "Intake").Location.Should().BeNull("a source-only component has no stated location and still has its interfaces");
        view.Connections.Should().Contain(item => item.Type == TopologyInterfaceType.Messaging && item.ProducerComponentId == "reportservice" && item.ConsumerComponentId == "intake" && item.Status == TopologyStatus.Discovered);
        view.Connections.Should().Contain(item => item.Type == TopologyInterfaceType.File && item.ConsumerComponentId == "archive" && item.Status == TopologyStatus.Configured);
    }

    [Fact]
    public async Task ConfiguredAndDiscoveredConnections_Merge_ButDifferentAddressesAreAConflict()
    {
        Source("report-service.zip", ReportService());
        Integration("intake-http", IntegrationKind.HttpApi, "Intake", "ReportService", "/api/reports");
        var merged = await Build();
        merged.Connections.Single(item => item.Type == TopologyInterfaceType.Rest && item.ProducerComponentId == "intake")
            .Should().Match<TopologyConnection>(item => item.Status == TopologyStatus.Discovered && item.Provenance.Count == 2);

        _harness.Active.Catalog.Extra.Clear();
        Integration("intake-http", IntegrationKind.HttpApi, "Intake", "ReportService", "/api/v2/reports");
        var conflicting = await Build();
        conflicting.Connections.Where(item => item.Type == TopologyInterfaceType.Rest && item.ProducerComponentId == "intake")
            .Should().HaveCount(2).And.OnlyContain(item => item.Status == TopologyStatus.Conflict && item.Notes.Any(note => note.Contains("different addresses")));
    }

    [Fact]
    public async Task AConflictingConsumerTarget_IsShown_AndNoConnectionIsInferred()
    {
        var architecture = ReportService() with
        {
            Dependencies = [new() { Id = "d1", FromComponentId = "c2", TargetReference = "ReportApi:BaseUrl", DependencyType = ArchitectureDependencyType.Http, Protocol = "HTTP",
                ConflictingTargets = ["c1", "c3"], EvidenceState = ArchitectureEvidenceState.Conflict }],
            MessagingChannels = [],
        };
        Source("report-service.zip", architecture);
        var view = await Build();
        Component(view, "Intake").Interfaces.Single(item => item.Direction == TopologyDirection.Calls).Status.Should().Be(TopologyStatus.Conflict);
        view.Connections.Should().NotContain(item => item.Type == TopologyInterfaceType.Rest, "a conflicting target never becomes a connection");
    }

    [Fact]
    public async Task NoEvidence_GuessesNothing_AndExpectedPathsStayUnresolved()
    {
        var view = await Build(profiles: new SkoleNaervaerTopologyProfile());
        Component(view, "MM").Status.Should().Be(TopologyStatus.Expected);
        view.Connections.Should().NotContain(item => item.ProducerComponentId == "mm" || item.ConsumerComponentId == "mm");
        var paths = await Service(new SkoleNaervaerTopologyProfile()).PathsAsync(Env, SkoleNaervaerTopologyProfile.ProfileId, new(null), default);
        paths!.Single(item => item.PathId == "rapportmottak").Steps.Should().OnlyContain(step => step.State == TopologyPathState.Unresolved && !step.Observed);
    }

    [Fact]
    public async Task AProfileAlias_MergesDifferentlyNamedSources_AndResolvesOnlyTheEvidencedStep()
    {
        Integration("report-intake", IntegrationKind.HttpApi, "Altinn (TT02)", "MU", "report submission");
        var paths = await Service(new SkoleNaervaerTopologyProfile()).PathsAsync(Env, SkoleNaervaerTopologyProfile.ProfileId, new(null), default);
        var steps = paths!.Single(item => item.PathId == "rapportmottak").Steps;
        steps[0].Should().Match<TopologyPathStep>(step => step.State == TopologyPathState.Resolved && step.Evidence == TopologyStatus.Configured && !step.Observed);
        steps.Skip(1).Should().OnlyContain(step => step.State == TopologyPathState.Unresolved, "MU → MM, MM → Skoletjenesten and Skoletjenesten → Bufdata have no evidence");
        (await Build(profiles: new SkoleNaervaerTopologyProfile())).Components.Should().NotContain(item => item.Name == "Altinn (TT02)", "the alias merged into Altinn");
    }

    // ── Target Environment, network safety and architecture ───────────────────────────────────────────────────────

    [Fact]
    public async Task TargetEnvironment_StatesMembershipAndLocation_RefusesUnsafeAddresses_AndHoldsNoInterfaces()
    {
        Trust(("ReportService", "https://report.example.test/base", null), ("Leaky", "https://user:secret@leaky.example.test/", null));
        var view = await Build();
        Component(view, "ReportService").Should().Match<TopologyComponent>(item => item.Location == "https://report.example.test/base/" && item.Status == TopologyStatus.Configured && item.Interfaces.Count == 0);
        Component(view, "Leaky").Location.Should().BeNull();
        JsonSerializer.Serialize(view).Should().NotContain("secret");
        view.Sources.Single(item => item.Source == TopologySource.TargetEnvironment).Detail.Should().Contain("execution allowed");
    }

    [Theory]
    [InlineData("https://other.example.test/swagger.json")]
    [InlineData("//other.example.test/swagger.json")]
    [InlineData("/../../etc/passwd")]
    [InlineData("swagger.json")]
    public void OpenApiDiscovery_ReadsOnlyARelativePathOnTheComponentsOwnHost(string path) =>
        TrustedComponentPolicy.OpenApiAddress(new TrustedComponent("A", "https://a.example.test/", path)).Should().BeNull();

    [Fact]
    public void GenericTopology_KnowsNoDomainOrProjectNames()
    {
        var root = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(root, "BirkNext.Api"))) root = Path.GetDirectoryName(root)!;
        var files = Directory.EnumerateFiles(Path.Combine(root, "BirkNext.Api", "Services", "IntegrationTopology"), "*.cs")
            .Append(Path.GetFullPath(Path.Combine(root, "..", "shared", "IntegrationTopologyContracts.cs"))).Append(Path.Combine(root, "BirkNext.Api", "Controllers", "IntegrationTopologyController.cs")).ToList();
        var forbidden = new Regex(@"(?i:Skole|skolenaervaer|Altinn|Bufdata|SkoleAdapter|M2LB|CriticalE2E)|\bMU\b|\bMM\b|BiRK", RegexOptions.CultureInvariant);
        files.Where(file => forbidden.IsMatch(File.ReadAllText(file))).Select(Path.GetFileName).Should().BeEmpty();
    }
}
