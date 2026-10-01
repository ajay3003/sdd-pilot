using BirkNext.Integrations;
using BirkNext.SourceArchitecture;
using BirkNext.Web.Components;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using BirkNext.Web.Tests.Integration;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// Source integrations section (Target Environment → Integrations): a generic title, model-derived disjoint counts, filters, source/channel/
/// capture/mapping columns, source evidence kept apart from runtime evidence, explicit confirmation of source suggestions, and the same UI for
/// Debezium CDC, a custom CDC publisher, HTTP and GraphQL — no source system or technology is special-cased.
/// </summary>
public sealed class SourceIntegrationsUiTests : BunitContext
{
    private readonly FakeIntegrationCatalogApi _api = new() { Catalog = M2lbFixture.Catalog() };
    private static readonly Guid Snapshot = Guid.Parse("11111111-2222-3333-4444-555555555555");

    public SourceIntegrationsUiTests()
    {
        Services.AddSingleton<IIntegrationCatalogApiService>(_api);
        Services.AddSingleton(new IntegrationMappingEvidenceSession());
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private IRenderedComponent<IntegrationsPane> Open() =>
        Render<IntegrationsPane>(p => p.Add(c => c.Profile, new FrontendAnalysisProfile { Id = "dev", Name = "Dev", EnvironmentType = FrontendEnvironmentType.Development }));

    private static SourceIntegrationCandidate Candidate(string channel, IntegrationPattern pattern, IntegrationTransport transport, string? capture, string? consumer,
        ArchitectureEvidenceState evidence = ArchitectureEvidenceState.Inferred, string? producer = null, string? caller = null, string? server = null) => new()
    {
        Id = $"{transport}:{channel}→{consumer}".ToLowerInvariant(), SourceSnapshotId = Snapshot, ChannelName = channel, Pattern = pattern, Transport = transport, CaptureTechnology = capture,
        ConsumerCandidate = consumer, ConsumerComponentId = consumer is null ? null : $"component:{consumer}", ConsumerEvidence = consumer is null ? ArchitectureEvidenceState.Unresolved : evidence,
        ProducerComponent = producer, CallerTechnology = caller, ServerTechnology = server, EvidenceState = evidence, SourceSystem = producer,
        Evidence =
        [
            new("Channel", channel, ArchitectureEvidenceState.Confirmed, ArchitectureEvidenceKind.Configuration, "Shop/Worker/appsettings.json", 3, "EventHub:EventHubNames", "architecture-bridge", "Configuration names this channel."),
            .. consumer is null ? Array.Empty<SourceFieldEvidence>() : [new SourceFieldEvidence("Consumer candidate", consumer, evidence, ArchitectureEvidenceKind.ApplicationSource, "Shop/Worker/Program.cs", 12, "EventProcessorClient", "architecture-bridge", $"{consumer} reads this channel in source.")],
        ],
    };

    private static SourceIntegrationsReport Report(IntegrationCatalog catalog, IReadOnlyList<SourceIntegrationCandidate> candidates, Func<IntegrationDefinition, SourceIntegrationMatch?> match, params string[] unconfigured) => new()
    {
        EnvironmentId = "dev", SourceAnalysis = SourceDiscoveryStatus.Partial, GeneratedAt = DateTimeOffset.UtcNow,
        Discovery = new() { SourceSnapshotId = Snapshot, SourceFingerprint = new string('c', 64), ArchiveName = "source.zip", AnalyzedAt = DateTimeOffset.UtcNow, Status = SourceDiscoveryStatus.Partial, Candidates = [.. candidates] },
        Matches = [.. catalog.Integrations.Select(match).OfType<SourceIntegrationMatch>()], UnconfiguredCandidateIds = [.. unconfigured],
    };

    /// <summary>The M2LB catalog with every topic found in source as Debezium CDC; the unassigned Kommune topic gets a source suggestion.</summary>
    private void UseM2lbReport()
    {
        _api.SourceReport = catalog =>
        {
            var candidates = catalog.Integrations.Select(i => Candidate(i.EndpointOrTopic!, IntegrationPattern.ChangeDataCapture, IntegrationTransport.EventHub, "Debezium",
                i.Consumer.DisplayName is null && i.Id.EndsWith("dbo.Kommune") ? "PersonBiRKAdapter.Worker" : null)).ToList();
            return Report(catalog, candidates, i =>
            {
                var c = candidates.First(x => x.ChannelName == i.EndpointOrTopic);
                return c.ConsumerCandidate is not null && i.Consumer.DisplayName is null
                    ? new() { IntegrationId = i.Id, State = SourceMatchState.SourceSuggestion, CandidateId = c.Id, SourceConsumer = c.ConsumerCandidate, SourceConsumerEvidence = c.ConsumerEvidence, Detail = $"Source analysis suggests {c.ConsumerCandidate}. The mapping has not been confirmed." }
                    : new() { IntegrationId = i.Id, State = SourceMatchState.Supported, CandidateId = c.Id, Detail = "Supported." };
            });
        };
    }

    private static AngleSharp.Dom.IElement Row(IRenderedComponent<IntegrationsPane> cut, string idSuffix) => cut.FindAll("[data-testid=ip-row]").Single(r => r.GetAttribute("data-integration-id")!.EndsWith(idSuffix));

    [Fact]
    public void TheSectionIsGeneric_NoSourceSystemOrTechnologyInItsTitle()
    {
        var cut = Open();
        cut.Find("[data-testid=ip-sources-title]").TextContent.Should().Be("Source integrations");
        cut.Find("[data-testid=ip-sources-subtitle]").TextContent.Should().Be("Source-to-consumer integrations detected or configured from source analysis.");
        cut.FindAll("h3").Select(h => h.TextContent).Should().NotContain(h => h.Contains("Debezium") || h.Contains("BIRK CDC"));
        cut.FindAll("[data-testid=ip-group]").Should().BeEmpty("the configured group name is no longer a section title");
        cut.FindAll("[data-testid=ip-table] thead th").Select(h => h.TextContent.Trim()).Should().Equal("Source", "Channel", "Producer / capture", "Consumer mapping", "Configuration", "Review", "Actions");
    }

    [Fact]
    public void CountsAreModelDerivedAndDisjoint()
    {
        var cut = Open();
        var business = SourceIntegrationsPresentation.Business(_api.Catalog);
        Int(cut, "ip-sources-count").Should().Be(business.Count);
        var mapping = cut.FindAll("[data-testid=ip-sources-mapping] [data-category]").ToDictionary(e => e.GetAttribute("data-category")!, e => int.Parse(e.TextContent.Split(' ', StringSplitOptions.RemoveEmptyEntries).Last()));
        mapping.Should().BeEquivalentTo(new Dictionary<string, int> { ["Confirmed"] = 1, ["Suggested"] = 8, ["NeedsConfirmation"] = 0, ["Unassigned"] = 7 });
        mapping.Values.Sum().Should().Be(business.Count, "mapping categories never overlap");
        cut.FindAll("[data-testid=ip-row]").Select(r => r.GetAttribute("data-mapping-category")).GroupBy(c => c).ToDictionary(g => g.Key!, g => g.Count())
            .Should().BeEquivalentTo(mapping.Where(m => m.Value > 0).ToDictionary());
    }

    private static int Int(IRenderedComponent<IntegrationsPane> cut, string id) => int.Parse(cut.Find($"[data-testid={id}]").TextContent.Trim());

    [Fact]
    public void ConfirmedSuggestedAndUnassignedLiveNextToTheConsumer_WithShortConfigurationAndReview()
    {
        var cut = Open();
        Row(cut, "dbo.Person").QuerySelector("[data-testid=ip-row-mapping]")!.TextContent.Should().Be("Confirmed");
        Row(cut, "dbo.Person").QuerySelector("[data-testid=ip-row-configuration]")!.TextContent.Should().Be("Ready");
        Row(cut, "dbo.Tiltak").QuerySelector("[data-testid=ip-row-mapping]")!.TextContent.Should().Be("Suggested");
        Row(cut, "dbo.Tiltak").QuerySelector("[data-testid=ip-row-configuration]")!.TextContent.Should().Be("Needs confirmation");
        var unassigned = Row(cut, "dbo.Kommune");
        unassigned.QuerySelector("[data-testid=ip-row-consumer]")!.TextContent.Should().Contain("Not assigned");
        unassigned.QuerySelector("[data-testid=ip-row-mapping]")!.TextContent.Should().Be("Unassigned");
        unassigned.QuerySelector("[data-testid=ip-row-mapping]")!.GetAttribute("title").Should().Be("No consumer mapping has been established.");
        Row(cut, "dbo.Tiltak").QuerySelector("[data-testid=ip-row-readiness] summary")!.TextContent.Should().Be("Why partial?");
        cut.Markup.Should().NotContain("Failed");
    }

    [Fact]
    public void SourceColumnAndChannelColumnAreGeneric()
    {
        var cut = Open();
        var person = Row(cut, "dbo.Person");
        person.QuerySelector("[data-testid=ip-row-source]")!.TextContent.Should().Be("Person");
        person.QuerySelector("th")!.TextContent.Should().Contain("BIRK · BirkM2LB.dbo.Person");
        person.QuerySelector("[data-testid=ip-row-channel]")!.TextContent.Should().Contain("Event Hub").And.Contain("…dbo.Person");
        person.QuerySelector("[data-testid=ip-row-producer]")!.TextContent.Should().Contain("Not detected", "without source analysis the pattern is not invented");
    }

    [Fact]
    public void WithoutSourceAnalysisTheSectionSaysNotAnalyzed()
    {
        var cut = Open();
        cut.Find("[data-testid=ip-source-analysis-state]").TextContent.Should().Be("Source analysis: Not analyzed");
        cut.Find("[data-testid=ip-open-source-analysis]").GetAttribute("href").Should().Be("/source-analysis");
        cut.FindAll("[data-testid=ip-row-source-note]").Should().BeEmpty();
    }

    [Fact]
    public void DebeziumIsTheCaptureTechnology_CdcThePattern_AndDetectedValuesAreMarked()
    {
        UseM2lbReport();
        var cut = Open();
        cut.Find("[data-testid=ip-source-analysis-state]").TextContent.Should().Be("Source analysis: Partial");
        var producer = Row(cut, "dbo.Person").QuerySelector("[data-testid=ip-row-producer]")!;
        producer.TextContent.Should().Contain("CDC").And.Contain("Debezium").And.Contain("detected from source");
        producer.QuerySelector(".ip-detected")!.GetAttribute("title").Should().Be("Detected from source");
    }

    [Fact]
    public void FiltersNarrowByMappingPatternTransportAndSource_AndClearResetsAll()
    {
        UseM2lbReport();
        var cut = Open();
        cut.Find("[data-testid=ip-filter-mapping]").Change(nameof(MappingCategory.Suggested));
        cut.FindAll("[data-testid=ip-row]").Should().HaveCount(8).And.OnlyContain(r => r.GetAttribute("data-mapping-category") == "Suggested");
        cut.Find("[data-testid=ip-filter-mapping]").Change("");
        cut.FindAll("[data-testid=ip-filter-pattern] option").Select(o => o.GetAttribute("value")).Should().Equal("", "CDC");
        cut.Find("[data-testid=ip-filter-pattern]").Change("CDC");
        cut.FindAll("[data-testid=ip-row]").Should().HaveCount(16);
        cut.Find("[data-testid=ip-filter-transport]").Change(nameof(IntegrationTransport.EventHub));
        cut.Find("[data-testid=ip-filter-source]").Change("BIRK");
        cut.FindAll("[data-testid=ip-row]").Should().HaveCount(16);
        cut.Find("[data-testid=ip-active-filter]").TextContent.Should().Contain("Pattern: CDC").And.Contain("Transport: Event Hub").And.Contain("Source: BIRK");
        cut.Find("[data-testid=ip-clear-filters]").Click();
        cut.FindAll("[data-testid=ip-active-filter]").Should().BeEmpty();
        foreach (var id in new[] { "ip-filter-mapping", "ip-filter-pattern", "ip-filter-transport", "ip-filter-source" })
            cut.Find($"[data-testid={id}]").Closest("label").Should().NotBeNull("every filter has a visible label");
    }

    [Fact]
    public void RowDetailsShowProvenance_AndKeepSourceAndRuntimeEvidenceApart()
    {
        UseM2lbReport();
        var cut = Open();
        Row(cut, "dbo.Kommune").QuerySelector("[data-testid=ip-row-view]")!.Click();
        var detail = cut.Find("[data-testid=ip-source-detail]");
        detail.QuerySelector("[data-testid=ip-detail-pattern]")!.TextContent.Should().Be("Change Data Capture");
        detail.QuerySelector("[data-testid=ip-detail-capture]")!.TextContent.Should().Be("Debezium");
        detail.QuerySelector("[data-testid=ip-detail-candidate]")!.TextContent.Should().Be("PersonBiRKAdapter.Worker · Inferred");
        var source = detail.QuerySelector("[data-testid=ip-detail-source-evidence]")!.TextContent;
        source.Should().Contain("Detected from source").And.Contain("Shop/Worker/Program.cs:12").And.Contain("architecture-bridge");
        var runtime = detail.QuerySelector("[data-testid=ip-detail-runtime-evidence]")!.TextContent;
        runtime.Should().Contain("Not assessed here").And.NotContain("Detected from source");
    }

    [Fact]
    public void ASourceSuggestionIsConfirmedOnlyThroughTheExplicitPanel()
    {
        UseM2lbReport();
        var cut = Open();
        var row = Row(cut, "dbo.Kommune");
        row.QuerySelector("[data-testid=ip-row-source-note]")!.TextContent.Should().Be("Source suggests PersonBiRKAdapter.Worker");
        row.QuerySelector("[data-testid=ip-row-mapping]")!.TextContent.Should().Be("Unassigned", "a suggestion never changes the mapping state");
        row.QuerySelector("[data-testid=ip-review-suggestion]")!.Click();
        var panel = cut.Find("[data-testid=ip-confirm-panel]");
        panel.GetAttribute("role").Should().Be("group");
        cut.Find("[data-testid=ip-confirm-consumer]").TextContent.Should().Be("PersonBiRKAdapter.Worker");
        cut.Find("[data-testid=ip-confirm-evidence]").TextContent.Should().Contain("Inferred").And.Contain("Shop/Worker/Program.cs:12");
        _api.Saved.Should().BeEmpty("reviewing a suggestion saves nothing");
        cut.Find("[data-testid=ip-confirm-mapping]").Click();
        cut.WaitForAssertion(() => _api.Saved.Should().ContainSingle());
        var consumer = _api.Saved.Single().Consumer;
        (consumer.DisplayName, consumer.MappingState, consumer.MappingSourceSnapshotId).Should().Be(("PersonBiRKAdapter.Worker", ConsumerMappingState.Confirmed, Snapshot));
        consumer.MappingEvidence.Should().Contain("Confirmed by a person");
    }

    [Fact]
    public void CancellingTheConfirmationSavesNothing()
    {
        var cut = Open();
        Row(cut, "dbo.Tiltak").QuerySelector("[data-testid=ip-inline-confirm]")!.Click();
        cut.Find("[data-testid=ip-confirm-cancel]").Click();
        cut.FindAll("[data-testid=ip-confirm-panel]").Should().BeEmpty();
        _api.Saved.Should().BeEmpty();
    }

    [Fact]
    public void AConflictAndChangedEvidenceAreVisible_AndNothingIsOverwritten()
    {
        _api.SourceReport = catalog =>
        {
            var person = catalog.Integrations.Single(i => i.Id.EndsWith("dbo.Person"));
            var barn = catalog.Integrations.Single(i => i.Id.EndsWith("dbo.Tiltak"));
            var candidates = new List<SourceIntegrationCandidate> { Candidate(person.EndpointOrTopic!, IntegrationPattern.ChangeDataCapture, IntegrationTransport.EventHub, "Custom publisher", "Tjeneste.Api"),
                Candidate(barn.EndpointOrTopic!, IntegrationPattern.ChangeDataCapture, IntegrationTransport.EventHub, "Custom publisher", "Tjeneste.Api") };
            return Report(catalog, candidates, i => i.Id == person.Id
                ? new() { IntegrationId = i.Id, State = SourceMatchState.Conflict, CandidateId = candidates[0].Id, SourceConsumer = "Tjeneste.Api", Detail = "Configured consumer Person Adapter (Confirmed) differs from the source's consumer candidate Tjeneste.Api. Needs review — the configured mapping is kept." }
                : i.Id == barn.Id ? new() { IntegrationId = i.Id, State = SourceMatchState.Supported, CandidateId = candidates[1].Id, SourceChanges = ["Capture technology: Debezium → Custom publisher"], Detail = "Supported." } : null);
        };
        var cut = Open();
        var person = Row(cut, "dbo.Person");
        person.QuerySelector("[data-testid=ip-row-source-note]")!.TextContent.Should().Be("Mapping conflict · Needs review");
        person.QuerySelector("[data-testid=ip-row-mapping]")!.TextContent.Should().Be("Confirmed", "the confirmed mapping is kept");
        Row(cut, "dbo.Tiltak").QuerySelector("[data-testid=ip-row-source-note]")!.TextContent.Should().Be("Source evidence changed");
        Row(cut, "dbo.Tiltak").QuerySelector("[data-testid=ip-row-producer]")!.TextContent.Should().Contain("Custom publisher").And.NotContain("Debezium");
        _api.Saved.Should().BeEmpty();
    }

    [Fact]
    public void DiscoverFromSourceShowsACompactSourceOnlyResult()
    {
        UseM2lbReport();
        var cut = Open();
        cut.Find("[data-testid=ip-discover]").TextContent.Should().Be("Discover from source");
        cut.Find("[data-testid=ip-discover]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=ip-discovery-report]").GetAttribute("role").Should().Be("status"));
        _api.Calls.Should().Contain("discover-source-integrations");
        var report = cut.Find("[data-testid=ip-discovery-report]").TextContent;
        report.Should().Contain("16 business integration candidate(s) detected in source (16 Change Data Capture)")
            .And.Contain("1 confirmed · 8 suggested · 0 need confirmation · 7 unassigned").And.Contain("No Event Hub, Service Bus, database or external endpoint was contacted");
        _api.Saved.Should().BeEmpty("discovery never writes the catalog");
    }

