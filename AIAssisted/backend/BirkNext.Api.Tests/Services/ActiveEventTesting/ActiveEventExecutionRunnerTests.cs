using System.Security.Cryptography;
using System.Text;
using BirkNext.Api.Services.ActiveEventTesting;
using BirkNext.Api.Services.ActiveCdcTests;
using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.Api.Tests.Services.ActiveCdcTests;
using BirkNext.Integrations;
using FluentAssertions;

namespace BirkNext.Api.Tests.Services.ActiveEventTesting;

public sealed class ActiveEventExecutionRunnerTests
{
    [Fact]
    public async Task M2lbPersonAndOrders_UseTheSameFullRunnerEntryPointAndGenericResult()
    {
        await using var harness = new ActiveCdcTestHarness();
        await harness.AddSnapshotAsync();
        var policy = new ActiveCdcPolicy(harness.Options);
        var provider = new M2lbPersonScenarioProvider(new IqrSourceStore(harness.Db()), policy, harness.Store, TimeProvider.System);
        var context = new ActiveEventProjectEvidenceContext(ActiveCdcTestHarness.Env, harness.Catalog.Integration, harness.Catalog.Platform);
        var descriptor = provider.GetScenarios(context).Single(item => item.ScenarioId == ActiveCdcScenarioCatalog.NormalPersonId);
        var preparation = await provider.PrepareAsync(descriptor.ScenarioId, context, null, default);
        var target = new ActiveEventTrustedTarget
        {
            TargetEnvironmentId = ActiveCdcTestHarness.Env, EnvironmentType = "Development", IntegrationId = ActiveCdcTestHarness.IntegrationId,
            IntegrationType = "EventHub", TransportType = "EventHub", Endpoint = ActiveCdcTestHarness.Fqdn, Resource = ActiveCdcTestHarness.Hub,
        };
        var eventHub = new CapturingTransport("EventHub");
        var runner = new ActiveEventExecutionRunner(new ActiveEventTransportRegistry([eventHub]), [], [], [], TimeProvider.System);

        var result = await runner.ExecuteAsync(provider, descriptor, preparation, target, preparation.Contract, Guid.NewGuid(),
            TimeSpan.FromSeconds(2), TimeSpan.Zero, default);
        var history = result.ToHistoryResult(preparation.Contract);

        result.Events.Should().ContainSingle();
        eventHub.Sent.Should().ContainSingle();
        history.Scenario.Should().BeEquivalentTo(descriptor);
        history.Events.Should().ContainSingle();
        history.Events.Single().BodySha256.Should().Be(result.Events.Single().BodySha256);
        history.Status.Should().Be(ActiveEventRunStatus.CompletedWithLimitedEvidence);
        System.Text.Json.JsonSerializer.Serialize(history).Should().NotContain("\"Body\"").And.NotContain("\"payload\"");
    }

    [Fact]
    public async Task ProductionLifecycle_PersistsTheGenericResultAndUsesTheFullRunner()
    {
        await using var harness = new ActiveCdcTestHarness();
        await harness.AddSnapshotAsync();
        var lifecycle = harness.Lifecycle();
        var ready = await lifecycle.ReadinessAsync(ActiveCdcTestHarness.Env, ActiveCdcTestHarness.IntegrationId,
            M2lbPersonActiveEventAdapter.ExtensionId, ActiveCdcScenarioCatalog.NormalPersonId, null, default);
        ready.CanRun.Should().BeTrue();

        var started = await lifecycle.StartAsync(new ActiveEventRunRequest
        {
            TargetEnvironmentId = ActiveCdcTestHarness.Env,
            IntegrationId = ActiveCdcTestHarness.IntegrationId,
            ExtensionId = M2lbPersonActiveEventAdapter.ExtensionId,
            ScenarioId = ActiveCdcScenarioCatalog.NormalPersonId,
            Confirmed = true,
            ConfirmedDestination = ActiveCdcTestHarness.Hub,
        }, default);
        started.Status.Should().Be(ActiveEventRunStatus.Running);
        await harness.Coordinator.Completion(started.RunId).WaitAsync(TimeSpan.FromSeconds(10));

        var history = await lifecycle.GetAsync(started.RunId, default);
        history.Should().NotBeNull();
        history!.Scenario.ExtensionId.Should().Be(M2lbPersonActiveEventAdapter.ExtensionId);
        history.Status.Should().Be(ActiveEventRunStatus.CompletedWithLimitedEvidence);
        history.Events.Should().ContainSingle();
        harness.Producers.Sent.Should().ContainSingle();
        System.Text.Json.JsonSerializer.Serialize(history).Should().NotContain("\"Body\"").And.NotContain("\"payload\"");
    }

