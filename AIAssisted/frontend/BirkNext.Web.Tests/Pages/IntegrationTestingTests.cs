using BirkNext.ContractTesting;
using BirkNext.Integrations;
using BirkNext.IntegrationSuites;
using BirkNext.IntegrationTopology;
using BirkNext.Web.Models;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using I = BirkNext.Web.Services.IntegrationTestingPresentation;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// Technical Testing → Integration Testing: one entry over Integration Topology, the Integrations catalog, declared Integration Journeys,
/// Integration Test Suites and Integration Quality Review. Counts name their entity; every handoff carries the owning module's exact identity
/// (catalog integration id, declared journey id, suite id); suites are listed for the project's own source snapshot only; discovered
/// connections, declared journeys, prepared suites and static reviews are never shown as passed runs. The journey editor builds the chain from
/// discovered systems and connections (and still by typing), with reordering; Integration Test Suites takes a journey as context only.
/// </summary>
public sealed class IntegrationTestingTests : BunitContext
{
    private static readonly Guid OwnSnapshot = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private readonly DeclaredJourneyApiFake _journeys = new();

    private static TopologyComponent C(string id, string name) => new() { Id = id, Name = name, Kind = TopologyComponentKind.Application };
    private static TopologyConnection Conn(string from, string to, TopologyRelationshipState state, string? catalogId = null) => new()
    {
        Id = $"{from}->{to}", ProducerComponentId = from, ConsumerComponentId = to, Relationship = state, Type = TopologyInterfaceType.Messaging,
        Provenance = catalogId is null ? [new(TopologySource.SourceAnalysis, "src", "source")] : [new(TopologySource.IntegrationCatalog, catalogId, "catalog")],
    };
    private static IntegrationTopologyView Topology() => new()
    {
        EnvironmentId = "qa", Components = [C("a", "Orders.Api"), C("b", "order-events"), C("c", "Orders.Notifier")],
        Connections = [Conn("a", "b", TopologyRelationshipState.Resolved, "qa:sb:orders"), Conn("b", "c", TopologyRelationshipState.Candidate)],
    };
    private static IntegrationSuiteSummary Suite(Guid snapshot, SuiteStatus status, string name = "Orders flow") =>
        new(Guid.NewGuid(), "k", 2, "api", name, snapshot, "orders.zip", DateTimeOffset.UtcNow, status, 7, 3, 1);

    private static I.Facts Facts(int journeys = 0, IReadOnlyList<IntegrationSuiteSummary>? suites = null, int reviews = 0, bool topology = true) => new(
        "library", "Library", "qa", "QA", topology ? Topology() : null, [new IntegrationDefinition { Id = "qa:sb:orders", DisplayName = "Orders topic", Enabled = true }],
        [.. Enumerable.Range(1, journeys).Select(i => new DeclaredIntegrationJourney { ProjectKey = "library", JourneyId = $"j{i}", DisplayName = $"Journey {i}", Components = ["Orders.Api", "Orders.Notifier"], Revision = 1 })],
        suites ?? [], 0, [.. Enumerable.Range(1, reviews).Select(_ => new IntegrationReviewRunSummary(Guid.NewGuid(), DateTimeOffset.UtcNow, IntegrationReviewOutcome.Completed, 2, 0))], []);

    // ---- presentation --------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Counts_name_the_entity_they_count_and_separate_connections_needing_review()
    {
        var counts = I.Counts(Facts(journeys: 1)).ToDictionary(c => c.Entity);
        counts.Keys.Should().Equal("Systems and components", "Connections", "Configured integrations", "Journeys", "Test suites", "Quality reviews");
        counts["Connections"].Value.Should().Be(2);
        counts["Connections"].Detail.Should().Be("1 need review (candidate or expected, not resolved)");
        counts["Configured integrations"].Value.Should().Be(1);
    }

    [Fact]
    public void The_next_step_follows_stored_state()
    {
        I.Next(I.Facts.Empty).Href.Should().Be("project-import");
        I.Next(Facts()).Label.Should().Be("Explore discovered integrations");
        I.Next(Facts(topology: false) with { Integrations = [] }).Label.Should().Be("Create journey", "nothing was discovered: declare by hand");
        I.Next(Facts(journeys: 1)).Should().Match<I.NextAction>(n => n.Label == "Prepare test suite" && n.Href == "technical-testing/integrations?stage=prepare&journey=project.library%3Aj1");
        var blocked = Suite(OwnSnapshot, SuiteStatus.Blocked);
        I.Next(Facts(journeys: 1, suites: [blocked])).Should().Match<I.NextAction>(n => n.Label == "Resolve readiness" && n.Href == $"integration-suites?suite={blocked.Id}&area=readiness");
        I.Next(Facts(journeys: 1, suites: [Suite(OwnSnapshot, SuiteStatus.ReadyToExecute)], reviews: 1)).Label.Should().Be("Review verification results");
        I.Next(Facts(journeys: 1, suites: [Suite(OwnSnapshot, SuiteStatus.ReadyToExecute)])).Label.Should().Be("Review journey");
    }