    [Fact]
    public void TechnicalChannelsAreGeneric_CollapsedAndExcluded()
    {
        var cut = Open();
        var toggle = cut.Find("[data-testid=ip-technical-toggle]");
        toggle.GetAttribute("aria-expanded").Should().Be("false");
        toggle.TextContent.Should().Contain("Technical integration channels").And.Contain("5 excluded");
        cut.Find("[data-testid=ip-technical-subtitle]").TextContent.Should().Be("Platform/support channels excluded from business integration counts.");
        cut.FindAll("[data-testid=ip-technical-row]").Should().HaveCount(5);
        cut.Find("[data-testid=ip-technical-list] thead").TextContent.Should().Contain("Transport").And.Contain("Reason excluded");
        Int(cut, "ip-sources-count").Should().Be(16, "technical channels never count as business integrations");
    }

    [Fact]
    public void ACustomCdcPublisherOverServiceBusRendersWithoutDebeziumTerminology()
    {
        _api.Catalog = new IntegrationCatalog
        {
            EnvironmentId = "dev",
            Integrations = [new() { Id = "dev:servicebus:orders", EnvironmentId = "dev", DisplayName = "Order changes", Kind = IntegrationKind.ServiceBus, EndpointOrTopic = "orders.changes",
                SourceSystem = "LegacyDb", SourceResource = "LegacyDb.dbo.Orders", Producer = "Legacy change publisher", Consumer = new() { DisplayName = "Orders Worker", MappingState = ConsumerMappingState.Suggested } }],
        };
        _api.SourceReport = catalog =>
        {
            var c = Candidate("orders.changes", IntegrationPattern.ChangeDataCapture, IntegrationTransport.ServiceBusTopic, "Custom publisher", "Orders.Worker", producer: "Legacy.ChangePublisher");
            return Report(catalog, [c], i => new() { IntegrationId = i.Id, State = SourceMatchState.Supported, CandidateId = c.Id, SourceConsumer = "Orders.Worker", Detail = "Supported." });
        };
        var cut = Open();
        var row = cut.Find("[data-testid=ip-row]");
        row.QuerySelector("[data-testid=ip-row-source]")!.TextContent.Should().Be("Orders");
        row.QuerySelector("[data-testid=ip-row-channel]")!.TextContent.Should().Contain("Service Bus topic").And.Contain("orders.changes");
        row.QuerySelector("[data-testid=ip-row-producer]")!.TextContent.Should().Contain("CDC").And.Contain("Custom publisher");
        cut.Find("[data-testid=ip-sources]").TextContent.Should().NotContain("Debezium");
    }

