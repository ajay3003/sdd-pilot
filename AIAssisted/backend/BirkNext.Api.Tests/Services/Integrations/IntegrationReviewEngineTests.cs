using System.Text.Json;
using BirkNext.Api.Data;
using BirkNext.Api.Services.Integrations;
using BirkNext.Integrations;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace BirkNext.Api.Tests.Services.Integrations;

/// <summary>
/// Integration Quality Review over configured Integrations and typed read-only evidence: configuration never stands in for runtime
/// evidence, missing sources are "Not assessed" with their precise reason (never zero), platform problems are one finding, lag needs both
/// positions, no invented thresholds, contracts need both sides, and each run keeps its configuration and contract snapshot.
/// </summary>
public sealed class IntegrationReviewEngineTests
{
    private const string DevId = "dev-profile";
    private const string DevUrl = "https://m2lbdev.bufetat.no/";
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    // ── Fakes ───────────────────────────────────────────────────────────────────────────────────────────────────────

    private sealed class Probe(bool reachable) : IIntegrationNamespaceProbe
    {
        public int Calls { get; private set; }
        public Task<NamespaceProbeResult> ProbeAsync(string fqdn, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new NamespaceProbeResult(true, reachable, reachable, reachable ? "Resolved, TCP 443 connected and TLS established (Tls13)." : "TCP connection failed (TimedOut).", 12, DateTimeOffset.UtcNow, reachable ? "Tls13" : null));
        }
    }

    private static IntegrationEvidenceAdapterStatus Ready(string adapter, IntegrationEvidenceSource source) => NotConfiguredEvidence.Status(adapter, source, IntegrationEvidenceState.Available, "Configured (test).");
    private static IntegrationEvidenceAdapterStatus Off(string adapter, IntegrationEvidenceSource source) => NotConfiguredEvidence.Status(adapter, source, IntegrationEvidenceState.NotConfigured, $"{adapter} is not configured (test).");

    private sealed class Metadata(Func<string, EvidenceResult<EventHubRuntimeMetadata>>? respond) : IEventHubMetadataSource
    {
        public IntegrationEvidenceAdapterStatus Describe(IntegrationPlatform platform) => respond is null ? Off("Event Hub metadata", IntegrationEvidenceSource.AzureMetadata) : Ready("Event Hub metadata", IntegrationEvidenceSource.AzureMetadata);
        public Task<EvidenceResult<EventHubRuntimeMetadata>> GetHubAsync(IntegrationPlatform platform, string hubName, CancellationToken ct) =>
            Task.FromResult(respond?.Invoke(hubName) ?? EvidenceResult<EventHubRuntimeMetadata>.Missing(IntegrationEvidenceState.NotConfigured, IntegrationEvidenceSource.AzureMetadata, "Event Hub metadata is not configured (test)."));
    }

    private sealed class Groups(Func<string, EvidenceResult<ConsumerGroupList>>? respond) : IEventHubConsumerGroupSource
    {
        public IntegrationEvidenceAdapterStatus Describe(IntegrationPlatform platform) => respond is null ? Off("Consumer groups", IntegrationEvidenceSource.AzureResourceManager) : Ready("Consumer groups", IntegrationEvidenceSource.AzureResourceManager);
        public Task<EvidenceResult<ConsumerGroupList>> ListAsync(IntegrationPlatform platform, string hubName, CancellationToken ct) =>
            Task.FromResult(respond?.Invoke(hubName) ?? EvidenceResult<ConsumerGroupList>.Missing(IntegrationEvidenceState.NotConfigured, IntegrationEvidenceSource.AzureResourceManager, "Not configured (test)."));
    }

    private sealed class Checkpoints(Func<string, string, EvidenceResult<CheckpointEvidence>>? respond) : ICheckpointEvidenceSource
    {
        public IntegrationEvidenceAdapterStatus Describe(IntegrationPlatform platform) => respond is null ? Off("Consumer checkpoints", IntegrationEvidenceSource.CheckpointStore) : Ready("Consumer checkpoints", IntegrationEvidenceSource.CheckpointStore);
        public Task<EvidenceResult<CheckpointEvidence>> GetAsync(IntegrationPlatform platform, string hubName, string consumerGroup, CancellationToken ct) =>
            Task.FromResult(respond?.Invoke(hubName, consumerGroup) ?? EvidenceResult<CheckpointEvidence>.Missing(IntegrationEvidenceState.NotConfigured, IntegrationEvidenceSource.CheckpointStore, "No checkpoint evidence source configured (test)."));
    }

    private sealed class Telemetry(Func<string, EvidenceResult<ConsumerTelemetry>>? respond) : ITelemetryEvidenceSource
    {
        public List<string> Roles { get; } = [];
        public IntegrationEvidenceAdapterStatus Describe(IntegrationPlatform platform) => respond is null ? Off("Consumer telemetry", IntegrationEvidenceSource.ApplicationInsights) : Ready("Consumer telemetry", IntegrationEvidenceSource.ApplicationInsights);
        public Task<EvidenceResult<ConsumerTelemetry>> GetConsumerAsync(IntegrationPlatform platform, string roleName, int windowHours, CancellationToken ct)
        {
            Roles.Add(roleName);
            return Task.FromResult(respond?.Invoke(roleName) ?? EvidenceResult<ConsumerTelemetry>.Missing(IntegrationEvidenceState.NotConfigured, IntegrationEvidenceSource.ApplicationInsights, "No telemetry source configured (test)."));
        }
    }

    private static IntegrationReviewEngine Engine(IIntegrationNamespaceProbe? probe = null, IEventHubMetadataSource? metadata = null, IEventHubConsumerGroupSource? groups = null,
        ICheckpointEvidenceSource? checkpoints = null, ITelemetryEvidenceSource? telemetry = null) =>
        new(probe ?? new Probe(true), metadata ?? new Metadata(null), groups ?? new Groups(null), checkpoints ?? new Checkpoints(null), telemetry ?? new Telemetry(null),
            new HttpClient(), NullLogger<IntegrationReviewEngine>.Instance);

    private static EvidenceResult<EventHubRuntimeMetadata> Hub(int partitions = 1, long lastSequence = 100, DateTimeOffset? lastEnqueued = null) =>
        EvidenceResult<EventHubRuntimeMetadata>.Available(IntegrationEvidenceSource.AzureMetadata,
            new EventHubRuntimeMetadata(true, Enumerable.Range(0, partitions).Select(i => new PartitionRuntime(i.ToString(), lastSequence, lastEnqueued ?? Now.AddMinutes(-5), false)).ToList()));

    private static EvidenceResult<ConsumerTelemetry> Tel(long exceptions = 0, long deser = 0, long auth = 0, long traces = 40) =>
        EvidenceResult<ConsumerTelemetry>.Available(IntegrationEvidenceSource.ApplicationInsights, new ConsumerTelemetry(exceptions, deser, auth, 0, 0, traces, traces, 10, 0, 18.5, Now.AddMinutes(-2), exceptions > 0 ? Now.AddMinutes(-3) : null, 24));

    private static AppDbContext Db(string? name = null) => new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(name ?? Guid.NewGuid().ToString()).Options);

    /// <summary>The seeded DEV catalog. By default without the v4 runtime-evidence defaults (no platform consumer-group expectation), so each
    /// test states the sources it needs; <paramref name="seededRuntime"/> keeps the verified defaults ($Default as a configured assumption).</summary>
    private static async Task<IntegrationCatalog> DevCatalog(AppDbContext? db = null, bool seededRuntime = false)
    {
        var catalog = await new IntegrationCatalogService(db ?? Db(), NullLogger<IntegrationCatalogService>.Instance).GetAsync(DevId, "Development", DevUrl);
        return seededRuntime ? catalog : catalog with { Platforms = catalog.Platforms.Select(p => p.Kind == IntegrationKind.EventHub ? p with { RuntimeEvidence = null } : p).ToList() };
    }

    private static IntegrationCatalog With(IntegrationCatalog catalog, Func<IntegrationDefinition, IntegrationDefinition>? topic = null, Func<IntegrationPlatform, IntegrationPlatform>? platform = null) =>
        catalog with
        {
            Integrations = catalog.Integrations.Select(topic ?? (t => t)).ToList(),
            Platforms = catalog.Platforms.Select(platform ?? (p => p)).ToList(),
        };

    private static IntegrationCatalog OnlyPerson(IntegrationCatalog catalog) => catalog with { Integrations = catalog.Integrations.Where(i => i.Id.EndsWith("dbo.Person")).ToList() };

    private static Task<IntegrationReviewResult> Run(IntegrationReviewEngine engine, IntegrationCatalog catalog, IntegrationContractSet? contracts = null, IReadOnlyList<IntegrationContractArtifact>? previous = null) =>
        engine.RunAsync(catalog, new IntegrationReviewRunRequest { EnvironmentId = DevId, EnvironmentName = "M2LB DEV" }, contracts ?? IntegrationContractSet.Empty, previous ?? [], CancellationToken.None);

    private static IntegrationCheck TopicCheck(IntegrationReviewResult result, string table, string checkId) =>
        result.Systems.SelectMany(s => s.Topics).Single(t => t.IntegrationId.EndsWith($"dbo.{table}")).Checks.Single(c => c.CheckId == checkId);

    private static IntegrationCheck PlatformCheck(IntegrationReviewResult result, string checkId) => result.Systems.SelectMany(s => s.PlatformChecks).Single(c => c.CheckId == checkId);

    // ── Readiness ───────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Readiness_WithoutEvidenceSources_IsCapabilitySpecific_AndRunsWithLimitations()
    {
        var readiness = Engine().Readiness(await DevCatalog(), IntegrationContractSet.Empty);
        readiness.CanRun.Should().BeTrue();
        readiness.Headline.Should().Be("Can run with limitations");
        var domains = readiness.Domains.ToDictionary(d => d.Domain, d => d.Readiness);
        domains[IntegrationReviewDomain.Configuration].Should().Be(IntegrationDomainReadiness.Ready);
        domains[IntegrationReviewDomain.Connectivity].Should().Be(IntegrationDomainReadiness.Limited);
        domains[IntegrationReviewDomain.Contract].Should().Be(IntegrationDomainReadiness.NotAssessable);
        domains[IntegrationReviewDomain.MessageFlow].Should().Be(IntegrationDomainReadiness.NotAssessable);
        domains[IntegrationReviewDomain.Reliability].Should().Be(IntegrationDomainReadiness.NotAssessable);
        domains[IntegrationReviewDomain.DataQuality].Should().Be(IntegrationDomainReadiness.NotAssessable);
        readiness.EvidenceAdapters.Should().HaveCount(4).And.OnlyContain(a => a.State == IntegrationEvidenceState.NotConfigured);
        readiness.Reasons.Should().Contain(r => r.Contains("Event Hub metadata") && r.Contains("not configured"));
    }

    [Fact]
    public async Task Readiness_WithEvidenceSources_FollowsEachPrerequisite()
    {
        var engine = Engine(metadata: new Metadata(_ => Hub()), checkpoints: new Checkpoints((_, _) => throw new InvalidOperationException()), telemetry: new Telemetry(_ => Tel()));
        var readiness = engine.Readiness(await DevCatalog(), IntegrationContractSet.Empty);
        var domains = readiness.Domains.ToDictionary(d => d.Domain, d => d);
        domains[IntegrationReviewDomain.Connectivity].Readiness.Should().Be(IntegrationDomainReadiness.Ready);
        domains[IntegrationReviewDomain.MessageFlow].Readiness.Should().Be(IntegrationDomainReadiness.Partial, "transport evidence never makes message flow fully assessable");
        domains[IntegrationReviewDomain.Reliability].Readiness.Should().Be(IntegrationDomainReadiness.Limited);
        domains[IntegrationReviewDomain.Reliability].Explanation.Should().Contain("Consumer group unknown for 16");
        domains[IntegrationReviewDomain.ErrorHandling].Readiness.Should().Be(IntegrationDomainReadiness.Limited, "15 topics have no consumer application to attribute telemetry to");
        domains[IntegrationReviewDomain.Performance].Readiness.Should().Be(IntegrationDomainReadiness.Partial);
        domains[IntegrationReviewDomain.Performance].Explanation.Should().Contain("Observed");
    }

    [Fact]
    public async Task Readiness_NothingEnabled_CannotRun()
    {
        var catalog = With(await DevCatalog(), t => t with { Enabled = false });
        var readiness = Engine().Readiness(catalog, IntegrationContractSet.Empty);
        readiness.CanRun.Should().BeFalse();
        readiness.Headline.Should().Be("Cannot run");
    }

    // ── §66–69 Event Hub metadata and platform grouping ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task EventHubExists_IsObserved_NeverAnAutomaticPass()
    {
        var result = await Run(Engine(metadata: new Metadata(_ => Hub())), await DevCatalog());
        var check = TopicCheck(result, "Person", "conn-hub");
        check.Status.Should().Be(IntegrationCheckStatus.Observed, "a hub that exists is an observation, not message flow or a pass");
        check.Evidence.Should().Contain("Observed match");
        check.Provenance.Should().Be(IntegrationEvidenceSource.AzureMetadata);
        check.Freshness.Should().Be(IntegrationEvidenceItemFreshness.Current);
        PlatformCheck(result, "conn-metadata-access").Status.Should().Be(IntegrationCheckStatus.Observed);
    }

    [Fact]
    public async Task EventHubMissing_IsFail_WithOneFindingPerHub()
    {
        var engine = Engine(metadata: new Metadata(hub => hub.EndsWith("dbo.Person")
            ? EvidenceResult<EventHubRuntimeMetadata>.Available(IntegrationEvidenceSource.AzureMetadata, new EventHubRuntimeMetadata(false, []), "not found") : Hub()));
        var result = await Run(engine, await DevCatalog());
        TopicCheck(result, "Person", "conn-hub").Status.Should().Be(IntegrationCheckStatus.Fail);
        TopicCheck(result, "Barn", "conn-hub").Status.Should().Be(IntegrationCheckStatus.Observed);
        result.Findings.Should().ContainSingle(f => f.RuleId == "hub-missing").Which.AffectedIntegrations.Should().ContainSingle();
    }

    [Fact]
    public async Task MetadataUnauthorized_IsNotAssessed_NeverAFailure()
    {
        var engine = Engine(metadata: new Metadata(_ => EvidenceResult<EventHubRuntimeMetadata>.Missing(IntegrationEvidenceState.NotAuthorized, IntegrationEvidenceSource.AzureMetadata,
            "Runtime Event Hub metadata access unauthorized (UnauthorizedAccessException).")));
        var result = await Run(engine, await DevCatalog());
        var check = TopicCheck(result, "Person", "conn-hub");
        check.Status.Should().Be(IntegrationCheckStatus.NotAssessed);
        check.Explanation.Should().Contain("Not authorized").And.Contain("Runtime Event Hub metadata access unauthorized");
        result.Findings.Should().NotContain(f => f.Domain == IntegrationReviewDomain.Connectivity);
        result.EvidenceAdapters.Should().Contain(a => a.Adapter.StartsWith("Event Hub metadata") && a.State == IntegrationEvidenceState.NotAuthorized);
        result.Limitations.Should().Contain(l => l.Contains("Not authorized"));
    }

    [Fact]
    public async Task NamespaceUnreachable_IsOneFinding_AffectingSixteenTopics()
    {
        var probe = new Probe(false);
        var result = await Run(Engine(probe), await DevCatalog());
        probe.Calls.Should().Be(1, "platform checks run once, not per topic");
        result.Findings.Should().ContainSingle(f => f.RuleId == "namespace-unreachable").Which.AffectedIntegrations.Should().HaveCount(16);
    }

    [Fact]
    public async Task PartitionCountDrift_IsAWarning_WithAFinding()
    {
        var result = await Run(Engine(metadata: new Metadata(_ => Hub(partitions: 2))), OnlyPerson(await DevCatalog()));
        var check = TopicCheck(result, "Person", "conn-partitions");
        check.Status.Should().Be(IntegrationCheckStatus.Warning);
        check.Evidence.Should().Be("2 partition(s) at runtime.");
        result.Findings.Should().ContainSingle(f => f.RuleId == "partition-drift");
    }

    // ── §70–75 Consumer mapping, consumer group, checkpoints, lag ────────────────────────────────────────────────────

    [Fact]
    public async Task SuggestedMapping_StaysSuggested_EvenWithRuntimeEvidence()
    {
        var catalog = await DevCatalog();
        var result = await Run(Engine(metadata: new Metadata(_ => Hub()), telemetry: new Telemetry(_ => Tel())), catalog);
        TopicCheck(result, "Tiltak", "cfg-consumer").Status.Should().Be(IntegrationCheckStatus.NeedsConfirmation);
        result.ConfigurationSnapshot.Integrations.Single(i => i.Id.EndsWith("dbo.Tiltak")).Consumer.MappingState.Should().Be(ConsumerMappingState.Suggested);
        result.ManualFollowUp.Should().Contain(m => m.Title == "Confirm suggested consumer mappings" && m.AffectedCount == 8);
    }

    [Fact]
    public async Task UnknownConsumerGroup_LimitsOnlyCheckpointAndLag()
    {
        var result = await Run(Engine(metadata: new Metadata(_ => Hub()), checkpoints: new Checkpoints((_, _) => throw new InvalidOperationException("must not be called"))), OnlyPerson(await DevCatalog()));
        TopicCheck(result, "Person", "rel-checkpoint").Status.Should().Be(IntegrationCheckStatus.NotAssessed);
        TopicCheck(result, "Person", "rel-checkpoint").Explanation.Should().Contain("Consumer group is not configured");
        TopicCheck(result, "Person", "perf-lag").Status.Should().Be(IntegrationCheckStatus.NotAssessed);
        TopicCheck(result, "Person", "flow-producer").Status.Should().Be(IntegrationCheckStatus.Observed, "other domains still run");
        TopicCheck(result, "Person", "cfg-consumer-group").Evidence.Should().NotContain("$Default");
    }

    [Fact]
    public async Task DiscoveredConsumerGroup_IsASuggestionWithProvenance_NeverSaved()
    {
        var groups = new Groups(_ => EvidenceResult<ConsumerGroupList>.Available(IntegrationEvidenceSource.AzureResourceManager, new ConsumerGroupList(["$Default", "person-adapter"])));
        var catalog = OnlyPerson(await DevCatalog());
        var result = await Run(Engine(groups: groups), catalog);
        var topic = result.Systems.Single().Topics.Single();
        topic.SuggestedConsumerGroup.Should().Be("person-adapter");
        topic.SuggestedConsumerGroupSource.Should().Contain("Azure Resource Manager");
        TopicCheck(result, "Person", "cfg-consumer-group").Status.Should().Be(IntegrationCheckStatus.NotConfigured, "a suggestion is not a configured group");
        result.ConfigurationSnapshot.Integrations.Single().ConsumerGroup.Should().BeNull();
        result.ManualFollowUp.Should().Contain(m => m.Title == "Confirm suggested consumer groups");
    }

    private static IntegrationCatalog PersonWithGroup(IntegrationCatalog catalog, long? maxLag = null, int? maxAge = null) =>
        With(OnlyPerson(catalog), t => t with { ConsumerGroup = "person-adapter" },
            p => p with { RuntimeEvidence = new IntegrationRuntimeEvidenceSettings { MaxConsumerLagEvents = maxLag, MaxCheckpointAgeMinutes = maxAge } });

    private static EvidenceResult<CheckpointEvidence> Checkpoint(long sequence = 95, DateTimeOffset? updated = null) =>
        EvidenceResult<CheckpointEvidence>.Available(IntegrationEvidenceSource.CheckpointStore,
            new CheckpointEvidence("person-adapter", [new PartitionCheckpoint("0", sequence, 4096, updated ?? Now.AddMinutes(-7))], 1));

    [Fact]
    public async Task CheckpointEvidence_IsRecordedWithPartitionPositionAndTime()
    {
        var result = await Run(Engine(metadata: new Metadata(_ => Hub()), checkpoints: new Checkpoints((_, _) => Checkpoint())), PersonWithGroup(await DevCatalog()));
        var check = TopicCheck(result, "Person", "rel-checkpoint");
        check.Status.Should().Be(IntegrationCheckStatus.Pass);
        check.Evidence.Should().Contain("Checkpoints for 1 of 1 partition(s)").And.Contain("person-adapter");
        check.SourceTimestamp.Should().NotBeNull();
        check.Provenance.Should().Be(IntegrationEvidenceSource.CheckpointStore);
    }

    [Fact]
    public async Task NoCheckpointSource_IsNotAssessed_NeverAZeroOffsetOrLag()
    {
        var result = await Run(Engine(metadata: new Metadata(_ => Hub())), PersonWithGroup(await DevCatalog()));
        TopicCheck(result, "Person", "rel-checkpoint").Status.Should().Be(IntegrationCheckStatus.NotAssessed);
        var lag = TopicCheck(result, "Person", "perf-lag");
        lag.Status.Should().Be(IntegrationCheckStatus.NotAssessed);
        lag.Evidence.Should().BeEmpty();
        lag.Explanation.Should().Contain("never shown as 0");
    }

    [Fact]
    public void Lag_IsLatestSequenceMinusCheckpoint_PerPartition()
    {
        var hub = new EventHubRuntimeMetadata(true, [new PartitionRuntime("0", 100, Now, false), new PartitionRuntime("1", 40, Now, false), new PartitionRuntime("2", 7, null, true)]);
        var checkpoint = new CheckpointEvidence("g", [new PartitionCheckpoint("0", 95, null, Now)], 0);
        var (lag, compared, withoutCheckpoint) = IntegrationReviewEngine.Lag(hub, checkpoint);
        (lag, compared, withoutCheckpoint).Should().Be((5L, 2, 1), "partition 1 has no checkpoint and is not counted; the empty partition has nothing to consume");
    }

    [Fact]
    public async Task Lag_WithoutThreshold_IsObserved()
    {
        var result = await Run(Engine(metadata: new Metadata(_ => Hub()), checkpoints: new Checkpoints((_, _) => Checkpoint())), PersonWithGroup(await DevCatalog()));
        var lag = TopicCheck(result, "Person", "perf-lag");
        lag.Status.Should().Be(IntegrationCheckStatus.Observed);
        lag.Evidence.Should().StartWith("5 event(s) behind");
        result.Findings.Should().NotContain(f => f.RuleId == "consumer-lag");
        result.ManualFollowUp.Should().Contain(m => m.Title == "Define IQR lag/checkpoint thresholds");
    }

    [Fact]
    public async Task Lag_WithAnExplicitThreshold_IsJudged()
    {
        var result = await Run(Engine(metadata: new Metadata(_ => Hub()), checkpoints: new Checkpoints((_, _) => Checkpoint(sequence: 10, updated: Now.AddHours(-3)))), PersonWithGroup(await DevCatalog(), maxLag: 50, maxAge: 30));
        TopicCheck(result, "Person", "perf-lag").Status.Should().Be(IntegrationCheckStatus.Warning);
        TopicCheck(result, "Person", "rel-checkpoint-freshness").Status.Should().Be(IntegrationCheckStatus.Warning);
        result.Findings.Select(f => f.RuleId).Should().Contain(["consumer-lag", "consumer-progress-stale"]);
    }

    // ── §76 activity ────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task NoRecentActivity_IsNoRecentEvidence_NotFailure()
    {
        var result = await Run(Engine(metadata: new Metadata(_ => Hub(lastEnqueued: Now.AddDays(-3)))), OnlyPerson(await DevCatalog()));
        var check = TopicCheck(result, "Person", "flow-producer");
        check.Status.Should().Be(IntegrationCheckStatus.NoRecentEvidence);
        check.Freshness.Should().Be(IntegrationEvidenceItemFreshness.Historical);
        result.Findings.Should().NotContain(f => f.Domain == IntegrationReviewDomain.MessageFlow);
    }

    // ── §77–80 Telemetry and error handling ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TelemetryAccess_IsApplicationInsightsEvidence_AndQueriedOncePerConsumer()
    {
        var telemetry = new Telemetry(_ => Tel());
        var catalog = With(await DevCatalog(), t => t.Consumer.DisplayName == "Tjeneste API" ? t with { Consumer = t.Consumer with { ContainerApp = "ca-tjeneste-api" } } : t);
        var result = await Run(Engine(telemetry: telemetry), catalog);
        result.EvidenceSources.Should().Contain(IntegrationEvidenceSource.ApplicationInsights);
        PlatformCheck(result, "obs-telemetry-access").Status.Should().Be(IntegrationCheckStatus.Pass);
        telemetry.Roles.Should().BeEquivalentTo(["ca-m2lb-person-adp-dev-nwe-001", "ca-tjeneste-api"], "one bounded query set per consumer role, not per topic");
        result.ReviewWindowHours.Should().Be(24);
    }

    [Fact]
    public async Task TelemetryUnauthorized_IsNotAssessed_WithTheReason()
    {
        var telemetry = new Telemetry(_ => EvidenceResult<ConsumerTelemetry>.Missing(IntegrationEvidenceState.NotAuthorized, IntegrationEvidenceSource.ApplicationInsights, "Telemetry access unauthorized (RequestFailedException (HTTP 403))."));
        var result = await Run(Engine(telemetry: telemetry), OnlyPerson(await DevCatalog()));
        var check = TopicCheck(result, "Person", "err-deserialization");
        check.Status.Should().Be(IntegrationCheckStatus.NotAssessed);
        check.Explanation.Should().Contain("Telemetry access unauthorized");
    }

    [Fact]
    public async Task NoErrorIndicators_AreNoIndicatorsObserved_NeverPass()
    {
        var result = await Run(Engine(telemetry: new Telemetry(_ => Tel())), OnlyPerson(await DevCatalog()));
        foreach (var id in new[] { "err-deserialization", "err-authorization", "err-consumer-exceptions", "err-dead-letter" })
            TopicCheck(result, "Person", id).Status.Should().Be(IntegrationCheckStatus.NoIndicatorsObserved);
        TopicCheck(result, "Person", "sec-runtime-auth").Status.Should().Be(IntegrationCheckStatus.NoIndicatorsObserved);
    }

    [Fact]
    public async Task DeserializationErrors_AreAWarning_AndOneFindingPerConsumer()
    {
        var catalog = With(await DevCatalog(), t => t.Consumer.DisplayName == "Tjeneste API" ? t with { Consumer = t.Consumer with { ContainerApp = "ca-tjeneste-api" } } : t);
        var result = await Run(Engine(telemetry: new Telemetry(role => role == "ca-tjeneste-api" ? Tel(exceptions: 12, deser: 9) : Tel())), catalog);
        TopicCheck(result, "Tiltak", "err-deserialization").Status.Should().Be(IntegrationCheckStatus.Warning);
        TopicCheck(result, "Person", "err-deserialization").Status.Should().Be(IntegrationCheckStatus.NoIndicatorsObserved);
        var finding = result.Findings.Should().ContainSingle(f => f.RuleId == "deserialization-errors").Subject;
        finding.Subject.Should().Be("ca-tjeneste-api");
        finding.AffectedIntegrations.Should().HaveCount(5, "the consumer serves five topics; one logical issue");
    }

    [Fact]
    public async Task AuthorizationErrors_FailRuntimeAuthorization_AsASecurityFinding()
    {
        var result = await Run(Engine(telemetry: new Telemetry(_ => Tel(exceptions: 3, auth: 3))), OnlyPerson(await DevCatalog()));
        TopicCheck(result, "Person", "sec-runtime-auth").Status.Should().Be(IntegrationCheckStatus.Fail);
        result.Findings.Should().ContainSingle(f => f.RuleId == "consumer-unauthorized").Which.Domain.Should().Be(IntegrationReviewDomain.Security);
    }

    // ── §81–84 Contracts ────────────────────────────────────────────────────────────────────────────────────────────

    private const string ProducerSchema = """
        { "$schema": "https://json-schema.org/draft/2020-12/schema", "version": "1.2", "type": "object",
          "properties": { "before": { "type": ["object","null"], "properties": { "PersonId": { "type": "integer" } } },
                          "after":  { "type": ["object","null"], "properties": { "PersonId": { "type": "integer" }, "Fornavn": { "type": "string" }, "Status": { "type": "string", "enum": ["A","I"] } }, "required": ["PersonId","Fornavn"] },
                          "source": { "type": "object", "properties": { "db": { "type": "string" }, "table": { "type": "string" } } },
                          "op": { "type": "string", "enum": ["c","u","d","r"] } },
          "required": ["op","source"] }
        """;
    private const string ConsumerSchema = """
        { "type": "object", "properties": { "after": { "type": ["object","null"], "properties": { "PersonId": { "type": "integer" }, "Fornavn": { "type": "string" } }, "required": ["PersonId"] },
          "op": { "type": "string", "enum": ["c","u","d","r"] } }, "required": ["op"] }
        """;
    private const string ConsumerNeedsMissingField = """
        { "type": "object", "properties": { "after": { "type": "object", "properties": { "PersonId": { "type": "string" }, "Fodselsnummer": { "type": "string" } }, "required": ["Fodselsnummer"] },
          "op": { "type": "string", "enum": ["c","u"] } }, "required": ["op"] }
        """;

    private static IntegrationContractSet Contracts(string integrationId, string? producer, string? consumer, string producerHash = "p1", string consumerHash = "c1")
    {
        var items = new List<(IntegrationContractArtifact, JsonSchemaContract?, string?)>();
        if (producer is not null) items.Add((new IntegrationContractArtifact { IntegrationId = integrationId, Role = IntegrationContractRole.Producer, FileName = "person.producer.schema.json", ContentHash = producerHash.PadRight(64, '0') }, JsonSchemaContract.Parse("p.json", producer).Contract, null));
        if (consumer is not null) items.Add((new IntegrationContractArtifact { IntegrationId = integrationId, Role = IntegrationContractRole.Consumer, FileName = "person.consumer.schema.json", ContentHash = consumerHash.PadRight(64, '0') }, JsonSchemaContract.Parse("c.json", consumer).Contract, null));
        return new IntegrationContractSet(items);
    }

    private const string PersonId = "dev:eventhub:birk-cdc:dbo.Person";

    [Fact]
    public async Task BothContractsCompatible_IsPass()
    {
        var result = await Run(Engine(), OnlyPerson(await DevCatalog()), Contracts(PersonId, ProducerSchema, ConsumerSchema));
        TopicCheck(result, "Person", "contract-availability").Status.Should().Be(IntegrationCheckStatus.Pass);
        TopicCheck(result, "Person", "contract-compatibility").Status.Should().Be(IntegrationCheckStatus.Pass);
        result.ContractSnapshot.Should().HaveCount(2);
    }

    [Fact]
    public async Task ContractMismatch_IsIncompatible_WithOneGroupedFinding()
    {
        var result = await Run(Engine(), OnlyPerson(await DevCatalog()), Contracts(PersonId, ProducerSchema, ConsumerNeedsMissingField));
        TopicCheck(result, "Person", "contract-compatibility").Status.Should().Be(IntegrationCheckStatus.Fail);
        var finding = result.Findings.Should().ContainSingle(f => f.RuleId == "contract-incompatible").Subject;
        finding.Evidence.Should().Contain(e => e.StartsWith("REQUIRED_FIELD_MISSING") && e.Contains("after.Fodselsnummer"));
        finding.Evidence.Should().Contain(e => e.StartsWith("TYPE_MISMATCH") && e.Contains("after.PersonId"));
        finding.Evidence.Should().Contain(e => e.StartsWith("ENUM_NOT_ACCEPTED") && e.Contains("`d`"));
    }

    [Fact]
    public async Task OneContractOnly_IsPartialEvidence_NeverCompatible()
    {
        var result = await Run(Engine(), OnlyPerson(await DevCatalog()), Contracts(PersonId, ProducerSchema, null));
        TopicCheck(result, "Person", "contract-availability").Status.Should().Be(IntegrationCheckStatus.Observed);
        TopicCheck(result, "Person", "contract-compatibility").Status.Should().Be(IntegrationCheckStatus.NotAssessed);
    }

    [Fact]
    public async Task ContractReplacement_IsDrift_AndAnEarlierResultKeepsItsHash()
    {
        var catalog = OnlyPerson(await DevCatalog());
        var first = await Run(Engine(), catalog, Contracts(PersonId, ProducerSchema, ConsumerSchema, producerHash: "aaaa"));
        var second = await Run(Engine(), catalog, Contracts(PersonId, ProducerSchema, ConsumerSchema, producerHash: "bbbb"), first.ContractSnapshot);
        TopicCheck(first, "Person", "contract-drift-producer").Status.Should().Be(IntegrationCheckStatus.NotAssessed, "no baseline yet");
        TopicCheck(second, "Person", "contract-drift-producer").Status.Should().Be(IntegrationCheckStatus.Observed);
        TopicCheck(second, "Person", "contract-drift-consumer").Status.Should().Be(IntegrationCheckStatus.Pass);
        TopicCheck(second, "Person", "contract-compatibility").Status.Should().Be(IntegrationCheckStatus.Pass, "drift is not incompatibility");
        first.ContractSnapshot.Single(c => c.Role == IntegrationContractRole.Producer).ContentHash.Should().StartWith("aaaa");
    }

    // ── §85–87 Data quality ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DataQuality_WithoutAnySource_IsNotAssessed()
    {
        var result = await Run(Engine(), OnlyPerson(await DevCatalog()));
        TopicCheck(result, "Person", "dq-envelope").Status.Should().Be(IntegrationCheckStatus.NotAssessed);
        TopicCheck(result, "Person", "dq-runtime-structure").Status.Should().Be(IntegrationCheckStatus.NotAssessed);
    }

    [Fact]
    public async Task DebeziumEnvelope_FromTheProducerContract_IsRecognised()
    {
        var result = await Run(Engine(), OnlyPerson(await DevCatalog()), Contracts(PersonId, ProducerSchema, null));
        TopicCheck(result, "Person", "dq-envelope").Status.Should().Be(IntegrationCheckStatus.Pass);
        TopicCheck(result, "Person", "dq-operations").Evidence.Should().Be("Declared: c, u, d, r.");
        TopicCheck(result, "Person", "dq-delete").Status.Should().Be(IntegrationCheckStatus.Pass);
        TopicCheck(result, "Person", "dq-envelope").Explanation.Should().Contain("no event was read");
    }

    [Fact]
    public async Task Tombstones_AreJudgedOnlyAgainstAnExpectation_NeverAnUnconditionalFailure()
    {
        var catalog = OnlyPerson(await DevCatalog());
        var unspecified = await Run(Engine(), catalog, Contracts(PersonId, ProducerSchema, null));
        TopicCheck(unspecified, "Person", "dq-tombstone").Status.Should().Be(IntegrationCheckStatus.NotAssessed);
        TopicCheck(unspecified, "Person", "cfg-delete-expectation").Status.Should().Be(IntegrationCheckStatus.NotConfigured);
        unspecified.ManualFollowUp.Should().Contain(m => m.Title == "Confirm delete/tombstone expectation");

        var expected = await Run(Engine(), With(catalog, t => t with { DeleteExpectation = CdcDeleteExpectation.DeleteEventAndTombstone }), Contracts(PersonId, ProducerSchema, null));
        TopicCheck(expected, "Person", "dq-tombstone").Status.Should().Be(IntegrationCheckStatus.NotAssessed);
        TopicCheck(expected, "Person", "dq-tombstone").Expectation.Should().Be("Delete event followed by a tombstone");
        expected.Findings.Should().NotContain(f => f.Domain == IntegrationReviewDomain.DataQuality);
    }

    // ── §90–91 Configuration vs runtime ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ManagedIdentityConfigured_IsNotRuntimeAuthorization()
    {
        var result = await Run(Engine(), OnlyPerson(await DevCatalog()));
        TopicCheck(result, "Person", "sec-consumer-auth").Status.Should().Be(IntegrationCheckStatus.Pass);
        TopicCheck(result, "Person", "sec-runtime-auth").Status.Should().Be(IntegrationCheckStatus.NotAssessed);
    }

    [Fact]
    public async Task ObservabilityConfigured_IsNotTelemetryAvailable()
    {
        var result = await Run(Engine(), OnlyPerson(await DevCatalog()));
        PlatformCheck(result, "obs-provider").Status.Should().Be(IntegrationCheckStatus.Pass);
        PlatformCheck(result, "obs-telemetry-access").Status.Should().Be(IntegrationCheckStatus.NotConfigured);
        TopicCheck(result, "Person", "obs-traces").Status.Should().Be(IntegrationCheckStatus.NotAssessed);
        result.Freshness.Should().Be(IntegrationEvidenceFreshness.Current, "the namespace probe is current runtime evidence");
    }

    [Fact]
    public async Task ConfigurationOnly_NeverProducesARuntimePass()
    {
        var result = await Run(Engine(), await DevCatalog());
        result.AllChecks.Where(c => c.Status == IntegrationCheckStatus.Pass)
            .Should().OnlyContain(c => c.Provenance == IntegrationEvidenceSource.Configuration || c.Provenance == IntegrationEvidenceSource.NetworkProbe);
        result.AllChecks.Where(c => c.Domain is IntegrationReviewDomain.MessageFlow or IntegrationReviewDomain.Performance)
            .Should().OnlyContain(c => c.Status == IntegrationCheckStatus.NotAssessed);
    }

    // ── Snapshot, scope, secrets ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ReviewDoesNotReadTechnicalTopics()
    {
        var metadata = new List<string>();
        var result = await Run(Engine(metadata: new Metadata(hub => { metadata.Add(hub); return Hub(); })), await DevCatalog());
        metadata.Should().HaveCount(16).And.NotContain(h => h == "schemahistory" || h.StartsWith("connect-") || h == "m2lb-cdc-dev");
        result.TopicsReviewed.Should().Be(16);
    }

    [Fact]
    public async Task ARunKeepsItsConfigurationSnapshot_AfterLaterEdits()
    {
        var db = Db();
        var catalogService = new IntegrationCatalogService(db, NullLogger<IntegrationCatalogService>.Instance);
        var service = new IntegrationReviewService(catalogService, Engine(), new IntegrationContractStore(db, catalogService, NullLogger<IntegrationContractStore>.Instance), db, NullLogger<IntegrationReviewService>.Instance);
        await catalogService.GetAsync(DevId, "Development", DevUrl);
        var run = await service.RunAsync(new IntegrationReviewRunRequest { EnvironmentId = DevId, EnvironmentName = "M2LB DEV" }, "Development", DevUrl);
        var person = (await catalogService.GetAsync(DevId, null, null)).Integrations.Single(i => i.Id.EndsWith("dbo.Person"));
        await catalogService.UpdateAsync(DevId, person.Id, person with { ConsumerGroup = "person-adapter" });

        var stored = await service.GetRunAsync(run.RunId);
        stored!.ConfigurationSnapshot.Integrations.Single(i => i.Id.EndsWith("dbo.Person")).ConsumerGroup.Should().BeNull();
        stored.ReviewWindowHours.Should().Be(24);
        stored.EvidenceAdapters.Should().NotBeEmpty();
        (await service.HistoryAsync(DevId)).Should().ContainSingle();
    }

    [Fact]
    public async Task ResultCarriesNoSecrets()
    {
        var result = await Run(Engine(metadata: new Metadata(_ => Hub()), telemetry: new Telemetry(_ => Tel(deser: 1))), await DevCatalog());
        var json = JsonSerializer.Serialize(result);
        foreach (var forbidden in new[] { "SharedAccessKey", "Endpoint=sb://", "AccountKey=", "client_secret", "Bearer " })
            json.Should().NotContain(forbidden);
    }

    [Fact]
    public async Task ScimIdentityProvisioningContributesPlatformChecksAndASnapshotWithoutRuntimeClaims()
    {
        await using var db = Db();
        var catalogService = new IntegrationCatalogService(db, NullLogger<IntegrationCatalogService>.Instance);
        var probe = new StubScimRuntimeProbe();
        var scim = new BirkNext.Api.Services.Integrations.Scim.ScimEvidenceService(db, catalogService, probe, NullLogger<BirkNext.Api.Services.Integrations.Scim.ScimEvidenceService>.Instance);
        var engine = new IntegrationReviewEngine(new Probe(true), new Metadata(null), new Groups(null), new Checkpoints(null), new Telemetry(null), new HttpClient(), NullLogger<IntegrationReviewEngine>.Instance, scim: scim);
        var service = new IntegrationReviewService(catalogService, engine, new IntegrationContractStore(db, catalogService, NullLogger<IntegrationContractStore>.Instance), db, NullLogger<IntegrationReviewService>.Instance, scim: scim);
        await catalogService.GetAsync(DevId, "Development", DevUrl);

        var readiness = await service.ReadinessAsync(DevId, "Development", DevUrl);
        var run = await service.RunAsync(new IntegrationReviewRunRequest { EnvironmentId = DevId, EnvironmentName = "M2LB DEV" }, "Development", DevUrl);

        readiness.Scim.Should().ContainSingle().Which.SourceAnalyzed.Should().BeFalse();
        readiness.Scim[0].RuntimeState.Should().Be(IntegrationEvidenceState.NotConfigured, "the seeded DEV base URL is unknown");
        probe.EnvironmentTypes.Should().Equal("Development");
        var system = run.Systems.Single(s => s.Kind == IntegrationKind.IdentityProvisioning);
        system.PlatformChecks.Should().Contain(c => c.Domain == IntegrationReviewDomain.MessageFlow && c.Status == IntegrationCheckStatus.NotAssessed);
        system.PlatformChecks.Should().Contain(c => c.Domain == IntegrationReviewDomain.Performance && c.Status == IntegrationCheckStatus.NotAssessed);
        system.PlatformChecks.Should().NotContain(c => c.Status == IntegrationCheckStatus.Pass);
        run.ScimSnapshot.Should().ContainSingle().Which.OverallState.Should().Be(ScimOverallState.NotTestable, "no source was analyzed and nothing was contacted");
        run.EvidenceAdapters.Should().Contain(a => a.Adapter.StartsWith("SCIM safe checks") && a.State == IntegrationEvidenceState.NotConfigured);
        (await service.GetRunAsync(run.RunId))!.ScimSnapshot.Should().ContainSingle();
    }

    private sealed class NoLiveProbe : BirkNext.Api.Services.SecurityClassification.IClassificationLiveProbe
    {
        public int Calls { get; private set; }
        public Task<ClassificationLiveEvidence> ProbeAsync(ClassificationTestContext context, ClassificationRunRequest request, IReadOnlyList<ClassificationLevel> levels, CancellationToken ct = default)
        { Calls++; return Task.FromResult(new ClassificationLiveEvidence()); }
    }

    [Fact]
    public async Task SecurityClassificationContributesSourceChecksWithoutLiveCalls()
    {
        await using var db = Db();
        var catalogService = new IntegrationCatalogService(db, NullLogger<IntegrationCatalogService>.Instance);
        var probe = new NoLiveProbe();
        var classification = new BirkNext.Api.Services.SecurityClassification.ClassificationReviewService(db, probe, new BirkNext.Api.Services.SecurityClassification.ClassificationTestContextStore(), NullLogger<BirkNext.Api.Services.SecurityClassification.ClassificationReviewService>.Instance);
        var files = new List<BirkNext.Api.Services.Integrations.ApplicationMessaging.SourceFile>
        {
            new("P/src/Person/M2LB.Person.csproj", "<Project/>"),
            new("P/src/Person/Level.cs", "public class SikkerhetsnivaaType { public int Nivaa { get; set; } public bool KreverGradertTilgang { get; set; } }"),
            new("P/src/Person/Seed.cs", "class S { void M() { var a = new SikkerhetsnivaaType { Nivaa = 2, Verdi = \"Kode7\", KreverGradertTilgang = true }; } }"),
        };
        using var stream = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
            foreach (var f in files) { using var w = new StreamWriter(zip.CreateEntry(f.Path).Open()); w.Write(f.Content); }
        // Source Analysis owns the archive; a Security Classification run binds its exact snapshot, which IQR then reuses (never a newer one).
        var (snapshot, error) = await new BirkNext.Api.Services.Integrations.SourceEvidence.IqrSourceStore(db).AnalyzeAsync(DevId, "source-analysis", "M2LB.zip", stream.ToArray());
        error.Should().BeNull();
        await classification.RunAsync(DevId, new ClassificationRunRequest { EnvironmentType = "Development", SourceScope = new() { PrimarySnapshotId = snapshot!.Id } });
        var liveCallsBefore = probe.Calls;
        var engine = new IntegrationReviewEngine(new Probe(true), new Metadata(null), new Groups(null), new Checkpoints(null), new Telemetry(null), new HttpClient(), NullLogger<IntegrationReviewEngine>.Instance, classification: classification);
        var service = new IntegrationReviewService(catalogService, engine, new IntegrationContractStore(db, catalogService, NullLogger<IntegrationContractStore>.Instance), db, NullLogger<IntegrationReviewService>.Instance);
        await catalogService.GetAsync(DevId, "Development", DevUrl);

        var run = await service.RunAsync(new IntegrationReviewRunRequest { EnvironmentId = DevId, EnvironmentName = "M2LB DEV" }, "Development", DevUrl);

        probe.Calls.Should().Be(liveCallsBefore, "IQR never runs live security checks");
        run.SecurityClassificationSnapshot.Should().NotBeNull();
        var system = run.Systems.Single(s => s.SystemName.StartsWith("Security classification"));
        system.PlatformChecks.Should().Contain(c => c.CheckId == "classification-model-reference" && c.Status == IntegrationCheckStatus.Detected);
        system.PlatformChecks.Should().Contain(c => c.Domain == IntegrationReviewDomain.MessageFlow && c.Status == IntegrationCheckStatus.NotAssessed);
        system.PlatformChecks.Should().NotContain(c => c.Status == IntegrationCheckStatus.Pass);
    }

    [Fact]
    public async Task IqrBindsAnExactSourceAnalysisSnapshotToAnIntegrationAndNeverALegacyUpload()
    {
        await using var db = Db();
        var catalogService = new IntegrationCatalogService(db, NullLogger<IntegrationCatalogService>.Instance);
        var store = new BirkNext.Api.Services.Integrations.SourceEvidence.IqrSourceStore(db);
        using var stream = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            using var w = new StreamWriter(zip.CreateEntry("P/src/Adapter/Adapter.csproj").Open()); w.Write("<Project/>");
        }
        var (snapshot, _) = await store.AnalyzeAsync(DevId, "source-analysis", "M2LB.zip", stream.ToArray());
        var (legacy, _) = await store.AnalyzeAsync(DevId, "person-adapter", "M2LB.zip", stream.ToArray());
        var service = new IntegrationReviewService(catalogService, Engine(), new IntegrationContractStore(db, catalogService, NullLogger<IntegrationContractStore>.Instance), db, NullLogger<IntegrationReviewService>.Instance,
            source: new BirkNext.Api.Services.SourceAnalysis.ReviewSourceEvidenceProvider(store));
        var integration = (await catalogService.GetAsync(DevId, "Development", DevUrl)).Integrations.First(i => i.Enabled).Id;

        var run = await service.RunAsync(new IntegrationReviewRunRequest { EnvironmentId = DevId, EnvironmentName = "M2LB DEV", SourceSelections = [new(integration, snapshot!.Id)] }, "Development", DevUrl);
        var refuse = () => service.RunAsync(new IntegrationReviewRunRequest { EnvironmentId = DevId, SourceSelections = [new(integration, legacy!.Id)] }, "Development", DevUrl);
        var readiness = await service.ReadinessAsync(DevId, "Development", DevUrl);

        run.SourceSnapshots.Should().ContainSingle(s => s.Id == snapshot.Id && s.IntegrationId == integration && s.Archive.Sha256 == snapshot.Archive.Sha256, "bound to the integration it was chosen for");
        await refuse.Should().ThrowAsync<BirkNext.Api.Services.Integrations.SourceEvidence.InvalidSourceSelectionException>().WithMessage("*nothing is substituted*");
        readiness.SourceSnapshots.Select(s => s.Id).Should().Equal(new[] { snapshot.Id }, "only Source Analysis snapshots are offered");
    }

    [Fact]
    public async Task IqrReadsObservabilityEvidenceFromTheSelectedSnapshotWithoutRescanning()
    {
        await using var db = Db();
        var catalogService = new IntegrationCatalogService(db, NullLogger<IntegrationCatalogService>.Instance);
        var store = new BirkNext.Api.Services.Integrations.SourceEvidence.IqrSourceStore(db);
        var bytes = BirkNext.Api.Tests.Services.SourceAnalysis.ObservabilitySourceAnalyzerTests.Zip(
            ("shop/Shop.Api/Shop.Api.csproj", """<Project Sdk="Microsoft.NET.Sdk.Web"><ItemGroup><PackageReference Include="OpenTelemetry.Extensions.Hosting" Version="1.0.0" /></ItemGroup></Project>"""),
            ("shop/Shop.Api/Program.cs", "var b = WebApplication.CreateBuilder(args); b.Services.AddOpenTelemetry().WithTracing(t => t.AddAspNetCoreInstrumentation().AddOtlpExporter()); var app = b.Build(); app.MapGet(\"/\", () => 1); app.Run();"),
            ("shop/Shop.Api/Jobs.cs", "public class Jobs(ILogger<Jobs> logger) { public void Run() { try { } catch (Exception ex) { logger.LogError(\"Failed {Reason}\", ex.Message); } } }"));
        var (snapshot, _) = await store.AnalyzeAsync(DevId, "source-analysis", "shop.zip", bytes);
        snapshot!.Observability.Should().NotBeNull();
        var service = new IntegrationReviewService(catalogService, Engine(), new IntegrationContractStore(db, catalogService, NullLogger<IntegrationContractStore>.Instance), db, NullLogger<IntegrationReviewService>.Instance,
            source: new BirkNext.Api.Services.SourceAnalysis.ReviewSourceEvidenceProvider(store));
        var integration = (await catalogService.GetAsync(DevId, "Development", DevUrl)).Integrations.First(i => i.Enabled).Id;

        var run = await service.RunAsync(new IntegrationReviewRunRequest { EnvironmentId = DevId, EnvironmentName = "Dev", SourceSelections = [new(integration, snapshot.Id)] }, "Development", DevUrl);

        run.SourceSnapshots.Single().Observability!.Id.Should().Be(snapshot.Observability!.Id, "the stored analysis is read, not recomputed");
        var observability = run.Domains.Single(d => d.Domain == IntegrationReviewDomain.Observability);
        observability.Observed.Should().Contain(l => l.StartsWith("Source observability (source snapshot ") && l.Contains("OTLP exporter"));
        observability.Missing.Should().Contain(BirkNext.Api.Services.Integrations.SourceEvidence.IqrObservabilityReview.RuntimeLimitation);
        run.Domains.Single(d => d.Domain == IntegrationReviewDomain.ErrorHandling).Observed.Should().Contain(l => l.StartsWith("Source exception logging") && l.Contains("1 log only its message"));
    }

    // ── $Default as a configured assumption (seed v4) ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task ExpectedGroup_IsUsedForCheckpointLookups_ButLabelledAnAssumption()
    {
        var looked = new List<string>();
        var groups = new Groups(_ => EvidenceResult<ConsumerGroupList>.Available(IntegrationEvidenceSource.AzureResourceManager, new ConsumerGroupList(["$Default"])));
        var checkpoints = new Checkpoints((_, group) => { looked.Add(group); return Checkpoint(); });
        var result = await Run(Engine(metadata: new Metadata(_ => Hub()), groups: groups, checkpoints: checkpoints), OnlyPerson(await DevCatalog(seededRuntime: true)));
        looked.Should().Equal("$Default");
        var group = TopicCheck(result, "Person", "cfg-consumer-group");
        group.Status.Should().Be(IntegrationCheckStatus.NeedsConfirmation);
        group.Evidence.Should().Contain("Expected: $Default (Configured assumption)").And.Contain("Observed on the hub: $Default").And.Contain("Mapping: Needs confirmation");
        group.Evidence.Should().NotContain("Confirmed mapping");
        TopicCheck(result, "Person", "rel-checkpoint").Status.Should().Be(IntegrationCheckStatus.Observed, "a checkpoint for an assumed group is observed, never a Pass");
        TopicCheck(result, "Person", "rel-checkpoint").Evidence.Should().Contain("configured assumption");
        result.ConfigurationSnapshot.Integrations.Single().ConsumerGroup.Should().BeNull("the assumption is never saved on the integration");
        result.ManualFollowUp.Should().Contain(m => m.Title == "Confirm the assumed consumer group");
        result.Limitations.Should().Contain(l => l.Contains("$Default is a configured assumption"));
    }

    [Fact]
    public async Task ExpectedGroup_NeverYieldsAThresholdVerdict()
    {
        var catalog = With(OnlyPerson(await DevCatalog(seededRuntime: true)), platform: p => p.RuntimeEvidence is { } r ? p with { RuntimeEvidence = r with { MaxConsumerLagEvents = 1000, MaxCheckpointAgeMinutes = 600 } } : p);
        var result = await Run(Engine(metadata: new Metadata(_ => Hub()), checkpoints: new Checkpoints((_, _) => Checkpoint())), catalog);
        foreach (var id in new[] { "rel-checkpoint", "rel-checkpoint-freshness", "perf-lag", "cfg-consumer-group" })
            TopicCheck(result, "Person", id).Status.Should().NotBe(IntegrationCheckStatus.Pass, $"{id} must not pass on an assumed group");
        TopicCheck(result, "Person", "perf-lag").Status.Should().Be(IntegrationCheckStatus.Observed);
        TopicCheck(result, "Person", "rel-checkpoint-freshness").Status.Should().Be(IntegrationCheckStatus.Observed);
    }

    [Fact]
    public async Task WithoutThresholds_LagAndCheckpointAgeAreObservedOnly()
    {
        var result = await Run(Engine(metadata: new Metadata(_ => Hub()), checkpoints: new Checkpoints((_, _) => Checkpoint())), PersonWithGroup(await DevCatalog()));
        TopicCheck(result, "Person", "perf-lag").Status.Should().Be(IntegrationCheckStatus.Observed);
        TopicCheck(result, "Person", "rel-checkpoint-freshness").Status.Should().Be(IntegrationCheckStatus.Observed);
    }

    [Fact]
    public async Task AnIntegrationsOwnGroupWinsOverTheExpectation()
    {
        var looked = new List<string>();
        var catalog = With(OnlyPerson(await DevCatalog(seededRuntime: true)), t => t with { ConsumerGroup = "person-adapter" });
        var result = await Run(Engine(checkpoints: new Checkpoints((_, g) => { looked.Add(g); return Checkpoint(); })), catalog);
        looked.Should().Equal("person-adapter");
        TopicCheck(result, "Person", "cfg-consumer-group").Status.Should().Be(IntegrationCheckStatus.Pass);
    }

    [Fact]
    public async Task AMissingTelemetrySourceDoesNotBlockMetadataGroupsOrCheckpoints()
    {
        var groups = new Groups(_ => EvidenceResult<ConsumerGroupList>.Available(IntegrationEvidenceSource.AzureResourceManager, new ConsumerGroupList(["$Default"])));
        var result = await Run(Engine(metadata: new Metadata(_ => Hub()), groups: groups, checkpoints: new Checkpoints((_, _) => Checkpoint())), OnlyPerson(await DevCatalog(seededRuntime: true)));
        result.EvidenceAdapters.Single(a => a.Adapter.StartsWith("Event Hub metadata")).State.Should().Be(IntegrationEvidenceState.Available);
        result.EvidenceAdapters.Single(a => a.Adapter.StartsWith("Consumer groups")).State.Should().Be(IntegrationEvidenceState.Available);
        result.EvidenceAdapters.Single(a => a.Adapter.StartsWith("Consumer checkpoints")).State.Should().Be(IntegrationEvidenceState.Available);
        result.EvidenceAdapters.Single(a => a.Adapter.StartsWith("Consumer telemetry")).State.Should().Be(IntegrationEvidenceState.NotConfigured);
        TopicCheck(result, "Person", "rel-checkpoint").Status.Should().Be(IntegrationCheckStatus.Observed);
    }

    [Fact]
    public async Task Readiness_NamesTheAssumptionInsteadOfUnknownGroups()
    {
        var readiness = Engine(checkpoints: new Checkpoints((_, _) => Checkpoint())).Readiness(await DevCatalog(seededRuntime: true), IntegrationContractSet.Empty);
        var system = readiness.Systems.Single(s => s.Kind == IntegrationKind.EventHub);
        system.ConsumerGroupsAssumed.Should().Be(16);
        system.ConsumerGroupsUnknown.Should().Be(0);
        system.ExpectedConsumerGroup.Should().Be("$Default");
        readiness.Reasons.Should().Contain(r => r.Contains("Consumer group $Default is a configured assumption for 16 topics"));
    }
}