    [Fact]
    public void Handoffs_use_exact_identities_only()
    {
        var topology = Topology();
        I.CatalogIntegrationId(topology.Connections[0], [new IntegrationDefinition { Id = "qa:sb:orders" }]).Should().Be("qa:sb:orders");
        I.CatalogIntegrationId(topology.Connections[0], []).Should().BeNull("a catalog reference that is no configured integration links nowhere");
        I.CatalogIntegrationId(topology.Connections[1], [new IntegrationDefinition { Id = "src" }]).Should().BeNull("only an Integrations-catalog provenance names an integration");
        I.DeclaredJourneyHref("project.library:order-flow").Should().Be("integration-journeys/declare?journey=order-flow");
        I.DeclaredJourneyHref("skolenaervaer:rapportmottak").Should().BeNull("a registered test-pack journey has no declaration editor");
        I.IqrHref("qa:sb:orders").Should().Be("integration-quality-review?view=review&integration=qa%3Asb%3Aorders");
    }

    [Fact]
    public void Evidence_labels_never_read_as_a_pass()
    {
        I.RelationshipLabel(TopologyRelationshipState.Candidate).Should().Be("Candidate — needs review");
        I.RelationshipLabel(TopologyRelationshipState.Observed).Should().Be("Observed at runtime");
        I.SuiteLabel(SuiteStatus.ReadyToExecute).Should().Be("Ready to execute");
        I.ReviewLabel(IntegrationReviewOutcome.Completed, 0).Should().Be("Completed without findings");
        I.ReviewLabel(IntegrationReviewOutcome.NothingAssessed, 0).Should().Be("Nothing assessed");
    }

    // ---- page ----------------------------------------------------------------------------------------------------------------------

    private IRenderedComponent<TechnicalTestingIntegrations> Open(string query = "", List<IntegrationSuiteSummary>? suites = null)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton(BirkNext.Web.Tests.Services.WorkspaceSnapshots.Projection(BirkNext.Web.Tests.Services.WorkspaceSnapshots.Loaded("Imported", "library", "Library")).Object);
        var context = new Mock<IFrontendAnalysisContextFactory>();
        context.Setup(c => c.GetActiveContextAsync()).ReturnsAsync(new FrontendAnalysisContext { ActiveProfile = new FrontendAnalysisProfile { Id = "qa", Name = "QA", TargetUrl = "https://qa.example" } });
        Services.AddSingleton(context.Object);
        var topology = new Mock<IIntegrationTopologyApiService>();
        topology.Setup(t => t.GetAsync("qa", It.IsAny<IReadOnlyList<TopologyObservation>>(), It.IsAny<CancellationToken>())).ReturnsAsync((Topology(), (string?)null));
        Services.AddSingleton(topology.Object);
        var catalog = new Mock<IIntegrationCatalogApiService>();
        catalog.Setup(c => c.GetCatalogAsync(It.IsAny<FrontendAnalysisProfile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IntegrationCatalog { Integrations = [new() { Id = "qa:sb:orders", DisplayName = "Orders topic", Enabled = true }, new() { Id = "off", DisplayName = "Disabled", Enabled = false }] });
        catalog.Setup(c => c.HistoryAsync("qa", It.IsAny<CancellationToken>())).ReturnsAsync([new IntegrationReviewRunSummary(Guid.NewGuid(), DateTimeOffset.UtcNow, IntegrationReviewOutcome.CompletedWithLimitations, 3, 2)]);
        Services.AddSingleton(catalog.Object);
        Services.AddSingleton<IDeclaredIntegrationJourneyApiService>(_journeys);
        var suiteApi = new Mock<IIntegrationSuiteApiService>();
        suiteApi.Setup(s => s.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync((suites ?? [], (string?)null));
        Services.AddSingleton(suiteApi.Object);
        var package = new BirkNext.Web.Services.ProjectEvidence.ProjectEvidencePackage("import-l", "Library", "library.zip", "sha", DateTimeOffset.UnixEpoch, OwnSnapshot, [],
            new Dictionary<WorkspaceArtifactType, int>(), 0, [], [], [], null);
        var evidence = new Mock<BirkNext.Web.Services.ProjectEvidence.IProjectEvidenceService>();
        evidence.Setup(e => e.Peek(It.IsAny<string?>())).Returns(new BirkNext.Web.Services.ProjectEvidence.ProjectEvidenceView("qa", "QA", null, package, [], [], [], []));
        Services.AddSingleton(evidence.Object);
        Services.GetRequiredService<NavigationManager>().NavigateTo("technical-testing/integrations" + query);
        var cut = Render<TechnicalTestingIntegrations>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=itx]").GetAttribute("aria-busy").Should().Be("false"));
        return cut;
    }

