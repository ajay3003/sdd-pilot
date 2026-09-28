using BirkNext.Api.Controllers;
using BirkNext.Api.Data;
using BirkNext.Api.Services.Integrations;
using BirkNext.Integrations;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace BirkNext.Api.Tests.Services.Integrations;

public sealed class IntegrationMappingEvidenceTests
{
    private static IntegrationDefinition Topic => M2lbDevIntegrationSeed.Integrations("dev", DateTimeOffset.UtcNow).Single(i => i.SourceResource!.EndsWith(".Tiltak"));
    private static IntegrationPlatform Platform => M2lbDevIntegrationSeed.Platform("dev", DateTimeOffset.UtcNow);
    private static IntegrationMappingEvidenceService Service(Sources source, bool enabled = true) => new(
        new IntegrationAzureCredential(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["IntegrationReview:Azure:Enabled"] = enabled.ToString() }).Build()), source, source, source, source);

    private sealed class Sources : IEventHubMetadataSource, IEventHubConsumerGroupSource, ICheckpointEvidenceSource, ITelemetryEvidenceSource
    {
        public int Calls;
        public int CheckpointCalls;
        public bool Exists = true;
        public DateTimeOffset At = DateTimeOffset.UtcNow;
        public IntegrationEvidenceState State = IntegrationEvidenceState.Available;
        public IntegrationEvidenceAdapterStatus Describe(IntegrationPlatform platform) => new() { State = State, Reason = "Source not configured", CapturedAt = At };
        public Task<EvidenceResult<EventHubRuntimeMetadata>> GetHubAsync(IntegrationPlatform p, string hub, CancellationToken ct)
        { Calls++; return Task.FromResult(new EvidenceResult<EventHubRuntimeMetadata>(IntegrationEvidenceState.Available, IntegrationEvidenceSource.AzureMetadata, "", At, new(Exists, [new("0", 10, At, false)]))); }
        public Task<EvidenceResult<ConsumerGroupList>> ListAsync(IntegrationPlatform p, string hub, CancellationToken ct)
        { Calls++; return Task.FromResult(new EvidenceResult<ConsumerGroupList>(IntegrationEvidenceState.Available, IntegrationEvidenceSource.AzureResourceManager, "", At, new([]))); }
        public Task<EvidenceResult<CheckpointEvidence>> GetAsync(IntegrationPlatform p, string hub, string group, CancellationToken ct)
        { Calls++; CheckpointCalls++; return Task.FromResult(new EvidenceResult<CheckpointEvidence>(IntegrationEvidenceState.Available, IntegrationEvidenceSource.CheckpointStore, "", At, new(group, [new("0", 5, 5, At)], 1))); }
        public Task<EvidenceResult<ConsumerTelemetry>> GetConsumerAsync(IntegrationPlatform p, string role, int window, CancellationToken ct)
        { Calls++; return Task.FromResult(new EvidenceResult<ConsumerTelemetry>(IntegrationEvidenceState.Available, IntegrationEvidenceSource.ApplicationInsights, "", At, new(0, 0, 0, 0, 0, 10, 10, 5, 0, 1, At, null, window))); }
    }

    [Fact]
    public async Task AzureDisabled_PerformsNoReadsAndLeavesMappingUnchanged()
    {
        var sources = new Sources(); var topic = Topic;
        var result = await Service(sources, false).CheckAsync(topic, Platform, IntegrationContractSet.Empty);
        result.OverallState.Should().Be(IntegrationMappingEvidenceState.NotTestable);
        result.Checks.Where(c => new[] { "topic", "groups", "checkpoint", "telemetry" }.Contains(c.CheckId))
            .Should().OnlyContain(c => c.MissingReason!.Contains("IntegrationReview:Azure:Enabled"));
        sources.Calls.Should().Be(0);
        topic.Consumer.MappingState.Should().Be(ConsumerMappingState.Suggested);
        topic.ConsumerGroup.Should().BeNull();
    }

    [Fact]
    public async Task NoConsumer_PerformsNoReadsAndExplainsWhy()
    {
        var sources = new Sources();
        var result = await Service(sources).CheckAsync(Topic with { Consumer = new() }, Platform, IntegrationContractSet.Empty);
        result.OverallState.Should().Be(IntegrationMappingEvidenceState.NotTestable);
        sources.Calls.Should().Be(0);
        result.Checks.Should().Contain(c => c.MissingReason != null && c.MissingReason.Contains("No consumer"));
    }

    [Fact]
    public async Task SupportingExistenceIsPartial_NotStrong_NotConfirmed()
    {
        var result = await Service(new()).CheckAsync(Topic, Platform, IntegrationContractSet.Empty);
        result.OverallState.Should().Be(IntegrationMappingEvidenceState.PartialEvidence);
        result.Checks.Single(c => c.CheckId == "rbac").State.Should().Be(IntegrationEvidenceState.NotSupported);
        result.Checks.Single(c => c.CheckId == "relationship").MissingReason.Should().Contain("No direct relationship evidence");
        Topic.Consumer.MappingState.Should().Be(ConsumerMappingState.Suggested);
    }

    [Fact]
    public async Task NoGroupMeansNoCheckpointReadAndUnknownLag_NoDefault()
    {
        var sources = new Sources();
        var result = await Service(sources).CheckAsync(Topic, Platform, IntegrationContractSet.Empty);
        sources.CheckpointCalls.Should().Be(0);
        result.Checks.Single(c => c.CheckId == "checkpoint").MissingReason.Should().Contain("Consumer group not configured");
        result.Checks.Single(c => c.CheckId == "lag").State.Should().Be(IntegrationEvidenceState.Unavailable);
        result.Checks.Should().NotContain(c => c.Summary.Contains("$Default"));
        result.Checks.Single(c => c.CheckId == "telemetry").MissingReason.Should().Contain("application identifier");
    }

    [Fact]
    public async Task CurrentGroupCheckpointsAndRoleTelemetryStillDoNotProveExactConsumer()
    {
        var topic = Topic with { ConsumerGroup = "test-group", Consumer = Topic.Consumer with { ContainerApp = "app" } };
        var result = await Service(new()).CheckAsync(topic, Platform, IntegrationContractSet.Empty);
        result.OverallState.Should().Be(IntegrationMappingEvidenceState.PartialEvidence);
        result.Checks.Single(c => c.CheckId == "lag").Summary.Should().Contain("5 events");
        result.Checks.Single(c => c.CheckId == "telemetry").Summary.Should().Contain("not an exact topic correlation");
        topic.Consumer.MappingState.Should().Be(ConsumerMappingState.Suggested);
    }

    [Fact]
    public async Task StaleEvidenceIsHistoricalAndCannotBecomeCurrentStrongEvidenceOrLag()
    {
        var topic = Topic with { ConsumerGroup = "test-group", Consumer = Topic.Consumer with { ContainerApp = "app" } };
        var result = await Service(new() { At = DateTimeOffset.UtcNow.AddDays(-3) }).CheckAsync(topic, Platform, IntegrationContractSet.Empty);
        result.OverallState.Should().Be(IntegrationMappingEvidenceState.NoSupportingEvidence);
        result.Checks.Single(c => c.CheckId == "checkpoint").Freshness.Should().Be(IntegrationEvidenceItemFreshness.Historical);
        result.Checks.Single(c => c.CheckId == "telemetry").Freshness.Should().Be(IntegrationEvidenceItemFreshness.Historical);
        result.Checks.Single(c => c.CheckId == "lag").State.Should().Be(IntegrationEvidenceState.Unavailable);
    }

    [Theory]
    [InlineData(IntegrationEvidenceState.NotConfigured)]
    [InlineData(IntegrationEvidenceState.NotAuthorized)]
    [InlineData(IntegrationEvidenceState.Unavailable)]
    [InlineData(IntegrationEvidenceState.NotSupported)]
    public async Task MissingReasonsKeepTheirTypedState(IntegrationEvidenceState state)
    {
        var sources = new Sources { State = state };
        var result = await Service(sources).CheckAsync(Topic, Platform, IntegrationContractSet.Empty);
        result.OverallState.Should().Be(IntegrationMappingEvidenceState.NotTestable);
        result.Checks.Single(c => c.CheckId == "topic").State.Should().Be(state);
        sources.Calls.Should().Be(0);
    }

    [Fact]
    public async Task ReadableSourcesWithNoSupportingObservationsAreNotAMappingFailure()
    {
        var result = await Service(new() { Exists = false }).CheckAsync(Topic, Platform, IntegrationContractSet.Empty);
        result.OverallState.Should().Be(IntegrationMappingEvidenceState.NoSupportingEvidence);
        result.ManualFollowUp.Should().Contain(s => s.Contains("does not mean the mapping is wrong"));
    }

    [Fact]
    public async Task CheckDoesNotPersist_OnlyExplicitCatalogUpdateConfirms()
    {
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var catalog = new IntegrationCatalogService(db, NullLogger<IntegrationCatalogService>.Instance);
        var created = await catalog.CreateAsync("dev", Topic);
        await Service(new()).CheckAsync(created, Platform, IntegrationContractSet.Empty);
        (await catalog.GetAsync("dev", null, null)).Integrations.Single().Consumer.MappingState.Should().Be(ConsumerMappingState.Suggested);
        var at = DateTimeOffset.UtcNow;
        await catalog.UpdateAsync("dev", created.Id, created with { Consumer = created.Consumer with { MappingState = ConsumerMappingState.Confirmed, MappingConfirmedAt = at, MappingEvidence = "Explicit confirmation by a person", MappingSource = "Target Environment Integrations" } });
        var saved = (await catalog.GetAsync("dev", null, null)).Integrations.Single();
        saved.Consumer.MappingState.Should().Be(ConsumerMappingState.Confirmed);
        saved.Consumer.MappingConfirmedAt.Should().Be(at);
    }

    private static IntegrationMappingEvidenceItem Item(string id, IntegrationEvidenceItemFreshness freshness, bool supports = false, bool direct = false,
        IntegrationEvidenceState state = IntegrationEvidenceState.Available) =>
        new() { CheckId = id, State = state, Freshness = freshness, SupportsMapping = supports, DirectRelationship = direct };

    [Fact]
    public void StrongEvidenceNeedsCurrentDirectRelationship_AndNeverChangesProvenance()
    {
        var topic = Topic;
        IReadOnlyList<IntegrationMappingEvidenceItem> direct = [Item("topic", IntegrationEvidenceItemFreshness.Current, supports: true),
            Item("subscription", IntegrationEvidenceItemFreshness.Current, supports: true, direct: true)];
        IntegrationMappingEvidenceService.Classify(direct, blocked: false).Should().Be(IntegrationMappingEvidenceState.StrongEvidence);
        topic.Consumer.MappingState.Should().Be(ConsumerMappingState.Suggested);

        // Reachability, topic existence, app existence and namespace receiver rights are supporting only.
        IReadOnlyList<IntegrationMappingEvidenceItem> supporting = [Item("topic", IntegrationEvidenceItemFreshness.Current, supports: true),
            Item("groups", IntegrationEvidenceItemFreshness.Current, supports: true), Item("telemetry", IntegrationEvidenceItemFreshness.Recent, supports: true),
            Item("identity", IntegrationEvidenceItemFreshness.Unknown), Item("consumer", IntegrationEvidenceItemFreshness.Unknown)];
        IntegrationMappingEvidenceService.Classify(supporting, blocked: false).Should().Be(IntegrationMappingEvidenceState.PartialEvidence);

        // Blocked by the Azure gate: nothing gathered is Not testable, even with a direct item present.
        IntegrationMappingEvidenceService.Classify(direct, blocked: true).Should().Be(IntegrationMappingEvidenceState.NotTestable);
    }

    [Theory]
    [InlineData(IntegrationEvidenceItemFreshness.Historical)]
    [InlineData(IntegrationEvidenceItemFreshness.Stale)]
    [InlineData(IntegrationEvidenceItemFreshness.Unknown)]
    public void StaleDirectEvidenceStaysStaleAndCannotBeStrong(IntegrationEvidenceItemFreshness freshness)
    {
        IReadOnlyList<IntegrationMappingEvidenceItem> checks = [Item("checkpoint", freshness, supports: true, direct: true)];
        IntegrationMappingEvidenceService.Classify(checks, blocked: false).Should().Be(IntegrationMappingEvidenceState.NoSupportingEvidence);
        checks[0].Freshness.Should().Be(freshness);
    }

    [Fact]
    public async Task RuntimeReadsNeverEmitDirectRelationshipInThisBuild()
    {
        var topic = Topic with { ConsumerGroup = "test-group", Consumer = Topic.Consumer with { ContainerApp = "app" } };
        var result = await Service(new()).CheckAsync(topic, Platform, IntegrationContractSet.Empty);
        result.Checks.Should().NotContain(c => c.DirectRelationship);
        result.OverallState.Should().NotBe(IntegrationMappingEvidenceState.StrongEvidence);
    }

    [Fact]
    public async Task MissingTelemetryIsNotConfigured_NeverNoIssuesObserved()
    {
        var result = await Service(new(), enabled: false).CheckAsync(Topic, Platform, IntegrationContractSet.Empty);
        var telemetry = result.Checks.Single(c => c.CheckId == "telemetry");
        telemetry.State.Should().Be(IntegrationEvidenceState.NotConfigured);
        result.Checks.Should().NotContain(c => c.Summary.Contains("No issues", StringComparison.OrdinalIgnoreCase));
        result.Checks.Single(c => c.CheckId == "lag").Summary.Should().Contain("not zero");
    }

    [Fact]
    public void OrchestratorHasNoWriteReceiveOrCatalogMutationPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "backend/BirkNext.Api/Services/Integrations/IntegrationMappingEvidenceService.cs"))) dir = dir.Parent;
        var source = File.ReadAllText(Path.Combine(dir!.FullName, "backend/BirkNext.Api/Services/Integrations/IntegrationMappingEvidenceService.cs"));
        source.Should().NotContainAny("EventHubProducerClient", "SendAsync", "ReceiveAsync", "ReadEvents", "CreateConsumerGroup", "UpdateCheckpoint", "SetMetadata", "Upload", "SaveChanges", "UpdateAsync", "HttpClient", "ProbeAsync");
    }
}