    [Fact]
    public async Task UnrelatedOrdersExtension_UsesTheGenericRunnerAndFakeTransport()
    {
        var provider = new ExampleOrdersScenarioProvider();
        var transport = new ExampleBrokerTransport();
        var runner = new ActiveEventExecutionRunner(new ActiveEventTransportRegistry([transport]), [], [], [], TimeProvider.System);
        var target = new ActiveEventTrustedTarget
        {
            TargetEnvironmentId = "test-orders", EnvironmentType = "Development", IntegrationId = "orders-integration",
            IntegrationType = "ExampleBroker", TransportType = "ExampleBroker", Endpoint = "fake://orders", Resource = "orders-test",
        };
        var descriptor = provider.GetScenarios(new("test-orders", null, null)).Single();
        var context = new ActiveEventProjectEvidenceContext("test-orders", null, null);
        var preparation = await provider.PrepareAsync(descriptor.ScenarioId, context, null, default);
        var result = await runner.ExecuteAsync(provider, descriptor, preparation, target, new(), Guid.NewGuid(), TimeSpan.FromSeconds(1), TimeSpan.Zero, default);

        result.Events.Should().ContainSingle();
        result.Evidence.Where(e => e.Stage is ActiveEventEvidenceStage.Generated or ActiveEventEvidenceStage.SendAttempted or ActiveEventEvidenceStage.TransportAccepted)
            .Select(e => (e.Stage, e.Status)).Should().Equal(
                (ActiveEventEvidenceStage.Generated, ActiveEventEvidenceStatus.Observed),
                (ActiveEventEvidenceStage.SendAttempted, ActiveEventEvidenceStatus.Observed),
                (ActiveEventEvidenceStage.TransportAccepted, ActiveEventEvidenceStatus.Observed));
        transport.Sent.Should().ContainSingle().Which.Should().BeEquivalentTo(result.Events.Single());
        Encoding.UTF8.GetString(transport.Sent.Single().Body).Should().Contain("OrderId").And.Contain("ORD-TEST-").And.NotContain("Person");
        result.Evidence.Single(e => e.Stage == ActiveEventEvidenceStage.DownstreamPersistenceVerified).Status.Should().Be(ActiveEventEvidenceStatus.NotVerified);
    }

    [Fact]
    public async Task ProductionLifecycle_OrdersUsesTheSameGenericRunnerAndHistoryContract()
    {
        await using var harness = new ActiveCdcTestHarness();
        var provider = new ExampleOrdersScenarioProvider(useEventHub: true);
        var lifecycle = harness.Lifecycle(provider);
        var descriptor = (await lifecycle.ScenariosAsync(ActiveCdcTestHarness.Env, ActiveCdcTestHarness.IntegrationId, default))
            .Single(item => item.ExtensionId == provider.ExtensionId);

        var readiness = await lifecycle.ReadinessAsync(ActiveCdcTestHarness.Env, ActiveCdcTestHarness.IntegrationId,
            descriptor.ExtensionId, descriptor.ScenarioId, null, default);
        readiness.CanRun.Should().BeTrue();
        var started = await lifecycle.StartAsync(new ActiveEventRunRequest
        {
            TargetEnvironmentId = ActiveCdcTestHarness.Env,
            IntegrationId = ActiveCdcTestHarness.IntegrationId,
            ExtensionId = descriptor.ExtensionId,
            ScenarioId = descriptor.ScenarioId,
            Confirmed = true,
            ConfirmedDestination = ActiveCdcTestHarness.Hub,
        }, default);
        await harness.Coordinator.Completion(started.RunId).WaitAsync(TimeSpan.FromSeconds(10));

        var history = await lifecycle.GetAsync(started.RunId, default);
        history.Should().NotBeNull();
        history!.GetType().Should().Be(typeof(ActiveEventRunResult));
        history.Scenario.ExtensionId.Should().Be(provider.ExtensionId);
        history.Status.Should().Be(ActiveEventRunStatus.CompletedWithLimitedEvidence);
        history.Evidence.Select(item => item.Stage).Should().Contain([
            ActiveEventEvidenceStage.Generated,
            ActiveEventEvidenceStage.SendAttempted,
            ActiveEventEvidenceStage.TransportAccepted,
            ActiveEventEvidenceStage.ConsumerActivityObserved,
            ActiveEventEvidenceStage.ConsumerContinuityObserved,
            ActiveEventEvidenceStage.DownstreamPersistenceVerified,
        ]);
        history.Events.Should().ContainSingle().Which.SafeMetadata.Should().ContainKey("synthetic");
        harness.Producers.Sent.Should().ContainSingle();
        System.Text.Json.JsonSerializer.Serialize(history).Should().NotContain("syntheticPersonPk").And.NotContain("PersonCdcFixture").And.NotContain("\"Body\"");
    }