    [Fact]
    public void The_overview_shows_the_counts_one_next_step_and_four_linked_stages()
    {
        var cut = Open();
        cut.Find("[data-testid=twh-title]").TextContent.Should().Be("Integration Testing");
        cut.FindAll("[data-testid=itx-count]").Select(c => c.GetAttribute("data-entity")).Should().HaveCount(6);
        cut.Find("[data-testid=itx-count][data-entity=\"Configured integrations\"] strong").TextContent.Should().Be("1", "a disabled integration is not counted");
        cut.Find("[data-testid=itx-next-action]").TextContent.Should().Be("Explore discovered integrations");
        cut.FindAll("[data-testid=itx-stage] [data-testid=gsl-title-link]").Select(a => a.GetAttribute("href")).Should().Equal(
            "technical-testing/integrations?stage=discover", "technical-testing/integrations?stage=define", "technical-testing/integrations?stage=prepare", "technical-testing/integrations?stage=verify");
    }

    [Fact]
    public void Changing_the_stage_changes_the_content()
    {
        var cut = Open("?stage=discover");
        cut.FindAll("[data-testid=itx-connection]").Select(c => c.GetAttribute("data-state")).Should().Equal("Resolved", "Candidate");
        cut.Find("[data-testid=itx-connection-iqr]").GetAttribute("href").Should().Be("integration-quality-review?view=review&integration=qa%3Asb%3Aorders");
        cut.FindAll("[data-testid=itx-integration]").Should().ContainSingle();

        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo("technical-testing/integrations?stage=define");                      // what a stage link does
        cut.WaitForAssertion(() => cut.Find("[data-testid=itx]").GetAttribute("data-stage").Should().Be("define"));
        cut.FindAll("[data-testid=itx-connection]").Should().BeEmpty();
        cut.Find("[data-testid=itx-journeys-none]");

        nav.NavigateTo("technical-testing/integrations?stage=verify");
        cut.WaitForAssertion(() => cut.Find("[data-testid=itx-review]").TextContent.Should().Contain("Completed with limitations"));
        cut.Find("[data-testid=itx-runtime]").TextContent.Should().Contain("never counted as a passed run");
    }

    [Fact]
    public void A_journey_is_handed_to_test_suites_by_its_exact_id_and_suites_of_other_sources_are_not_shown()
    {
        var journey = _journeys.Seed("library", "Order flow", "Kari", ["Orders.Api", "order-events", "Orders.Notifier"]);
        var cut = Open($"?stage=prepare&journey={Uri.EscapeDataString(journey.Id)}", [Suite(OwnSnapshot, SuiteStatus.NeedsReview, "Own"), Suite(Guid.NewGuid(), SuiteStatus.ReadyToExecute, "Elsewhere")]);
        cut.Find("[data-testid=itx-prepare-journey-context]").TextContent.Should().Contain("Order flow").And.Contain("Orders.Api → order-events → Orders.Notifier");
        cut.Find("[data-testid=itx-open-suites]").GetAttribute("href").Should().Be($"integration-suites?journey={Uri.EscapeDataString(journey.Id)}");
        cut.FindAll("[data-testid=itx-suite]").Should().ContainSingle().Which.TextContent.Should().Contain("Own").And.Contain("Needs review");
        cut.Find("[data-testid=itx-other-suites]").TextContent.Should().Be("1 suite of other source snapshots are not shown.");
    }

    [Fact]
    public void A_journey_of_another_project_is_not_shown_as_context()
    {
        _journeys.Seed("payroll", "Salary", "Ola", ["Payroll", "Bank"]);
        var cut = Open("?stage=prepare&journey=project.payroll%3Asalary");
        cut.Find("[data-testid=itx-journey-unknown]");
        cut.FindAll("[data-testid=itx-open-suites]").Should().BeEmpty();
    }

