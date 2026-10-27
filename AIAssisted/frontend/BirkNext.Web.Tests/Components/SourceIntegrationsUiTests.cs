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
/// Target Environment → Integration Catalog with Source Analysis as the owner of source evidence: generic section titles, model-derived disjoint
/// counts, compact filters, source/channel/capture/mapping columns, a link to Source Analysis instead of repeated source evidence, explicit
/// confirmation of source suggestions, secondary suggestions that are added — never confirmed — on request, and the same UI for Debezium CDC,
/// a custom CDC publisher, HTTP and GraphQL — no source system or technology is special-cased.
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
    public void TheSectionsAreGeneric_NoSourceSystemOrTechnologyInTheirTitles()
    {
        var cut = Open();
        cut.FindAll("h3").Select(h => h.TextContent.Trim()).Should().StartWith(["Configured integrations", "Integration candidates from Source Analysis", "Source evidence"]);
        cut.FindAll("h3").Select(h => h.TextContent).Should().NotContain(h => h.Contains("Debezium") || h.Contains("BIRK CDC"));
        cut.FindAll("[data-testid=ip-group]").Should().BeEmpty("the configured group name is no longer a section title");
        cut.FindAll("[data-testid=ip-table] thead th").Select(h => h.TextContent.Trim()).Should().Equal("Integration", "Type", "Producer / source", "Consumer / destination", "Configuration", "Source evidence", "Runtime evidence", "Actions");
    }

    [Fact]
    public void CountsAreModelDerivedAndDisjoint()
    {
        var cut = Open();
        var business = SourceIntegrationsPresentation.Business(_api.Catalog);
        Int(cut, "ip-summary-configured").Should().Be(business.Count);
        var mapping = cut.FindAll("[data-testid=ip-row]").Select(r => r.GetAttribute("data-mapping-category")).GroupBy(c => c).ToDictionary(g => g.Key!, g => g.Count());
        mapping.Should().BeEquivalentTo(new Dictionary<string, int> { ["Confirmed"] = 1, ["Suggested"] = 8, ["Unassigned"] = 7 });
        mapping.Values.Sum().Should().Be(business.Count, "mapping categories never overlap");
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
        Row(cut, "dbo.Tiltak").QuerySelector("[data-testid=ip-row-iqr]")!.GetAttribute("href").Should().Be("/integration-quality-review", "review readiness is Integration Quality Review's");
        cut.FindAll("[data-testid=ip-row-readiness]").Should().BeEmpty("review readiness is not a configuration column");
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
    public void WithoutSourceAnalysis_SuggestionsAreUnavailable_AndConfiguredIntegrationsStillWork()
    {
        var cut = Open();
        cut.Find("[data-testid=ip-summary-suggestions]").TextContent.Should().Be("Unavailable");
        cut.Find("[data-testid=ip-suggestions-unavailable]").TextContent.Should().Contain("Unavailable.").And.Contain("No source snapshot is linked").And.Contain("work without it");
        cut.Find("[data-testid=ip-open-source-analysis]").GetAttribute("href").Should().Be("/source-analysis");
        cut.Find("[data-testid=ip-source-summary]").TextContent.Should().Be("No source snapshot is linked to this environment.");
        cut.FindAll("[data-testid=ip-review-suggestions]").Should().BeEmpty();
        cut.FindAll("[data-testid=ip-row-source-note]").Should().BeEmpty();
        cut.FindAll("[data-testid=ip-row]").Should().HaveCount(16, "configured integrations work without Source Analysis");
    }

    [Fact]
    public void DebeziumIsTheCaptureTechnology_CdcThePattern_AndDetectedValuesAreMarked()
    {
        UseM2lbReport();
        var cut = Open();
        cut.Find("[data-testid=ip-source-archive]").TextContent.Should().Be("source.zip");
        cut.Find("[data-testid=ip-source-status]").TextContent.Should().Be("Partial");
        var producer = Row(cut, "dbo.Person").QuerySelector("[data-testid=ip-row-producer]")!;
        producer.TextContent.Should().Contain("CDC").And.Contain("Debezium").And.Contain("detected from source");
        producer.QuerySelector(".ip-detected")!.GetAttribute("title").Should().Be("Detected from source");
    }

    [Fact]
    public void CompactFiltersNarrowByMappingAndConfiguration_AndClearResetsAll()
    {
        UseM2lbReport();
        var cut = Open();
        cut.Find("[data-testid=ip-filter-mapping]").Change(nameof(MappingCategory.Suggested));
        cut.FindAll("[data-testid=ip-row]").Should().HaveCount(8).And.OnlyContain(r => r.GetAttribute("data-mapping-category") == "Suggested");
        cut.Find("[data-testid=ip-filter-state]").Change(nameof(IntegrationConfigurationState.NeedsConfirmation));
        cut.Find("[data-testid=ip-search]").Input("Tiltak");
        cut.FindAll("[data-testid=ip-row]").Should().HaveCount(2);
        cut.Find("[data-testid=ip-active-filter]").TextContent.Should().Contain("Mapping: Suggested").And.Contain("Needs confirmation").And.Contain("Search: Tiltak");
        cut.Find("[data-testid=ip-clear-filters]").Click();
        cut.FindAll("[data-testid=ip-active-filter]").Should().BeEmpty();
        cut.FindAll("[data-testid=ip-row]").Should().HaveCount(16);
        foreach (var id in new[] { "ip-filter-mapping", "ip-filter-state", "ip-search" })
            cut.Find($"[data-testid={id}]").Closest("label").Should().NotBeNull("every filter has a visible label");
        foreach (var removed in new[] { "ip-filter-pattern", "ip-filter-transport", "ip-filter-source", "ip-filter-readiness" })
            cut.FindAll($"[data-testid={removed}]").Should().BeEmpty("source-analysis facets belong to Source Analysis, not to the configuration list");
    }

    [Fact]
    public void RowDetailsLinkToSourceAnalysis_InsteadOfRepeatingItsEvidence()
    {
        UseM2lbReport();
        var cut = Open();
        Row(cut, "dbo.Kommune").QuerySelector("[data-testid=ip-row-view]")!.Click();
        var detail = cut.Find("[data-testid=ip-row-detail]");
        detail.QuerySelector("[data-testid=ip-detail-source]")!.TextContent.Should().Contain("Source suggestion").And.Contain("PersonBiRKAdapter.Worker").And.Contain("not been confirmed");
        detail.QuerySelector("[data-testid=ip-detail-open-source]")!.GetAttribute("href").Should().Be("/source-analysis");
        detail.QuerySelector("[data-testid=ip-detail-origin]")!.TextContent.Should().NotBeNullOrWhiteSpace();
        cut.FindAll("[data-testid=ip-source-detail]").Should().BeEmpty("detailed source evidence belongs to Source Analysis");
        detail.TextContent.Should().NotContain("Shop/Worker/Program.cs:12").And.NotContain("architecture-bridge");
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
        cut.Find("[data-testid=ip-confirm-evidence]").TextContent.Should().Contain("Inferred").And.Contain("source.zip");
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
        var cut = Open().WithAdvancedOpen();
        cut.Find("[data-testid=ip-discover]").TextContent.Should().Be("Refresh integration candidates");
        cut.Find("[data-testid=ip-discover-help]").TextContent.Should().Contain("stored Source Analysis snapshot").And.Contain("Source is not re-analyzed").And.Contain("no live system is contacted");
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
        var cut = Open().WithAdvancedOpen();
        var toggle = cut.Find("[data-testid=ip-technical-toggle]");
        toggle.GetAttribute("aria-expanded").Should().Be("false");
        toggle.TextContent.Should().Contain("Technical integration channels").And.Contain("5 excluded");
        cut.Find("[data-testid=ip-technical-subtitle]").TextContent.Should().Be("Platform/support channels excluded from business integration counts.");
        cut.FindAll("[data-testid=ip-technical-row]").Should().HaveCount(5);
        cut.Find("[data-testid=ip-technical-list] thead").TextContent.Should().Contain("Transport").And.Contain("Reason excluded");
        Int(cut, "ip-summary-configured").Should().Be(16, "technical channels never count as business integrations");
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
        cut.Find("[data-testid=ip-configured]").TextContent.Should().NotContain("Debezium");
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
        cut.Find("[data-testid=ip-configured]").TextContent.Should().NotContain("CDC").And.NotContain("Debezium").And.NotContain("Event Hub");
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
        Int(cut, "ip-suggestions-total").Should().Be(1);
        Int(cut, "ip-suggestions-strong").Should().Be(1);
        cut.Find("[data-testid=ip-suggestions-toggle]").GetAttribute("aria-expanded").Should().Be("false");
        cut.FindAll("[data-testid=ip-suggestion]").Should().BeEmpty("suggestions are secondary: listed only on request");
        cut.Find("[data-testid=ip-suggestions-toggle]").Click();
        var row = cut.Find("[data-testid=ip-suggestion]");
        row.TextContent.Should().Contain("Shop.Web").And.Contain("GraphQL").And.Contain("/graphql").And.Contain("Catalog.Graph").And.Contain("Strongly supported");
        row.TextContent.Should().NotContain("Strawberry Shake", "technology detail is Source Analysis evidence");
        Int(cut, "ip-summary-configured").Should().Be(16, "an unconfigured candidate is never counted as a configured business integration");
    }

    // ── Suggestions from Source Analysis: secondary, prioritized, added on request and never confirmed ──────────────────

    private void UseSuggestions()
    {
        _api.SourceReport = catalog =>
        {
            var strong = Candidate("m2lb-cdc-dev.birkm2lb.dbo.barn_n_hjemmstedskommune", IntegrationPattern.ChangeDataCapture, IntegrationTransport.EventHub, "Debezium", "PersonBiRKAdapter.Worker", ArchitectureEvidenceState.StronglySupported) with { SourceEntity = "barn_n_hjemmstedskommune", SourceDatabase = "birkm2lb", SourceSchema = "dbo" };
            var inferred = Candidate("orders.created", IntegrationPattern.Messaging, IntegrationTransport.ServiceBusTopic, null, "Orders.Worker") with { SourceEntity = "Orders" };
            var unassigned = Candidate("audit.events", IntegrationPattern.Messaging, IntegrationTransport.ServiceBusQueue, null, null) with { SourceEntity = "Audit" };
            var conflicting = Candidate("leselogg", IntegrationPattern.Messaging, IntegrationTransport.ServiceBusQueue, null, "Revisjon", ArchitectureEvidenceState.Conflict) with { SourceEntity = "Leselogg" };
            return Report(catalog, [strong, inferred, unassigned, conflicting], _ => null, strong.Id, inferred.Id, unassigned.Id, conflicting.Id);
        };
    }

    [Fact]
    public void Candidates_AreSummarized_AndTheDefaultListShowsReadyToReviewFirst()
    {
        UseSuggestions();
        var cut = Open();
        Int(cut, "ip-summary-suggestions").Should().Be(4);
        (Int(cut, "ip-suggestions-total"), Int(cut, "ip-suggestions-strong"), Int(cut, "ip-suggestions-suggested"), Int(cut, "ip-suggestions-unassigned"), Int(cut, "ip-suggestions-conflicts"))
            .Should().Be((4, 1, 1, 1, 1));
        cut.Find("[data-testid=ip-review-suggestions]").TextContent.Should().Be("Review integration candidates");
        cut.Find("[data-testid=ip-review-suggestions]").Click();
        cut.FindAll("[data-testid=ip-suggestion]").Select(r => r.GetAttribute("data-strength")).Should().Equal("StronglySupported");
        cut.FindAll("[data-testid=ip-suggestion-strength]").Should().BeEmpty("a single-state view does not repeat the same badge on every row");
        cut.Find("[data-testid=ip-suggestion-filter]").TextContent.Should().Contain("Showing 1 of 4 candidates · Ready to review");
        cut.Find("[data-testid=ip-candidate-filter]").Change(nameof(CandidateFilter.Conflicts));
        cut.FindAll("[data-testid=ip-suggestion]").Select(r => r.GetAttribute("data-strength")).Should().Equal("Conflict");
        cut.Find("[data-testid=ip-suggestions-all]").Click();
        cut.FindAll("[data-testid=ip-suggestion]").Select(r => r.GetAttribute("data-strength")).Should().Equal("StronglySupported", "Suggested", "Unassigned", "Conflict");
        cut.FindAll("[data-testid=ip-suggestion-strength]").Select(s => s.TextContent).Should().Equal("Ready to review", "Needs confirmation", "Needs mapping", "Conflicting evidence");
        cut.Find("[data-testid=ip-candidate-search]").Input("Orders");
        cut.FindAll("[data-testid=ip-suggestion]").Should().ContainSingle().Which.QuerySelector("[data-testid=ip-suggestion-relationship]")!.TextContent.Should().Be("Orders → Orders.Worker");
        cut.Find("[data-testid=ip-suggestions-toggle]").GetAttribute("aria-expanded").Should().Be("true");
        _api.Saved.Should().BeEmpty("reviewing candidates writes nothing");
    }

    [Fact]
    public void AddingASuggestion_CreatesASourceSuggestionRecord_ThatIsNotConfirmed()
    {
        UseSuggestions();
        var cut = Open();
        cut.Find("[data-testid=ip-review-suggestions]").Click();
        var review = cut.FindAll("[data-testid=ip-suggestion]").Single(r => r.GetAttribute("data-strength") == "StronglySupported").QuerySelector("[data-testid=ip-suggestion-review]")!;
        review.TextContent.Should().Be("Review & configure", "the list action reviews a candidate; it never implies a confirmed mapping");
        review.GetAttribute("aria-label").Should().StartWith("Review and configure ");
        review.Click();
        _api.Created.Should().BeEmpty("opening the review adds nothing");
        cut.Find("[data-testid=ip-candidate-review]").TextContent.Should().Contain("stays unconfirmed");
        var add = cut.Find("[data-testid=ip-suggestion-add]");
        add.TextContent.Should().Be("Add as unconfirmed integration");
        add.Click();
        cut.WaitForAssertion(() => _api.Created.Should().ContainSingle());
        var created = _api.Created.Single();
        created.Origin.Should().Be(IntegrationRecordOrigin.SourceSuggestion);
        created.EndpointOrTopic.Should().Be("m2lb-cdc-dev.birkm2lb.dbo.barn_n_hjemmstedskommune");
        created.Kind.Should().Be(IntegrationKind.EventHub);
        created.SourceResource.Should().Be("birkm2lb.dbo.barn_n_hjemmstedskommune");
        created.Consumer.DisplayName.Should().Be("PersonBiRKAdapter.Worker");
        created.Consumer.MappingState.Should().Be(ConsumerMappingState.Suggested, "strongly supported in source is still not a confirmed mapping");
        created.Consumer.MappingSource.Should().Contain("strongly supported");
        created.Consumer.MappingSourceSnapshotId.Should().Be(Snapshot);
        cut.Find("[data-testid=ip-save-status]").TextContent.Should().Contain("not confirmed");
    }

    [Fact]
    public void ASuggestionWhoseChannelIsAlreadyConfigured_CannotBeAddedAgain()
    {
        _api.SourceReport = catalog =>
        {
            var person = catalog.Integrations.Single(i => i.Id.EndsWith("dbo.Person"));
            var duplicate = Candidate(person.EndpointOrTopic!.ToUpperInvariant(), IntegrationPattern.ChangeDataCapture, IntegrationTransport.EventHub, "Debezium", "Person.Adapter", ArchitectureEvidenceState.StronglySupported) with { SourceEntity = "Person (other id)" };
            return Report(catalog, [duplicate], _ => null, duplicate.Id);
        };
        var cut = Open();
        cut.Find("[data-testid=ip-review-suggestions]").Click();
        var row = cut.Find("[data-testid=ip-suggestion]");
        row.QuerySelector("[data-testid=ip-suggestion-configured]")!.TextContent.Should().Be("Already configured");
        row.QuerySelectorAll("[data-testid=ip-suggestion-add]").Should().BeEmpty();
    }
}