    [Fact]
    public async Task LifecycleCancellation_PreservesPersistedProgressAndMarksInFlightSendAmbiguous()
    {
        await using var harness = new ActiveCdcTestHarness();
        await harness.AddSnapshotAsync();
        var sendEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Producers.Behaviour = async (_, ct) =>
        {
            sendEntered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        };
        var lifecycle = harness.Lifecycle();
        var started = await lifecycle.StartAsync(new ActiveEventRunRequest
        {
            TargetEnvironmentId = ActiveCdcTestHarness.Env,
            IntegrationId = ActiveCdcTestHarness.IntegrationId,
            ExtensionId = M2lbPersonActiveEventAdapter.ExtensionId,
            ScenarioId = ActiveCdcScenarioCatalog.NormalPersonId,
            Confirmed = true,
            ConfirmedDestination = ActiveCdcTestHarness.Hub,
        }, default);
        await sendEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        lifecycle.Cancel(started.RunId).Should().BeTrue();
        await harness.Coordinator.Completion(started.RunId).WaitAsync(TimeSpan.FromSeconds(10));

        var history = await lifecycle.GetAsync(started.RunId, default);
        history.Should().NotBeNull();
        history!.Status.Should().Be(ActiveEventRunStatus.Cancelled);
        history.Events.Should().ContainSingle();
        history.Evidence.Select(item => (item.Stage, item.Status)).Should().Contain((ActiveEventEvidenceStage.SendAttempted, ActiveEventEvidenceStatus.Observed));
        history.Evidence.Select(item => (item.Stage, item.Status)).Should().Contain((ActiveEventEvidenceStage.TransportAccepted, ActiveEventEvidenceStatus.Ambiguous));
        history.Evidence.Should().NotContain(item => item.Stage == ActiveEventEvidenceStage.TransportAccepted && item.Status == ActiveEventEvidenceStatus.Observed);
    }

    [Fact]
    public void ScenarioRegistry_RejectsDuplicateStableIds()
    {
        var create = () => new ActiveEventScenarioRegistry([new ExampleOrdersScenarioProvider(), new ExampleOrdersScenarioProvider()])
            .Scenarios(new("test-orders", null, null));
        create.Should().Throw<InvalidOperationException>().WithMessage("*Duplicate Active Event scenario ID*");
    }

    [Fact]
    public void TransportRegistry_RejectsDuplicateProviderTypes()
    {
        var create = () => new ActiveEventTransportRegistry([new ExampleBrokerTransport(), new ExampleBrokerTransport()]);
        create.Should().Throw<InvalidOperationException>().WithMessage("*Duplicate Active Event transport provider*");
    }

