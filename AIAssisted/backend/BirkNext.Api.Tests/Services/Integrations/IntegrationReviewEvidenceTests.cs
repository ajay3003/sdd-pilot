using BirkNext.Api.Data;
using BirkNext.Api.Services.Integrations;
using BirkNext.Integrations;
using BirkNext.IntegrationTopology;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace BirkNext.Api.Tests.Services.Integrations;

/// <summary>
/// Integration Quality Review reads one canonical evidence composition: Integration Catalog + Integration Topology + Connected Systems.
/// Established discovered flows and registered systems are reviewed without a catalog entry; partial evidence needs confirmation; a disabled
/// catalog entry is never overridden; a family that fails is a limitation, never "none".
/// </summary>
public sealed class IntegrationReviewEvidenceTests
{
    private const string Env = "env-1";

    private static TopologyComponent Component(string id, string name, TopologyComponentKind kind = TopologyComponentKind.Application) =>
        new() { Id = id, Name = name, Kind = kind };

    private static TopologyConnection Connection(string from, string to, TopologyRelationshipState relationship, TopologyInterfaceType type = TopologyInterfaceType.Rest,
        string protocol = "HTTPS", string? catalogId = null) => new()
    {
        Id = $"{from}->{to}", ProducerComponentId = from, ConsumerComponentId = to, Type = type, Protocol = protocol, Address = "/api/reports", Relationship = relationship,
        Provenance = catalogId is null ? [new(TopologySource.SourceAnalysis, "app.zip", "http client")] : [new(TopologySource.IntegrationCatalog, catalogId, "configured")],
    };

    private static IntegrationTopologyView Topology(IEnumerable<TopologyComponent> components, IEnumerable<TopologyConnection> connections) =>
        new() { EnvironmentId = Env, EnvironmentName = "Portal DEV", GeneratedAt = new DateTimeOffset(2026, 11, 2, 8, 0, 0, TimeSpan.Zero), Components = [.. components], Connections = [.. connections] };

    private static readonly IntegrationCatalog Empty = new() { EnvironmentId = Env };

    private static ExternalIntegration Altinn() => new() { Id = "env-1:altinn", TargetEnvironmentId = Env, Name = "Altinn – buf/skolenaervaer", ProviderType = ExternalProviderType.Altinn };

    [Fact]
    public void An_established_topology_flow_is_reviewed_without_a_catalog_entry()
    {
        var topology = Topology([Component("portal", "Portal"), Component("reports", "Reports API")], [Connection("portal", "reports", TopologyRelationshipState.Resolved)]);

        var set = IntegrationReviewEvidence.Compose(Empty, topology, null, [], null, topology.EnvironmentName);

        var candidate = set.Candidates.Should().ContainSingle().Subject;
        candidate.State.Should().Be(IntegrationCandidateState.Reviewed);
        candidate.Origin.Should().Be(IntegrationRecordOrigin.Discovered);
        candidate.DisplayName.Should().Be("Portal → Reports API");
        candidate.Kind.Should().Be(IntegrationKind.HttpApi);
        candidate.Relationship.Should().Be("Resolved");
        var effective = set.Effective(Empty);
        effective.Integrations.Should().ContainSingle(i => i.Id == candidate.IntegrationId && i.Enabled && i.Origin == IntegrationRecordOrigin.Discovered && i.Producer == "Portal");
        effective.Integrations.Single().Consumer.MappingState.Should().Be(ConsumerMappingState.Confirmed, "a Resolved relationship establishes the consumer");
        effective.Integrations.Single().Consumer.MappingEvidence.Should().Be("Integration Topology · Resolved");
        // Deterministic: the same evidence gives the same id.
        IntegrationReviewEvidence.Compose(Empty, topology, null, [], null, null).Candidates.Single().IntegrationId.Should().Be(candidate.IntegrationId);
    }

    [Fact]
    public void A_resource_centred_chain_is_one_integration_over_that_resource()
    {
        var topology = Topology([Component("cdc", "Debezium"), Component("hub", "person-hub", TopologyComponentKind.Resource), Component("adapter", "Person adapter")],
            [Connection("cdc", "hub", TopologyRelationshipState.Resolved, TopologyInterfaceType.Messaging, "Event Hub"),
             Connection("hub", "adapter", TopologyRelationshipState.StronglySupported, TopologyInterfaceType.Messaging, "Event Hub")]);

        var candidate = IntegrationReviewEvidence.Compose(Empty, topology, null, [], null, null).Candidates.Should().ContainSingle().Subject;

        candidate.DisplayName.Should().Be("Debezium → Person adapter");
        candidate.EndpointOrTopic.Should().Be("person-hub");
        candidate.Kind.Should().Be(IntegrationKind.EventHub);
        candidate.Relationship.Should().Be("Strongly supported", "the chain is as strong as its weakest hop");
        candidate.State.Should().Be(IntegrationCandidateState.Reviewed);
        IntegrationReviewEvidence.Compose(Empty, topology, null, [], null, null).Discovered.Single().Consumer.MappingState
            .Should().Be(ConsumerMappingState.Suggested, "Strongly supported suggests the consumer; it does not establish it");
    }