    [Fact]
    public void AnHttpIntegrationWithoutCdcRendersInTheSameTable()
    {
        _api.Catalog = new IntegrationCatalog
        {
            EnvironmentId = "dev",
            Integrations = [new() { Id = "dev:http:partner", EnvironmentId = "dev", DisplayName = "Partner orders API", Kind = IntegrationKind.HttpApi, EndpointOrTopic = "Orders:BaseUrl",
                SourceSystem = "Partner gateway", Consumer = new() { DisplayName = "Orders API", MappingState = ConsumerMappingState.Confirmed } }],
        };
        _api.SourceReport = catalog =>
        {
            var c = Candidate("Orders:BaseUrl", IntegrationPattern.ApiCall, IntegrationTransport.Http, null, "Orders.Api", ArchitectureEvidenceState.StronglySupported, producer: "Partner.Gateway");
            return Report(catalog, [c], i => new() { IntegrationId = i.Id, State = SourceMatchState.Supported, CandidateId = c.Id, SourceConsumer = "Orders.Api", Detail = "Supported." });
        };
        var cut = Open();
        var row = cut.Find("[data-testid=ip-row]");
        row.QuerySelector("[data-testid=ip-row-source]")!.TextContent.Should().Be("Partner orders API");
        row.QuerySelector("[data-testid=ip-row-channel]")!.TextContent.Should().Contain("HTTP");
        row.QuerySelector("[data-testid=ip-row-producer]")!.TextContent.Should().Contain("API").And.Contain("Partner.Gateway");
        row.QuerySelector("[data-testid=ip-row-mapping]")!.TextContent.Should().Be("Confirmed");
        cut.FindAll("[data-testid=ip-filter-pattern] option").Select(o => o.GetAttribute("value")).Should().Equal("", "API");
        cut.Find("[data-testid=ip-sources]").TextContent.Should().NotContain("CDC").And.NotContain("Debezium").And.NotContain("Event Hub");
    }