    [Fact]
    public void GenericRunnerSource_HasNoDomainSpecificTypeReferences()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "BirkNext.Api", "Services", "ActiveEventTesting", "ActiveEventExecutionRunner.cs");
        if (!File.Exists(path)) return; // The source-level check is exercised in repository builds where sources are present.
        var source = File.ReadAllText(path);
        source.Should().NotContain("PersonPK").And.NotContain("M2LB").And.NotContain("Utdanning");
    }

    private sealed class ExampleOrdersScenarioProvider(bool useEventHub = false) : IActiveEventScenarioProvider
    {
        public string ExtensionId => "example.orders";
        public string ExtensionVersion => "1";
        public bool CanApply(ActiveEventProjectEvidenceContext context) => context.TargetEnvironmentId is "test-orders" or ActiveCdcTestHarness.Env;
        public IReadOnlyList<ActiveEventScenarioDescriptor> GetScenarios(ActiveEventProjectEvidenceContext context) =>
        [new() { ExtensionId = ExtensionId, ExtensionVersion = ExtensionVersion, ScenarioId = "orders.create.synthetic", DisplayName = "Create synthetic order",
            RequiredIntegrationType = useEventHub ? "EventHub" : "ExampleBroker", RequiredTransportType = useEventHub ? "EventHub" : "ExampleBroker", SyntheticDataRequired = true,
            SupportedOperations = [ActiveEventOperation.Create], RequiredSourceEvidence = ["Order contract"] }];
        public Task<ActiveEventScenarioPreparation> PrepareAsync(string scenarioId, ActiveEventProjectEvidenceContext context, Guid? sourceSnapshotId, CancellationToken ct) =>
            Task.FromResult(new ActiveEventScenarioPreparation { Compatible = scenarioId == "orders.create.synthetic", Detail = "Synthetic order contract validated." });
        public Task<ActiveEventScenarioGeneration> GenerateAsync(string scenarioId, Guid runId, ActiveEventTrustedTarget target,
            ActiveEventSourceContractReference contract, CancellationToken ct)
        {
            var body = Encoding.UTF8.GetBytes($"{{\"OrderId\":\"ORD-TEST-{runId:N}\",\"CustomerReference\":\"TEST-CUSTOMER\",\"Amount\":12.50}}");
            var hash = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();
            var eventId = $"{runId:N}-0";
            return Task.FromResult(new ActiveEventScenarioGeneration
            {
                Events = [new() { EventId = eventId, ExtensionId = ExtensionId, ScenarioId = scenarioId, Operation = ActiveEventOperation.Create,
                    Body = body, BodyBytes = body.Length, BodySha256 = hash,
                    Correlation = new() { RunId = runId, EventId = eventId, EventFingerprint = hash, SyntheticMarker = "ORD-TEST" },
                    SafeDisplayMetadata = new Dictionary<string, string> { ["synthetic"] = "true" } }],
            });
        }
    }

    private sealed class ExampleBrokerTransport : IActiveEventTransportProvider
    {
        public string TransportType => "ExampleBroker";
        public List<GeneratedActiveEvent> Sent { get; } = [];
        public bool CanSend(ActiveEventTrustedTarget target, out string reason) { reason = "Fake transport is ready."; return true; }
        public Task<ActiveEventStageEvidence> SendAsync(ActiveEventTrustedTarget target, GeneratedActiveEvent activeEvent, TimeSpan timeout, CancellationToken ct)
        {
            Sent.Add(activeEvent);
            return Task.FromResult(new ActiveEventStageEvidence { Stage = ActiveEventEvidenceStage.TransportAccepted, Status = ActiveEventEvidenceStatus.Observed,
                CorrelationQuality = ActiveEventCorrelationQuality.Strong, Detail = "Fake broker accepted the event.", EvidenceSource = "Example broker test provider" });
        }
    }

    private sealed class CapturingTransport(string transportType) : IActiveEventTransportProvider
    {
        public string TransportType => transportType;
        public List<GeneratedActiveEvent> Sent { get; } = [];
        public bool CanSend(ActiveEventTrustedTarget target, out string reason) { reason = "Test transport is ready."; return true; }
        public Task<ActiveEventStageEvidence> SendAsync(ActiveEventTrustedTarget target, GeneratedActiveEvent activeEvent, TimeSpan timeout, CancellationToken ct)
        {
            Sent.Add(activeEvent);
            return Task.FromResult(new ActiveEventStageEvidence { Stage = ActiveEventEvidenceStage.TransportAccepted, Status = ActiveEventEvidenceStatus.Observed,
                Detail = "Transport accepted the event.", EvidenceSource = "Test transport" });
        }
    }
}