    [Fact]
    public void Flows_between_the_same_two_ends_are_told_apart_by_their_topic()
    {
        var topology = Topology([Component("demo", "Demo"), Component("mu", "MU"), Component("q1", "ping_pong", TopologyComponentKind.Resource), Component("q2", "tiltak", TopologyComponentKind.Resource)],
            [Connection("demo", "q1", TopologyRelationshipState.Resolved, TopologyInterfaceType.Messaging, "Service Bus"), Connection("q1", "mu", TopologyRelationshipState.Resolved, TopologyInterfaceType.Messaging, "Service Bus"),
             Connection("demo", "q2", TopologyRelationshipState.Resolved, TopologyInterfaceType.Messaging, "Service Bus"), Connection("q2", "mu", TopologyRelationshipState.Resolved, TopologyInterfaceType.Messaging, "Service Bus")]);

        var set = IntegrationReviewEvidence.Compose(Empty, topology, null, [], null, null);
        set.Candidates.Select(c => c.DisplayName).Should().Equal(["Demo → MU · ping_pong", "Demo → MU · tiltak"]);
        var system = IntegrationReviewEngine.Systems(Empty, set.Discovered).Should().ContainSingle("the topics of one producer → consumer pair are one system").Subject;
        system.SystemName.Should().Be("Demo → MU");
        system.Discovered.Should().BeTrue();
        system.ConsumersConfirmed.Should().Be(2);
    }

    [Fact]
    public void Partial_evidence_needs_confirmation_and_is_not_reviewed()
    {
        var topology = Topology([Component("portal", "Portal"), Component("reports", "Reports API"), Component("hub", "orders", TopologyComponentKind.Resource)],
            [Connection("portal", "reports", TopologyRelationshipState.Candidate), Connection("portal", "hub", TopologyRelationshipState.Resolved, TopologyInterfaceType.Messaging, "Service Bus")]);

        var set = IntegrationReviewEvidence.Compose(Empty, topology, null, [], null, null);

        set.Candidates.Should().HaveCount(2).And.OnlyContain(c => c.State == IntegrationCandidateState.NeedsConfirmation);
        set.Candidates.Should().Contain(c => c.DisplayName == "Portal → Unknown consumer" && c.Kind == IntegrationKind.ServiceBus);
        set.Discovered.Should().BeEmpty();
        set.Effective(Empty).Integrations.Should().BeEmpty();
    }

    [Fact]
    public void A_catalog_integration_is_not_duplicated_and_a_disabled_one_is_never_overridden()
    {
        var catalog = Empty with
        {
            Integrations =
            [
                new IntegrationDefinition { Id = "cat-1", EnvironmentId = Env, DisplayName = "Portal → Reports", Kind = IntegrationKind.HttpApi, Producer = "Portal" },
                new IntegrationDefinition { Id = "cat-2", EnvironmentId = Env, DisplayName = "Portal → Archive", Kind = IntegrationKind.HttpApi, Enabled = false },
            ],
        };
        var topology = Topology([Component("portal", "Portal"), Component("reports", "Reports API"), Component("archive", "Archive")],
            [Connection("portal", "reports", TopologyRelationshipState.Resolved, catalogId: "cat-1"), Connection("portal", "archive", TopologyRelationshipState.Resolved, catalogId: "cat-2")]);

        var set = IntegrationReviewEvidence.Compose(catalog, topology, null, [], null, null);

        set.Candidates.Should().HaveCount(2);
        set.Candidates.Single(c => c.IntegrationId == "cat-1").Sources.Should().Equal([IntegrationEvidenceFamily.IntegrationCatalog, IntegrationEvidenceFamily.IntegrationTopology]);
        set.Candidates.Single(c => c.IntegrationId == "cat-2").State.Should().Be(IntegrationCandidateState.Excluded);
        set.Discovered.Should().BeEmpty();
    }

