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
        public TopologyCoverageView? Coverage { get; set; }
        public Task<(TopologyCoverageView? Coverage, string? Error)> CoverageAsync(string environmentId, string profileId, IReadOnlyList<TopologyObservation> observations, CancellationToken ct = default) =>
            Task.FromResult<(TopologyCoverageView?, string?)>((Coverage, Coverage is null ? "not configured" : null));
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
        page.FindAll("textarea").Should().BeEmpty("the topology is read-only; no endpoint is edited here");
        page.FindAll("input, select").Should().OnlyContain(control => control.Closest(".it-filters") != null, "the only controls filter the interface inventory");
    }

    [Fact]
    public void DiscoverInterfaces_IsAnExplicitPreviewedAction_SeparateFromRefresh()
    {
        _api.View = Sample() with { Discovery = new([new("ReportService", "report.example.test", "/swagger/v1/swagger.json")], [new("Legacy", "", "https://other.example.test/x", "not on its own origin")]) };
        var page = Render<IntegrationTopologyPage>();
        (_api.Gets, _api.Discovers).Should().Be((1, 0));
        page.Find("[data-testid=it-refresh]").TextContent.Should().Be("Refresh view");
        page.FindAll("[data-testid=it-discovery]").Should().BeEmpty("nothing about discovery runs or shows until asked");
        page.Find("[data-testid=it-discover]").Click();
        _api.Discovers.Should().Be(0, "opening discovery only previews it");
        page.Find("[data-testid=it-discovery]").TextContent.Should().Contain("read-only HTTP requests").And.Contain("No application data is changed");
        (page.Find("[data-testid=it-discovery-eligible]").TextContent, page.Find("[data-testid=it-discovery-hosts]").TextContent).Should().Be(("1", "1"));
        page.Find("[data-testid=it-discovery-host-list]").TextContent.Should().Contain("report.example.test/swagger/v1/swagger.json").And.Contain("Legacy: not queried");
        page.Find("[data-testid=it-discover-run]").Click();
        _api.Discovers.Should().Be(1);
    }

    [Fact]
    public void Discovery_WithNothingEligible_CannotRun_AndSaysWhy()
    {
        var page = Render<IntegrationTopologyPage>();
        page.Find("[data-testid=it-discover]").Click();
        page.Find("[data-testid=it-discover-run]").HasAttribute("disabled").Should().BeTrue();
        page.Find("[data-testid=it-discovery-none]").TextContent.Should().Contain("No additional OpenAPI document targets are currently eligible")
            .And.Contain("Existing source, runtime and configuration evidence remains available below (3 interfaces)");
    }

    private static TopologyCoverageView Coverage() => new()
    {
        ProfileId = "orders", ProfileLabel = "Orders", ResolverVersion = 2, CoreSourcesComplete = true, Summary = "Topology is partially resolved. 1 expected relationship needs additional evidence.",
        Roles =
        [
            new() { RoleId = "app", DisplayName = "Orders application", Requirement = TopologySourceRequirement.Required, State = TopologySourceRoleState.Available, MatchingSources = ["a1.zip"] },
            new() { RoleId = "iac", DisplayName = "Infrastructure as Code", Requirement = TopologySourceRequirement.Recommended, State = TopologySourceRoleState.Available },
            new() { RoleId = "ref", DisplayName = "Payments reference", Requirement = TopologySourceRequirement.BuiltInReference, State = TopologySourceRoleState.ReferenceAvailable },
            new() { RoleId = "ledger", DisplayName = "Ledger consumer", Requirement = TopologySourceRequirement.Conditional, State = TopologySourceRoleState.NotProvided, Consequence = "Service Bus → Ledger may remain unresolved." },
        ],
        Relationships =
        [
            new() { Id = "a", Label = "Billing → Orders", From = "Billing", To = "Orders", Transport = "HTTP", Dimension = "Application", State = TopologyRelationshipState.Resolved,
                Route = "Billing → Orders", Why = "Very strong: explicit client target.", Unverified = "Not observed at runtime.",
                Evidence = [new("http-client-target-host", TopologyEvidenceStrength.VeryStrong, "Application source", "Billing sets OrdersApi to host ca-orders.", "b2.zip")] },
            new() { Id = "b", Label = "Service Bus → Ledger", From = "Service Bus", To = "Ledger consumer", Transport = "Service Bus", Dimension = "External consumer",
                State = TopologyRelationshipState.ExpectedUnresolved, AvailableEvidence = "Orders produces to orders.created.", MissingEvidence = "A Ledger consumer of an entity Orders produces.",
                SourceRoleId = "ledger", SourceRoleName = "Ledger consumer", SourceRoleState = TopologySourceRoleState.NotProvided, Action = TopologyGapAction.ImportSource, Owner = "Project Import" },
        ],
        Completeness = [new("Application", TopologyCompletenessState.Complete, "1 resolved."), new("External consumer", TopologyCompletenessState.Partial, "0 of 1 resolved."),
            new("Runtime observation", TopologyCompletenessState.NotObserved, "No runtime observation.")],
    };

    [Fact]
    public void SourceCoverage_AndResolvedVersusExpectedButUnresolved_AreTheDefault_WithMissingSourceGuidance()
    {
        _api.View = Sample() with { Profiles = ["orders"] };
        _api.Coverage = Coverage();
        var page = Render<IntegrationTopologyPage>();

        page.Find("[data-testid=it-count-unresolved]").TextContent.Should().Be("1 of 2 expected");
        page.Find("[data-testid=it-count-coverage]").TextContent.Should().Be("Core sources complete");
        page.FindAll("[data-testid=it-role-group]").Select(group => group.GetAttribute("data-group")).Should().Equal(
            "Required application sources", "Infrastructure", "Built-in references", "Additional evidence");
        page.Find("[data-role=ref]").TextContent.Should().Contain("Reference available");
        page.Find("[data-role=ledger]").TextContent.Should().Contain("Not provided").And.Contain("may remain unresolved");
        page.Find("[data-testid=it-partial]").TextContent.Should().Contain("partially resolved").And.Contain("View missing evidence");

        page.FindAll("[data-testid=it-resolved]").Should().ContainSingle().Which.TextContent.Should().Contain("Billing → Orders").And.Contain("Resolved")
            .And.Contain("Very strong").And.Contain("What remains unverified");
        page.Find("[data-testid=it-unresolved]").TextContent.Should().Contain("Expected but unresolved").And.Contain("Additional source needed").And.Contain("Ledger consumer");
        page.Find("[data-testid=it-missing]").TextContent.Should().Be("A Ledger consumer of an entity Orders produces.");
        var action = page.Find("[data-testid=it-gap-action]");
        (action.TextContent, action.GetAttribute("href")).Should().Be(("Import source", "/project-import"));
        page.Find("[data-testid=it-owner]").TextContent.Should().Be("Project Import");
        page.FindAll("[data-testid=it-dimension]").Select(item => item.GetAttribute("data-state")).Should().Equal("Complete", "Partial", "NotObserved");
        page.Find("[data-testid=it-relationships]").TextContent.Should().NotContain("%", "completeness is categorical, never a percentage");
    }

    [Fact]
    public void Components_AreGroupedByKind_WithResourcesCollapsed()
    {
        _api.View = Sample() with { Components = [.. Sample().Components, new() { Id = "resource:servicebus:t", Name = "t", Kind = TopologyComponentKind.Resource, Status = TopologyStatus.Discovered }] };
        var page = Render<IntegrationTopologyPage>();
        page.FindAll("[data-testid=it-component-group]").Select(group => (group.GetAttribute("data-kind"), group.HasAttribute("open"))).Should().Equal([("Application", false), ("Resource", false)],
            "components are technical inventory, one disclosure away");
        page.Find("[data-testid=it-count-components]").TextContent.Should().Be("2", "resources are not counted as components");
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

    // ── Source presence vs binding, state-aware actions, attention, confidence, discovery preflight, inventory filters ──

    private static TopologyCoverageView BindingCoverage() => Coverage() with
    {
        CoreSourcesComplete = false,
        Roles =
        [
            new() { RoleId = "mu", DisplayName = "Meldingsutveksleren (MU)", Requirement = TopologySourceRequirement.Required, State = TopologySourceRoleState.Bound,
                MatchingSources = ["Tiltak 2.zip"], BoundSources = ["Tiltak 2.zip"], Action = TopologyGapAction.ViewEvidence },
            new() { RoleId = "m2lb", DisplayName = "M2LB", Requirement = TopologySourceRequirement.Required, State = TopologySourceRoleState.AvailableNeedsBinding,
                MatchingSources = ["M2LB.zip"], Action = TopologyGapAction.BindSource, Detail = "M2LB.zip matches by content but is not bound." },
            new() { RoleId = "birk", DisplayName = "BiRK application source", Requirement = TopologySourceRequirement.Conditional, State = TopologySourceRoleState.NotProvided, Action = TopologyGapAction.ImportSource },
            new() { RoleId = "bufdata", DisplayName = "Bufdata consumer evidence", Requirement = TopologySourceRequirement.Conditional, State = TopologySourceRoleState.Ambiguous,
                MatchingSources = ["a.zip", "b.zip"], Action = TopologyGapAction.ReviewCandidates },
        ],
        Relationships =
        [
            new() { Id = "r", Label = "SkoleAdapter → Skoletjenesten", Transport = "HTTP", State = TopologyRelationshipState.Resolved, Unverified = "x" },
            new() { Id = "o", Label = "Altinn → MU", Transport = "Webhook", State = TopologyRelationshipState.Observed, RuntimeObserved = true, Unverified = "x" },
            new() { Id = "s", Label = "MU → MM", Transport = "Service Bus", State = TopologyRelationshipState.StronglySupported, SourceRoleName = "M2LB",
                SourceRoleState = TopologySourceRoleState.AvailableNeedsBinding, Action = TopologyGapAction.BindSource, MissingEvidence = "matching queue" },
            new() { Id = "m", Label = "MM → Skoletjenesten", Transport = "HTTP", State = TopologyRelationshipState.Candidate, SourceRoleName = "M2LB",
                SourceRoleState = TopologySourceRoleState.AvailableNeedsBinding, Action = TopologyGapAction.BindSource, MissingEvidence = "base address" },
            new() { Id = "b", Label = "BiRK → CDC", Transport = "CDC", State = TopologyRelationshipState.ExpectedUnresolved, SourceRoleName = "BiRK application source",
                SourceRoleState = TopologySourceRoleState.NotProvided, Action = TopologyGapAction.ImportSource, MissingEvidence = "BiRK side" },
        ],
    };

    [Fact]
    public void AvailableUnboundSource_ReadsNeedsBinding_AndOffersBindNotImport()
    {
        _api.View = Sample() with { Profiles = ["skolenaervaer"] };
        _api.Coverage = BindingCoverage();
        var page = Render<IntegrationTopologyPage>();

        var m2lb = page.Find("[data-testid=it-role][data-role=m2lb]");
        m2lb.TextContent.Should().Contain("Available — needs binding").And.Contain("Matching: M2LB.zip").And.NotContain("Not provided");
        var bind = m2lb.QuerySelector("[data-testid=it-role-action]")!;
        (bind.TextContent, bind.GetAttribute("href")).Should().Be(("Bind existing source", "/integration-quality-review?view=setup-evidence"));
        page.Find("[data-testid=it-role][data-role=mu]").TextContent.Should().Contain("Bound").And.Contain("Bound: Tiltak 2.zip");
        page.Find("[data-testid=it-role][data-role=mu]").QuerySelector("[data-testid=it-role-action]").Should().BeNull("a bound source needs no action");
        page.Find("[data-testid=it-role][data-role=birk]").QuerySelector("[data-testid=it-role-action]")!.TextContent.Should().Be("Import source");
        page.Find("[data-testid=it-role][data-role=bufdata]").TextContent.Should().Contain("Ambiguous — several candidates").And.Contain("Review source candidates");

        var actions = page.FindAll("[data-testid=it-unresolved] [data-testid=it-gap-action]");
        actions.Select(item => item.TextContent).Should().Equal("Bind existing source", "Bind existing source", "Import source");
        page.FindAll("[data-testid=it-gap-group]").Select(item => item.TextContent).Should().Equal(
            "2 need M2LB binding (source available)", "1 needs BiRK application source (not provided — import)");
        page.Find("[data-testid=it-role-row][data-role=m2lb] [data-testid=it-role-binding]").TextContent.Should().Be("Not bound");
        page.Find("[data-testid=it-role-row][data-role=mu] [data-testid=it-role-binding]").TextContent.Should().Be("Tiltak 2.zip");
    }

    [Fact]
    public void Attention_IsCategorized_MutuallyExclusive_NotOneOpaqueNumber()
    {
        var view = Sample() with { Profiles = ["skolenaervaer"] };
        var report = view.Components[0];
        view = view with { Components = [report with { Interfaces = [.. report.Interfaces,
            new() { Id = "c1", ComponentId = "report", Type = TopologyInterfaceType.Rest, Direction = TopologyDirection.Calls, Address = "ApiBaseUrl + autorisasjon/graphql", Status = TopologyStatus.NeedsReview, Protocol = "HTTP" },
            new() { Id = "c2", ComponentId = "report", Type = TopologyInterfaceType.Messaging, Direction = TopologyDirection.Consumes, Address = "q", Status = TopologyStatus.NeedsReview, Protocol = "SB" }] }, view.Components[1]] };
        _api.View = view;
        _api.Coverage = BindingCoverage();
        var page = Render<IntegrationTopologyPage>();

        page.FindAll("[data-testid=it-count-attention]").Should().BeEmpty("the single opaque count is gone");
        page.FindAll("[data-testid=it-attention-category]").Select(item => (item.GetAttribute("data-key"), item.GetAttribute("data-count"))).Should().Equal(
            [("unresolved", "3"), ("sources", "3"), ("targets", "1"), ("interfaces", "1"), ("ambiguous", "1")]);
        page.Find("[data-testid=it-attention]").TextContent.Should().Contain("Expected relationships unresolved").And.Contain("Unresolved call targets (relationship candidates)");

        page.Find("[data-testid=it-attention-category][data-key=targets] a").Click();
        page.Find("[data-testid=it-interfaces]").HasAttribute("open").Should().BeTrue();
        page.Find("[data-testid=it-candidate]").TextContent.Should().Contain("Relationship candidate").And.Contain("not resolved");
    }

    [Fact]
    public void ResolvedAndObserved_AreSeparateDimensions_AndStronglySupportedIsBelowResolved()
    {
        _api.View = Sample() with { Profiles = ["skolenaervaer"] };
        _api.Coverage = BindingCoverage();
        var page = Render<IntegrationTopologyPage>();

        var resolved = page.FindAll("[data-testid=it-resolved]");
        resolved.Single(item => item.GetAttribute("data-relationship") == "r").TextContent.Should().Contain("Resolved").And.Contain("Not observed at runtime");
        resolved.Single(item => item.GetAttribute("data-relationship") == "o").TextContent.Should().Contain("Resolved").And.Contain("Observed at runtime")
            .And.NotContain("Healthy");
        page.Find("[data-testid=it-unresolved][data-relationship=s]").TextContent.Should().Contain("Strongly supported · not yet resolved");
        var legend = page.Find("[data-testid=it-legend]").TextContent;
        legend.IndexOf("Candidate", StringComparison.Ordinal).Should().BeLessThan(legend.IndexOf("Strongly supported", StringComparison.Ordinal));
        legend.IndexOf("Strongly supported", StringComparison.Ordinal).Should().BeLessThan(legend.IndexOf("Resolved", StringComparison.Ordinal));
        legend.Should().Contain("Not observed, healthy, contract-compatible or tested");
    }

    [Fact]
    public void ZeroEligibleOpenApiTargets_IsVisibleBeforeClicking_AndInterfacesRemainVisible()
    {
        var page = Render<IntegrationTopologyPage>();
        page.Find("[data-testid=it-discover]").TextContent.Should().Be("Discover OpenAPI interfaces…", "the action names what it reads");
        page.Find("[data-testid=it-discover]").GetAttribute("aria-describedby").Should().Be("it-discover-state");
        page.Find("[data-testid=it-discover-state]").TextContent.Should().StartWith("No eligible OpenAPI targets");
        page.Find("[data-testid=it-count-interfaces]").TextContent.Should().Be("3", "no OpenAPI target is not no interface evidence");
        page.Find("[data-testid=it-interfaces] summary").TextContent.Should().Contain("Interfaces (3)");
        _api.Discovers.Should().Be(0);
    }

    [Fact]
    public void EligibleOpenApiTargets_ShowComponentsAndHosts_AndStillNeedAnExplicitRun()
    {
        _api.View = Sample() with { Discovery = new([new("A", "a.example.test", "/swagger.json"), new("B", "b.example.test", "/openapi.json")], []) };
        var page = Render<IntegrationTopologyPage>();
        page.Find("[data-testid=it-discover-state]").TextContent.Should().Be("2 components · 2 hosts eligible");
        page.Find("[data-testid=it-discover]").Click();
        _api.Discovers.Should().Be(0, "eligibility never auto-runs discovery");
    }

    [Fact]
    public void InterfaceInventory_FiltersByTypeStatusAndSource_AndCountsEachInterfaceOnce()
    {
        var page = Render<IntegrationTopologyPage>();
        page.Find("[data-testid=it-interface-count]").TextContent.Should().StartWith("3 of 3 interfaces match");
        page.Find("[data-testid=it-interface-type]").Change("Rest");
        page.FindAll("[data-testid=it-interface-row]").Should().ContainSingle().Which.TextContent.Should().Contain("/api/reports").And.Contain("OpenAPI, Source Analysis, Browser observation",
            "one interface, several provenance entries");
        page.Find("[data-testid=it-interface-type]").Change("");
        page.Find("[data-testid=it-interface-source]").Change("BrowserObservation");
        page.FindAll("[data-testid=it-interface-row]").Should().ContainSingle();
        page.Find("[data-testid=it-interface-source]").Change("");
        page.Find("[data-testid=it-interface-status]").Change("review");
        page.FindAll("[data-testid=it-interface-row]").Should().BeEmpty();
        page.Find("[data-testid=it-interface-filter]").Should().NotBeNull();
        foreach (var id in new[] { "it-interface-filter", "it-interface-type", "it-interface-status", "it-interface-source" })
            page.Find($"label[for={id}]").TextContent.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void SourcesUsed_CountsProviderCategories_NotSnapshots()
    {
        var page = Render<IntegrationTopologyPage>();
        page.Find("[data-testid=it-sources] summary").TextContent.Should().Be("Sources used (1 of 2 evidence provider categories used)");
    }

    [Fact]
    public void RuntimeObservations_SayHowManyWereMergedAndFromWhere()
    {
        _api.View = Sample() with { Sources = [.. Sample().Sources, new(TopologySource.BrowserObservation, TopologySourceState.Used, "merged")] };
        var page = Render<IntegrationTopologyPage>();
        page.Find("[data-testid=it-count-observations]").TextContent.Should().Contain("runtime observations merged").And.Contain("Endpoint Discovery");
    }
}
