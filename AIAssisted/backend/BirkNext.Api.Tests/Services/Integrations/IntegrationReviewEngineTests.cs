using System.Text.Json;
using BirkNext.Api.Data;
using BirkNext.Api.Services.Integrations;
using BirkNext.Integrations;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace BirkNext.Api.Tests.Services.Integrations;

/// <summary>
/// Integration Quality Review over the configured catalog: capability-specific readiness, configuration never standing in for
/// runtime evidence, typed Not assessed instead of zeros, grouped platform findings, contract comparison only from contract
/// evidence, no invented thresholds, and a configuration snapshot per run.
/// </summary>
public sealed class IntegrationReviewEngineTests
{
    private const string DevId = "dev-profile";
    private const string DevUrl = "https://m2lbdev.bufetat.no/";

    private sealed class Probe(bool reachable) : IIntegrationNamespaceProbe
    {
        public int Calls { get; private set; }
        public Task<NamespaceProbeResult> ProbeAsync(string fqdn, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new NamespaceProbeResult(true, reachable, reachable, reachable ? "Resolved, TCP 443 connected and TLS established." : "Not reachable (SocketException).", 12, DateTimeOffset.UtcNow));
        }
    }

    private sealed class Runtime(IntegrationEvidenceCapabilities caps, Func<IntegrationDefinition, IntegrationRuntimeEvidence?> evidence) : IIntegrationRuntimeEvidenceSource
    {
        public IntegrationEvidenceCapabilities Capabilities => caps;
        public Task<IReadOnlyDictionary<string, IntegrationRuntimeEvidence>> GetAsync(IntegrationPlatform platform, IReadOnlyList<IntegrationDefinition> integrations, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<string, IntegrationRuntimeEvidence>>(integrations.Select(i => (i.Id, e: evidence(i))).Where(x => x.e is not null).ToDictionary(x => x.Id, x => x.e!));
    }

    private sealed class Contracts(string producer, string consumer) : IIntegrationContractSource
    {
        public bool CanRetrieve => true;
        public Task<IntegrationContractEvidence?> GetAsync(IntegrationDefinition definition, CancellationToken ct) => Task.FromResult<IntegrationContractEvidence?>(new(producer, consumer, "fixture"));
    }

    private static IntegrationReviewEngine Engine(IIntegrationNamespaceProbe? probe = null, IIntegrationRuntimeEvidenceSource? runtime = null, IIntegrationContractSource? contracts = null) =>
        new(probe ?? new Probe(true), runtime ?? new NoRuntimeEvidenceSource(), contracts ?? new NoContractSource(), new HttpClient(), NullLogger<IntegrationReviewEngine>.Instance);

    private static async Task<IntegrationCatalog> DevCatalog(AppDbContext? db = null) =>
        await new IntegrationCatalogService(db ?? Db(), NullLogger<IntegrationCatalogService>.Instance).GetAsync(DevId, "Development", DevUrl);

    private static AppDbContext Db(string? name = null) => new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(name ?? Guid.NewGuid().ToString()).Options);

    private static Task<IntegrationReviewResult> Run(IntegrationReviewEngine engine, IntegrationCatalog catalog) =>
        engine.RunAsync(catalog, new IntegrationReviewRunRequest { EnvironmentId = DevId, EnvironmentName = "M2LB DEV" }, CancellationToken.None);

    private static IntegrationCheck TopicCheck(IntegrationReviewResult result, string table, string checkId) =>
        result.Systems.SelectMany(s => s.Topics).Single(t => t.IntegrationId.EndsWith($"dbo.{table}")).Checks.Single(c => c.CheckId == checkId);

    // ── Readiness ───────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Readiness_IsCapabilitySpecific_AndRunsWithLimitations()
    {
        var readiness = Engine().Readiness(await DevCatalog());
        readiness.CanRun.Should().BeTrue("configuration and connectivity can still run");
        readiness.Headline.Should().Be("Can run with limitations");
        var domains = readiness.Domains.ToDictionary(d => d.Domain, d => d.Readiness);
        domains[IntegrationReviewDomain.Configuration].Should().Be(IntegrationDomainReadiness.Ready);
        domains[IntegrationReviewDomain.Connectivity].Should().Be(IntegrationDomainReadiness.Limited, "namespace probe yes, hub metadata no");
        domains[IntegrationReviewDomain.Contract].Should().Be(IntegrationDomainReadiness.NotAssessable);
        domains[IntegrationReviewDomain.Reliability].Should().Be(IntegrationDomainReadiness.NotAssessable);
        domains[IntegrationReviewDomain.Performance].Should().Be(IntegrationDomainReadiness.NotAssessable);
        readiness.Domains.Single(d => d.Domain == IntegrationReviewDomain.Reliability).Explanation.Should().Contain("Consumer group is not configured");
        var system = readiness.Systems.Should().ContainSingle().Subject;
        system.SystemName.Should().Be("BIRK CDC / Debezium");
        system.Topics.Should().Be(16);
        (system.ConsumersConfirmed, system.ConsumersSuggested, system.ConsumersNeedingConfirmation, system.ConsumerGroupsUnknown).Should().Be((1, 8, 7, 16));
    }

    [Fact]
    public async Task Readiness_WithHubMetadata_ConnectivityAvailable_CheckpointStillLimitedByConsumerGroup()
    {
        var caps = new IntegrationEvidenceCapabilities(true, true, true, false, false, false, "fixture");
        var readiness = Engine(runtime: new Runtime(caps, _ => null)).Readiness(await DevCatalog());
        var domains = readiness.Domains.ToDictionary(d => d.Domain, d => d.Readiness);
        domains[IntegrationReviewDomain.Connectivity].Should().Be(IntegrationDomainReadiness.Available);
        domains[IntegrationReviewDomain.Reliability].Should().Be(IntegrationDomainReadiness.Limited);
        domains[IntegrationReviewDomain.Contract].Should().Be(IntegrationDomainReadiness.NotAssessable);
        readiness.Headline.Should().Be("Can run with limitations");
    }

    [Fact]
    public async Task Readiness_NothingEnabled_CannotRun()
    {
        var catalog = await DevCatalog();
        var disabled = catalog with { Integrations = catalog.Integrations.Select(i => i with { Enabled = false }).ToList() };
        var readiness = Engine().Readiness(disabled);
        readiness.CanRun.Should().BeFalse();
        readiness.Headline.Should().Be("Cannot run");
    }

    // ── Configuration vs runtime ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ConfigurationPass_NeverMakesConnectivityPass_WithoutEvidence()
    {
        var result = await Run(Engine(), await DevCatalog());
        var platform = result.Systems.Single().PlatformChecks;
        platform.Single(c => c.CheckId == "cfg-namespace").Status.Should().Be(IntegrationCheckStatus.Pass);
        TopicCheck(result, "Person", "conn-hub").Status.Should().Be(IntegrationCheckStatus.NotAssessed, "existence is never inferred from saved configuration");
        TopicCheck(result, "Person", "conn-hub").Explanation.Should().Contain("metadata access is not available");
        platform.Single(c => c.CheckId == "conn-namespace").Provenance.Should().Be(IntegrationEvidenceSource.NetworkProbe);
    }

    [Fact]
    public async Task UnknownConsumer_LimitsConsumerChecks_TopicNeverFailed()
    {
        var result = await Run(Engine(), await DevCatalog());
        var kommune = result.Systems.SelectMany(s => s.Topics).Single(t => t.IntegrationId.EndsWith("dbo.Kommune"));
        kommune.Checks.Single(c => c.CheckId == "cfg-consumer").Status.Should().Be(IntegrationCheckStatus.NeedsConfirmation);
        kommune.Checks.Single(c => c.CheckId == "rel-checkpoint").Status.Should().Be(IntegrationCheckStatus.NotAssessed);
        kommune.Checks.Should().NotContain(c => c.Status == IntegrationCheckStatus.Fail);
        kommune.ConfigurationState.Should().Be(IntegrationConfigurationState.NeedsConfirmation);
        result.Systems.Single().PlatformChecks.Single(c => c.CheckId == "conn-namespace").Status.Should().Be(IntegrationCheckStatus.Pass, "connectivity still runs");
    }

    [Fact]
    public async Task UnknownConsumerGroup_IsNotConfigured_CheckpointNotAssessed_NoFinding()
    {
        var result = await Run(Engine(), await DevCatalog());
        TopicCheck(result, "Person", "cfg-consumer-group").Status.Should().Be(IntegrationCheckStatus.NotConfigured);
        TopicCheck(result, "Person", "rel-checkpoint").Status.Should().Be(IntegrationCheckStatus.NotAssessed);
        TopicCheck(result, "Person", "rel-checkpoint").Recommendation.Should().Be("Confirm the consumer group to enable checkpoint/lag review.");
        result.Findings.Should().NotContain(f => f.Title.Contains("consumer group", StringComparison.OrdinalIgnoreCase));
        result.ManualFollowUp.Should().Contain(m => m.Title == "Confirm consumer group(s)" && m.AffectedCount == 16);
    }

    [Fact]
    public async Task NoEvidence_IsNotAssessed_NeverZero()
    {
        var result = await Run(Engine(), await DevCatalog());
        foreach (var id in new[] { "flow-producer", "flow-consumer", "err-deserialization", "perf-latency", "dq-envelope" })
        {
            var check = TopicCheck(result, "Person", id);
            check.Status.Should().Be(IntegrationCheckStatus.NotAssessed, id);
            check.Evidence.Should().NotContain("0 ").And.NotBe("0");
        }
        result.Domains.Single(d => d.Domain == IntegrationReviewDomain.Performance).StateLabel.Should().Be("Not assessed");
        result.Freshness.Should().Be(IntegrationEvidenceFreshness.Current, "the namespace probe is live runtime evidence");
        result.Outcome.Should().Be(IntegrationReviewOutcome.CompletedWithLimitations);
    }

    // ── Findings ────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task NamespaceUnreachable_IsOneFinding_AffectingSixteenTopics()
    {
        var result = await Run(Engine(new Probe(false)), await DevCatalog());
        var finding = result.Findings.Should().ContainSingle(f => f.RuleId == "namespace-unreachable").Subject;
        finding.AffectedIntegrations.Should().HaveCount(16);
        result.Systems.Single().PlatformChecks.Single(c => c.CheckId == "conn-namespace").Status.Should().Be(IntegrationCheckStatus.Fail);
        result.Outcome.Should().Be(IntegrationReviewOutcome.ManualReviewRequired);
    }

    [Fact]
    public async Task HubMissing_IsAFailedCheck_AndOneFindingPerHub()
    {
        var caps = new IntegrationEvidenceCapabilities(true, false, false, false, false, false, "fixture");
        var runtime = new Runtime(caps, i => new IntegrationRuntimeEvidence { Source = IntegrationEvidenceSource.AzureMetadata, CapturedAt = DateTimeOffset.UtcNow, HubExists = !i.Id.EndsWith("dbo.Tiltak") });
        var result = await Run(Engine(runtime: runtime), await DevCatalog());
        TopicCheck(result, "Tiltak", "conn-hub").Status.Should().Be(IntegrationCheckStatus.Fail);
        TopicCheck(result, "Person", "conn-hub").Status.Should().Be(IntegrationCheckStatus.Pass);
        result.Findings.Should().ContainSingle(f => f.RuleId == "hub-missing").Which.Subject.Should().Be("m2lb-cdc-dev.BirkM2LB.dbo.Tiltak");
    }

    [Fact]
    public async Task ContractUnavailable_IsNotAssessed_NoCompatibilityFinding()
    {
        var result = await Run(Engine(), await DevCatalog());
        TopicCheck(result, "Person", "contract-availability").Status.Should().Be(IntegrationCheckStatus.NotConfigured);
        TopicCheck(result, "Person", "contract-compatibility").Status.Should().Be(IntegrationCheckStatus.NotAssessed);
        result.Findings.Should().NotContain(f => f.Domain == IntegrationReviewDomain.Contract);
    }

    [Fact]
    public async Task ContractMismatchFromFixture_FailsAndIsATypedFinding_WithoutLiveActivity()
    {
        const string producer = """{ "properties": { "id": { "type": "string" }, "name": { "type": "string" } }, "required": ["id"] }""";
        const string consumer = """{ "properties": { "id": { "type": "integer" }, "birthDate": { "type": "string" } }, "required": ["id", "birthDate"] }""";
        var catalog = await DevCatalog();
        var withContract = catalog with { Integrations = catalog.Integrations.Select(i => i.Id.EndsWith("dbo.Person") ? i with { ContractRelationship = ContractRelationshipState.BothContractsAvailable, ProducerContractReference = "p", ConsumerContractReference = "c" } : i).ToList() };
        var result = await Run(Engine(contracts: new Contracts(producer, consumer)), withContract);
        TopicCheck(result, "Person", "contract-compatibility").Status.Should().Be(IntegrationCheckStatus.Fail);
        var finding = result.Findings.Should().ContainSingle(f => f.RuleId == "contract-incompatible").Subject;
        finding.Evidence.Should().Contain(e => e.StartsWith("REQUIRED_FIELD_MISSING")).And.Contain(e => e.StartsWith("TYPE_MISMATCH"));
    }

    [Fact]
    public async Task NoRecentActivity_IsNoRecentEvidence_NotFailure()
    {
        var caps = new IntegrationEvidenceCapabilities(false, true, false, false, false, false, "fixture");
        var runtime = new Runtime(caps, _ => new IntegrationRuntimeEvidence { Source = IntegrationEvidenceSource.ApplicationInsights, CapturedAt = DateTimeOffset.UtcNow });
        var result = await Run(Engine(runtime: runtime), await DevCatalog());
        TopicCheck(result, "Person", "flow-producer").Status.Should().Be(IntegrationCheckStatus.NoRecentEvidence);
        result.Findings.Should().NotContain(f => f.Domain == IntegrationReviewDomain.MessageFlow);
    }

    [Fact]
    public async Task StaleCheckpoint_WithoutAPolicyThreshold_IsObserved_NoFinding()
    {
        var caps = new IntegrationEvidenceCapabilities(false, true, true, false, false, false, "fixture");
        var runtime = new Runtime(caps, _ => new IntegrationRuntimeEvidence { Source = IntegrationEvidenceSource.AzureMetadata, CapturedAt = DateTimeOffset.UtcNow, LastEnqueuedAt = DateTimeOffset.UtcNow, CheckpointEventsBehind = 420 });
        var catalog = await DevCatalog();
        var withGroup = catalog with { Integrations = catalog.Integrations.Select(i => i with { ConsumerGroup = "cg" }).ToList() };
        var result = await Run(Engine(runtime: runtime), withGroup);
        var check = TopicCheck(result, "Person", "rel-checkpoint");
        check.Status.Should().Be(IntegrationCheckStatus.Observed);
        check.Evidence.Should().Contain("420 event(s) behind");
        result.Findings.Should().NotContain(f => f.Domain == IntegrationReviewDomain.Reliability, "no staleness threshold exists in policy");
    }

    [Fact]
    public async Task ManagedIdentityConfigured_IsNotRuntimeAuthorization()
    {
        var result = await Run(Engine(), await DevCatalog());
        TopicCheck(result, "Person", "sec-consumer-auth").Status.Should().Be(IntegrationCheckStatus.Pass);
        TopicCheck(result, "Person", "sec-consumer-auth").Evidence.Should().Contain("Managed Identity (id-m2lb-person-adp-dev-nwe)");
        TopicCheck(result, "Person", "sec-runtime-auth").Status.Should().Be(IntegrationCheckStatus.NotAssessed);
        result.Systems.Single().PlatformChecks.Single(c => c.CheckId == "obs-provider").Status.Should().Be(IntegrationCheckStatus.Pass);
        result.Systems.Single().PlatformChecks.Single(c => c.CheckId == "obs-dashboard").Status.Should().Be(IntegrationCheckStatus.NotConfigured);
    }

    [Fact]
    public async Task ReviewDoesNotReadTechnicalTopics()
    {
        var result = await Run(Engine(), await DevCatalog());
        result.TopicsReviewed.Should().Be(16);
        result.Systems.SelectMany(s => s.Topics).Select(t => t.Topic).Should().NotContain(new[] { "connect-configs", "connect-offsets", "connect-status", "schemahistory", "m2lb-cdc-dev" });
    }

    // ── Snapshot / history ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ARunKeepsItsConfigurationSnapshot_AfterLaterEdits()
    {
        var db = Db();
        var catalog = new IntegrationCatalogService(db, NullLogger<IntegrationCatalogService>.Instance);
        var service = new IntegrationReviewService(catalog, Engine(), db, NullLogger<IntegrationReviewService>.Instance);
        var first = await service.RunAsync(new IntegrationReviewRunRequest { EnvironmentId = DevId, EnvironmentName = "M2LB DEV" }, "Development", DevUrl);

        var person = (await catalog.GetAsync(DevId, "Development", DevUrl)).Integrations.Single(i => i.Id.EndsWith("dbo.Person"));
        await catalog.UpdateAsync(DevId, person.Id, person with { ConsumerGroup = "cg-new", TechnicalOwner = "new-owner", Consumer = person.Consumer with { DisplayName = "Renamed Adapter" } });
        var second = await service.RunAsync(new IntegrationReviewRunRequest { EnvironmentId = DevId, EnvironmentName = "M2LB DEV" }, "Development", DevUrl);

        var stored = (await service.GetRunAsync(first.RunId))!;
        var oldPerson = stored.ConfigurationSnapshot.Integrations.Single(i => i.Id == person.Id);
        oldPerson.ConsumerGroup.Should().BeNull();
        oldPerson.Consumer.DisplayName.Should().Be("Person Adapter");
        second.ConfigurationSnapshot.Integrations.Single(i => i.Id == person.Id).ConsumerGroup.Should().Be("cg-new");
        (await service.HistoryAsync(DevId)).Should().HaveCount(2);
    }

    [Fact]
    public async Task ResultAndSnapshotCarryNoSecrets()
    {
        var result = await Run(Engine(), await DevCatalog());
        JsonSerializer.Serialize(result).Should().NotContainAny("SharedAccessKey", "Endpoint=sb://", "AccountKey", "Password=", "client_secret", "Bearer ");
    }
}