    [Fact]
    public void A_source_flow_naming_a_catalog_end_by_its_abbreviation_supports_that_entry_instead_of_duplicating_it()
    {
        var catalog = Empty with
        {
            Integrations =
            [
                new IntegrationDefinition { Id = "cat-1", EnvironmentId = Env, DisplayName = "Altinn report intake", Kind = IntegrationKind.HttpApi, Producer = "Altinn",
                    Consumer = new IntegrationConsumer { DisplayName = "Meldingsutveksleren (MU)" } },
            ],
        };
        var topology = Topology([Component("altinn", "Altinn", TopologyComponentKind.External), Component("mufull", "Meldingsutveksleren (MU)"), Component("mu", "MU")],
            [Connection("altinn", "mufull", TopologyRelationshipState.Resolved, TopologyInterfaceType.ExternalApplication, catalogId: "cat-1"),
             Connection("altinn", "mu", TopologyRelationshipState.Resolved, TopologyInterfaceType.ExternalApplication),
             Connection("mu", "altinn", TopologyRelationshipState.StronglySupported, TopologyInterfaceType.ExternalApplication)]);

        var set = IntegrationReviewEvidence.Compose(catalog, topology, null, [], null, null);

        set.Candidates.Should().HaveCount(2, "Altinn → MU is the catalog entry; the reverse direction is its own integration");
        set.Candidates.Single(c => c.IntegrationId == "cat-1").Sources.Should().Equal([IntegrationEvidenceFamily.IntegrationCatalog, IntegrationEvidenceFamily.IntegrationTopology]);
        set.Candidates.Should().ContainSingle(c => c.DisplayName == "MU → Altinn" && c.State == IntegrationCandidateState.Reviewed);
        set.Families.Single(f => f.Family == IntegrationEvidenceFamily.IntegrationTopology).Detail.Should().Be("3 connections · 1 discovered integration reviewed");
    }

    [Fact]
    public void A_registered_connected_system_is_reviewed_or_joins_the_flow_that_reaches_it()
    {
        var alone = IntegrationReviewEvidence.Compose(Empty, Topology([], []), null, [Altinn()], null, "Skolenærvær DEV");
        var system = alone.Candidates.Should().ContainSingle().Subject;
        system.State.Should().Be(IntegrationCandidateState.Reviewed);
        system.DisplayName.Should().Be("Skolenærvær DEV → Altinn – buf/skolenaervaer");
        system.Kind.Should().Be(IntegrationKind.HttpApi);
        system.Sources.Should().Equal([IntegrationEvidenceFamily.ConnectedSystems]);
        alone.Discovered.Single().Consumer.MappingState.Should().Be(ConsumerMappingState.Confirmed, "the system was registered for this environment");

        var topology = Topology([Component("portal", "Portal"), Component("altinn", "Altinn 3", TopologyComponentKind.External)],
            [Connection("portal", "altinn", TopologyRelationshipState.Resolved, TopologyInterfaceType.ExternalApplication)]);
        var merged = IntegrationReviewEvidence.Compose(Empty, topology, null, [Altinn()], null, null).Candidates.Should().ContainSingle().Subject;
        merged.Sources.Should().Equal([IntegrationEvidenceFamily.IntegrationTopology, IntegrationEvidenceFamily.ConnectedSystems]);
    }

    [Fact]
    public void A_family_that_cannot_be_read_is_failed_never_none()
    {
        var set = IntegrationReviewEvidence.Compose(Empty, null, "Integration Topology could not be read.", null, "Connected Systems could not be read.", null);

        set.Families.Single(f => f.Family == IntegrationEvidenceFamily.IntegrationTopology).State.Should().Be(IntegrationEvidenceFamilyState.Failed);
        set.Families.Single(f => f.Family == IntegrationEvidenceFamily.ConnectedSystems).State.Should().Be(IntegrationEvidenceFamilyState.Failed);
        set.Families.Single(f => f.Family == IntegrationEvidenceFamily.IntegrationCatalog).Detail.Should().Contain("not required");
    }

    // ── Service: readiness and run read the same composition ───────────────────────────────────────────────────────────

    private sealed class Sources(IntegrationTopologyView? topology, IReadOnlyList<ExternalIntegration> systems) : IIntegrationReviewEvidenceSources
    {
        public Task<(IntegrationTopologyView? View, string? Error)> TopologyAsync(string environmentId, CancellationToken ct) => Task.FromResult((topology, (string?)null));
        public Task<(IReadOnlyList<ExternalIntegration>? Systems, string? Error)> ConnectedSystemsAsync(string environmentId, CancellationToken ct) =>
            Task.FromResult(((IReadOnlyList<ExternalIntegration>?)systems, (string?)null));
    }