    [Fact]
    public void AGraphQlCandidateKeepsStrawberryShakeAndHotChocolateAsTechnologies()
    {
        _api.SourceReport = catalog =>
        {
            var gql = Candidate("/graphql", IntegrationPattern.ApiCall, IntegrationTransport.GraphQl, null, "Catalog.Graph", ArchitectureEvidenceState.StronglySupported, producer: "Shop.Web",
                caller: "Strawberry Shake", server: "Hot Chocolate (GraphQL server)") with { SourceEntity = "Shop.Web", SourceSystemType = "Application component" };
            return Report(catalog, [gql], _ => null, gql.Id);
        };
        var cut = Open();
        cut.Find("[data-testid=ip-unconfigured-toggle]").GetAttribute("aria-expanded").Should().Be("false");
        var row = cut.Find("[data-testid=ip-unconfigured-row]");
        row.TextContent.Should().Contain("Shop.Web").And.Contain("GraphQL").And.Contain("/graphql").And.Contain("Strawberry Shake").And.Contain("Server: Hot Chocolate").And.Contain("Catalog.Graph").And.Contain("Suggested from source");
        row.GetAttribute("data-pattern").Should().Be(nameof(IntegrationPattern.ApiCall));
        Int(cut, "ip-sources-count").Should().Be(16, "an unconfigured candidate is never counted as a configured business integration");
    }
}