    [Fact]
    public async Task The_topology_is_read_through_the_shared_resolver_not_only_a_DI_registration()
    {
        // The real app does not register IIntegrationTopologyApiService in DI (every topology page uses IntegrationTopologyApiService.Resolve).
        // Without a registration the loader must still try the topology and say what happened — never skip it silently.
        var services = new ServiceCollection();
        services.AddSingleton(BirkNext.Web.Tests.Services.WorkspaceSnapshots.Projection(BirkNext.Web.Tests.Services.WorkspaceSnapshots.Loaded("Imported", "library", "Library")).Object);
        var context = new Mock<IFrontendAnalysisContextFactory>();
        context.Setup(c => c.GetActiveContextAsync()).ReturnsAsync(new FrontendAnalysisContext { ActiveProfile = new FrontendAnalysisProfile { Id = "qa", Name = "QA" } });
        services.AddSingleton(context.Object);
        var facts = await new IntegrationTestingWorkspace(services.BuildServiceProvider()).LoadAsync();
        facts.Topology.Should().BeNull();
        facts.Errors.Should().Contain(e => e.StartsWith("Integration Topology"), "the topology read was attempted through the resolver and its outcome reported");
    }

    // ---- journey editor: systems and steps ---------------------------------------------------------------------------------------

    [Fact]
    public void The_journey_editor_builds_the_chain_from_discovered_systems_and_connections_and_reorders_it()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<IDeclaredIntegrationJourneyApiService>(_journeys);
        Services.AddSingleton(BirkNext.Web.Tests.Services.WorkspaceSnapshots.Projection(BirkNext.Web.Tests.Services.WorkspaceSnapshots.Loaded("Imported", "library", "Library")).Object);
        var context = new Mock<IFrontendAnalysisContextFactory>();
        context.Setup(c => c.GetActiveContextAsync()).ReturnsAsync(new FrontendAnalysisContext { ActiveProfile = new FrontendAnalysisProfile { Id = "qa", Name = "QA" } });
        Services.AddSingleton(context.Object);
        var topology = new Mock<IIntegrationTopologyApiService>();
        topology.Setup(t => t.GetAsync("qa", It.IsAny<IReadOnlyList<TopologyObservation>>(), It.IsAny<CancellationToken>())).ReturnsAsync((Topology(), (string?)null));
        Services.AddSingleton(topology.Object);
        var page = Render<DeclaredIntegrationJourneys>();
        page.WaitForAssertion(() => page.Find("[data-testid=ijd-page]").GetAttribute("aria-busy").Should().Be("false"));

        page.Find("[data-testid=ijd-steps-none]");
        page.Find("[data-testid=ijd-add-system][data-name=\"Orders.Notifier\"]").Click();
        page.FindAll("[data-testid=ijd-connection]").Select(c => c.GetAttribute("data-state")).Should().Equal("Resolved", "Candidate");
        page.FindAll("[data-testid=ijd-add-connection]")[0].Click();                          // Orders.Api → order-events
        Steps().Should().Equal("Orders.Notifier", "Orders.Api", "order-events");
        page.FindAll("[data-testid=ijd-step-down]")[0].Click();
        page.FindAll("[data-testid=ijd-step-down]")[1].Click();
        Steps().Should().Equal("Orders.Api", "order-events", "Orders.Notifier");
        page.FindAll("[data-testid=ijd-step-up]")[0].HasAttribute("disabled").Should().BeTrue();
        page.Find("[data-testid=ijd-system-search]").Input("notif");
        page.FindAll("[data-testid=ijd-add-system]").Select(b => b.GetAttribute("data-name")).Should().Equal("Orders.Notifier");

        page.Find("[data-testid=ijd-name]").Input("Order flow");
        page.Find("[data-testid=ijd-declared-by]").Input("Kari");
        page.Find("[data-testid=ijd-save]").Click();
        _journeys.Sent.Should().ContainSingle().Which.Components.Should().Equal("Orders.Api", "order-events", "Orders.Notifier");

        List<string> Steps() => [.. page.FindAll("[data-testid=ijd-step] > span").Select(s => s.TextContent[(s.TextContent.IndexOf(". ") + 2)..])];
    }

    [Fact]
    public void Without_discovered_systems_the_journey_can_still_be_typed()
    {
        Services.AddSingleton<IDeclaredIntegrationJourneyApiService>(_journeys);
        Services.AddSingleton(BirkNext.Web.Tests.Services.WorkspaceSnapshots.Projection(BirkNext.Web.Tests.Services.WorkspaceSnapshots.Loaded("Imported", "library", "Library")).Object);
        var page = Render<DeclaredIntegrationJourneys>();
        page.WaitForAssertion(() => page.Find("[data-testid=ijd-page]").GetAttribute("aria-busy").Should().Be("false"));
        page.Find("[data-testid=ijd-no-topology]");
        page.Find("[data-testid=ijd-components]").Input("A\nB");
        page.FindAll("[data-testid=ijd-step]").Should().HaveCount(2);
    }
}