    private sealed class Off : IIntegrationNamespaceProbe, IEventHubMetadataSource, IEventHubConsumerGroupSource, ICheckpointEvidenceSource, ITelemetryEvidenceSource
    {
        private static IntegrationEvidenceAdapterStatus Status(string adapter, IntegrationEvidenceSource source) =>
            NotConfiguredEvidence.Status(adapter, source, IntegrationEvidenceState.NotConfigured, $"{adapter} is not configured (test).");
        public Task<NamespaceProbeResult> ProbeAsync(string fqdn, CancellationToken ct) => Task.FromResult(new NamespaceProbeResult(true, true, true, "ok", 1, DateTimeOffset.UtcNow, "Tls13"));
        IntegrationEvidenceAdapterStatus IEventHubMetadataSource.Describe(IntegrationPlatform platform) => Status("Event Hub metadata", IntegrationEvidenceSource.AzureMetadata);
        public Task<EvidenceResult<EventHubRuntimeMetadata>> GetHubAsync(IntegrationPlatform platform, string hubName, CancellationToken ct) =>
            Task.FromResult(EvidenceResult<EventHubRuntimeMetadata>.Missing(IntegrationEvidenceState.NotConfigured, IntegrationEvidenceSource.AzureMetadata, "off"));
        IntegrationEvidenceAdapterStatus IEventHubConsumerGroupSource.Describe(IntegrationPlatform platform) => Status("Consumer groups", IntegrationEvidenceSource.AzureResourceManager);
        public Task<EvidenceResult<ConsumerGroupList>> ListAsync(IntegrationPlatform platform, string hubName, CancellationToken ct) =>
            Task.FromResult(EvidenceResult<ConsumerGroupList>.Missing(IntegrationEvidenceState.NotConfigured, IntegrationEvidenceSource.AzureResourceManager, "off"));
        IntegrationEvidenceAdapterStatus ICheckpointEvidenceSource.Describe(IntegrationPlatform platform) => Status("Consumer checkpoints", IntegrationEvidenceSource.CheckpointStore);
        public Task<EvidenceResult<CheckpointEvidence>> GetAsync(IntegrationPlatform platform, string hubName, string consumerGroup, CancellationToken ct) =>
            Task.FromResult(EvidenceResult<CheckpointEvidence>.Missing(IntegrationEvidenceState.NotConfigured, IntegrationEvidenceSource.CheckpointStore, "off"));
        IntegrationEvidenceAdapterStatus ITelemetryEvidenceSource.Describe(IntegrationPlatform platform) => Status("Consumer telemetry", IntegrationEvidenceSource.ApplicationInsights);
        public Task<EvidenceResult<ConsumerTelemetry>> GetConsumerAsync(IntegrationPlatform platform, string roleName, int windowHours, CancellationToken ct) =>
            Task.FromResult(EvidenceResult<ConsumerTelemetry>.Missing(IntegrationEvidenceState.NotConfigured, IntegrationEvidenceSource.ApplicationInsights, "off"));
    }

    private static IntegrationReviewService Service(AppDbContext db, IIntegrationReviewEvidenceSources sources)
    {
        var catalog = new IntegrationCatalogService(db, NullLogger<IntegrationCatalogService>.Instance);
        var off = new Off();
        var engine = new IntegrationReviewEngine(off, off, off, off, off, new HttpClient(), NullLogger<IntegrationReviewEngine>.Instance);
        return new IntegrationReviewService(catalog, engine, new IntegrationContractStore(db, catalog, NullLogger<IntegrationContractStore>.Instance), db,
            NullLogger<IntegrationReviewService>.Instance, evidence: sources);
    }

    [Fact]
    public async Task Readiness_and_run_cover_discovered_integrations_with_an_empty_catalog()
    {
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var topology = Topology([Component("portal", "Portal"), Component("reports", "Reports API")], [Connection("portal", "reports", TopologyRelationshipState.Resolved)]);
        var service = Service(db, new Sources(topology, [Altinn()]));

        var readiness = await service.ReadinessAsync(Env, "Development", null);

        readiness.CanRun.Should().BeTrue("the evidence establishes integrations; none has to be defined by hand");
        readiness.Subjects.Should().HaveCount(2).And.OnlyContain(s => s.Origin == IntegrationRecordOrigin.Discovered);
        readiness.Evidence.Select(f => f.Family).Should().Equal([IntegrationEvidenceFamily.ProjectEvidence, IntegrationEvidenceFamily.IntegrationTopology,
            IntegrationEvidenceFamily.ConnectedSystems, IntegrationEvidenceFamily.IntegrationCatalog, IntegrationEvidenceFamily.Contracts, IntegrationEvidenceFamily.RuntimeEvidence]);
        readiness.EvaluatedAt.Should().NotBeNull();
        (await new IntegrationCatalogService(db, NullLogger<IntegrationCatalogService>.Instance).GetAsync(Env, null, null)).Integrations.Should().BeEmpty("nothing is written to the catalog");

        var scoped = readiness.Subjects.First().IntegrationId;
        (await service.ReadinessAsync(Env, "Development", null, scoped)).ScopeIntegrationId.Should().Be(scoped);
        var run = await service.RunAsync(new IntegrationReviewRunRequest { EnvironmentId = Env, EnvironmentName = "Portal DEV", IntegrationId = scoped }, "Development", null);
        run.ConfigurationSnapshot.Integrations.Should().ContainSingle(i => i.Id == scoped && i.Origin == IntegrationRecordOrigin.Discovered);
    }
}
