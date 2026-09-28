using System.Net;
using System.Text;
using Azure.Core;
using BirkNext.Api.Data;
using BirkNext.Api.Services.Integrations;
using BirkNext.Api.Services.Integrations.ServiceBus;
using BirkNext.Integrations;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace BirkNext.Api.Tests.Services.Integrations;

/// <summary>
/// Azure Service Bus transport evidence: configured topology vs code routes vs runtime metadata, kept apart from Event Hub and from Wolverine
/// application messaging. Existence is Observed (never Pass), Pass is only an exact property comparison, counts are Observed without invented
/// thresholds, failures stay typed, and the runtime source only ever issues GET requests to Azure Resource Manager.
/// </summary>
public sealed class ServiceBusEvidenceTests
{
    private static IntegrationPlatform Platform(string? subscriptionId = "00000000-0000-0000-0000-000000000001") =>
        M2lbDevIntegrationSeed.ServiceBusPlatform("dev", DateTimeOffset.UtcNow) with
        {
            RuntimeEvidence = subscriptionId is null ? null : new IntegrationRuntimeEvidenceSettings { SubscriptionId = subscriptionId },
        };

    private static MessagingRoute Route(MessagingRouteDirection direction, MessagingEndpointKind kind, string? entity, string? topic = null, string technology = "Wolverine", string? message = "Msg") => new()
    {
        Direction = direction, EndpointKind = kind, EntityName = entity, TopicName = topic, Technology = technology, MessageType = message,
        Endpoint = entity ?? "runtime", EntityNameSource = entity is null ? null : "literal (Program.cs:1)",
    };

    private static ApplicationMessagingEvidenceSet Messaging(params (string App, MessagingRoute Route)[] routes) => new()
    {
        EnvironmentId = "dev", AnalyzerVersion = WolverineSourceAnalyzerVersion,
        Applications = routes.GroupBy(r => r.App).Select(g => new ApplicationMessagingEvidence
        {
            ApplicationId = g.Key, Detection = MessagingDetection.Confirmed,
            Routes = g.Select(x => x.Route).Where(r => r.Technology == "Wolverine").ToList(), SdkRoutes = g.Select(x => x.Route).Where(r => r.Technology != "Wolverine").ToList(),
        }).ToList(),
    };

    private static ServiceBusEntityObservation Observed(ServiceBusEntityExpectation e, Dictionary<string, string?>? properties = null, long? active = 0, long? dead = 0) => new()
    {
        EntityType = e.EntityType, Name = e.Name, Topic = e.Topic, Status = "Active",
        Properties = properties ?? new()
        {
            ["maxDeliveryCount"] = e.MaxDeliveryCount?.ToString(), ["lockDuration"] = e.LockDuration, ["defaultMessageTimeToLive"] = "P14D",
            ["requiresSession"] = "false", ["deadLetteringOnMessageExpiration"] = "true", ["deadLetteringOnFilterEvaluationExceptions"] = "true",
            ["requiresDuplicateDetection"] = "false", ["maxSizeInMegabytes"] = "1024",
        },
        ActiveMessageCount = e.EntityType == ServiceBusEntityType.Topic ? null : active, DeadLetterMessageCount = e.EntityType == ServiceBusEntityType.Topic ? null : dead, ScheduledMessageCount = 0,
    };

    private static ServiceBusRuntimeEvidence Runtime(Func<ServiceBusEntityExpectation, ServiceBusEntityObservation?>? map = null) => new()
    {
        PlatformId = M2lbDevIntegrationSeed.ServiceBusPlatformId, Namespace = M2lbDevIntegrationSeed.ServiceBusNamespace, State = IntegrationEvidenceState.Available,
        CapturedAt = DateTimeOffset.UtcNow, NamespaceStatus = "Active", Sku = "Premium",
        Entities = M2lbDevIntegrationSeed.ServiceBusTopology().Entities.Select(e => map is null ? Observed(e) : map(e)).OfType<ServiceBusEntityObservation>().ToList(),
    };

    private static ServiceBusEvidenceCheck Evaluate(ApplicationMessagingEvidenceSet? messaging, ServiceBusRuntimeEvidence runtime) =>
        ServiceBusEvidenceService.Evaluate(Platform(), M2lbDevIntegrationSeed.ServiceBusTopology(), messaging, runtime, DateTimeOffset.UtcNow, 24);

    private const int WolverineSourceAnalyzerVersion = BirkNext.Api.Services.Integrations.ApplicationMessaging.WolverineSourceAnalyzer.Version;

    [Fact]
    public void AnAnalysisFromBeforeEntityResolutionIsNeverComparedAndAsksForReanalysis()
    {
        var old = Messaging(("M2LB.Hendelse.Api", Route(MessagingRouteDirection.Publish, MessagingEndpointKind.Queue, null))) with { AnalyzerVersion = 0 };
        var check = Evaluate(old, NotConfigured);
        check.Routes.Should().BeEmpty();
        check.Configuration.Should().NotContain(c => c.State == ServiceBusCheckState.NotReferenced, "no false orphans from an analysis without entity names");
        check.Missing.Should().Contain(m => m.Contains("re-analyze"));
    }

    private static readonly ServiceBusRuntimeEvidence NotConfigured = new() { State = IntegrationEvidenceState.NotConfigured, Reason = IntegrationAzureCredential.DisabledMessage };

    // ── Topology and routes ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void SeededTopologyIsDeterministicAndServiceBusShaped()
    {
        var topology = M2lbDevIntegrationSeed.ServiceBusTopology();
        topology.Queues.Select(q => q.Name).Should().Equal("operasjonsregistrering", "leselogg", "operatorkontroll.varsler", "birk-adapter-errors");
        topology.Topics.Should().HaveCount(11);
        topology.Subscriptions.Should().HaveCount(8).And.OnlyContain(s => topology.Topics.Any(t => t.Name == s.Topic));
        topology.Subscriptions.Should().Contain(s => s.Path == "person.barn/tjeneste-barnregistrert");
        topology.Queues.Should().OnlyContain(q => q.MaxDeliveryCount == 10 && q.DefaultMessageTimeToLive == "P14D" && q.LockDuration == "PT1M");
        topology.Source.Should().Contain("Terraform").And.Contain("seeded");
        var platform = M2lbDevIntegrationSeed.ServiceBusPlatform("dev", DateTimeOffset.UtcNow);
        platform.Kind.Should().Be(IntegrationKind.ServiceBus);
        platform.TopicPrefix.Should().BeNull("Event Hub concepts are not reused for Service Bus");
        platform.RuntimeEvidence.Should().BeNull("the Azure subscription id is unknown and is not invented");
    }

    [Fact]
    public void WolverineRouteToConfiguredQueueMatches_UnknownQueueIsAMismatchFinding()
    {
        var check = Evaluate(Messaging(("M2LB.Hendelse.Api", Route(MessagingRouteDirection.Publish, MessagingEndpointKind.Queue, "leselogg", message: "LeseloggHendelseMessage")),
            ("M2LB.Hendelse.Api", Route(MessagingRouteDirection.Publish, MessagingEndpointKind.Queue, "missing-queue"))), NotConfigured);
        var matched = check.Routes.Single(r => r.Entity == "leselogg");
        matched.Configuration.Should().Be(ServiceBusCheckState.Matched);
        matched.Access.Should().Be(ServiceBusCheckState.Matched);
        matched.Detail.Should().Contain("not a message published or processed");
        var missing = check.Routes.Single(r => r.Entity == "missing-queue");
        missing.Configuration.Should().Be(ServiceBusCheckState.Mismatch);
        check.Findings.Should().ContainSingle().Which.Should().Contain("missing-queue");
        check.OverallState.Should().Be(ServiceBusEvidenceState.IssueDetected);
    }

    [Fact]
    public void WolverineKindAndSubscriptionTopicAreExact()
    {
        var check = Evaluate(Messaging(
            ("M2LB.Tjeneste.Api", Route(MessagingRouteDirection.Publish, MessagingEndpointKind.Queue, "tjeneste.tjenester")),
            ("M2LB.Tjeneste.Api", Route(MessagingRouteDirection.Listen, MessagingEndpointKind.Subscription, "tjeneste-barnregistrert", "person.barn")),
            ("M2LB.Tjeneste.Api", Route(MessagingRouteDirection.Listen, MessagingEndpointKind.Subscription, "revisjon", "person.audit"))), NotConfigured);
        check.Routes.Single(r => r.Entity == "tjeneste.tjenester").Detail.Should().Contain("configured topology has it as a topic");
        check.Routes.Single(r => r.Entity == "person.barn/tjeneste-barnregistrert").Configuration.Should().Be(ServiceBusCheckState.Matched);
        check.Routes.Single(r => r.Entity == "person.audit/revisjon").Detail.Should().Be("Subscription revisjon is not configured on topic person.audit.");
    }

    [Fact]
    public void ListenerWithoutAuditedReceiveGrantIsAnAccessMismatch_NamespaceReceiversAreGranted()
    {
        var check = Evaluate(Messaging(
            ("M2LB.Person.Api", Route(MessagingRouteDirection.Listen, MessagingEndpointKind.Queue, "leselogg")),
            ("M2LB.Hendelse.BiRK.Adapter", Route(MessagingRouteDirection.Listen, MessagingEndpointKind.Queue, "leselogg"))), NotConfigured);
        check.Routes.Single(r => r.Application == "M2LB.Person.Api").Access.Should().Be(ServiceBusCheckState.Mismatch);
        check.Routes.Single(r => r.Application == "M2LB.Person.Api").Detail.Should().Contain("grants receive on leselogg to M2LB.Revisjon.Worker, M2LB.Tjeneste.Api");
        check.Routes.Single(r => r.Application == "M2LB.Hendelse.BiRK.Adapter").Access.Should().Be(ServiceBusCheckState.Matched, "adapters are namespace-wide receivers");
    }

    [Fact]
    public void UnreferencedConfiguredEntityIsNotAutomaticallyADefect()
    {
        var check = Evaluate(Messaging(("M2LB.Hendelse.Api", Route(MessagingRouteDirection.Publish, MessagingEndpointKind.Queue, "leselogg"))), NotConfigured);
        var orphan = check.Configuration.Single(c => c.CheckId == "sb-code-reference" && c.Entity == "birk-adapter-errors");
        orphan.State.Should().Be(ServiceBusCheckState.NotReferenced);
        orphan.Detail.Should().Contain("not a defect by itself");
        check.Findings.Should().BeEmpty();
    }

    [Fact]
    public void AzureDisabledStillAssessesTopologyWithoutAnyAzureCall()
    {
        var handler = new Handler(_ => throw new InvalidOperationException("no Azure call expected"));
        var source = new ArmServiceBusMetadataSource(Azure(enabled: false), new HttpClient(handler), NullLogger<ArmServiceBusMetadataSource>.Instance);
        var check = new ServiceBusEvidenceService(source, NullLogger<ServiceBusEvidenceService>.Instance)
            .CheckAsync(Platform(), Messaging(("M2LB.Revisjon.Worker", Route(MessagingRouteDirection.Listen, MessagingEndpointKind.Queue, "leselogg"))), CancellationToken.None).Result;
        handler.Requests.Should().BeEmpty();
        check.Runtime!.State.Should().Be(IntegrationEvidenceState.NotConfigured);
        check.Configuration.Single(c => c.CheckId == "sb-topology").State.Should().Be(ServiceBusCheckState.Configured);
        check.Routes.Single().Configuration.Should().Be(ServiceBusCheckState.Matched);
        check.Routes.Single().Runtime.Should().Be(ServiceBusCheckState.NotAssessed);
        check.OverallState.Should().Be(ServiceBusEvidenceState.Partial, "missing runtime evidence is not a failure");
        check.Missing.Should().Contain(m => m.Contains("IntegrationReview:Azure:Enabled"));
    }

    // ── Runtime metadata ───────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ExistingEntitiesAreObservedNotPass_MissingSubscriptionIsNotFound()
    {
        var check = Evaluate(null, Runtime(e => e.Path == "person.barn/tjeneste-barnregistrert" ? null : Observed(e)));
        check.RuntimeChecks.Where(c => c.CheckId == "sb-entity" && c.Entity == "leselogg").Should().ContainSingle().Which.State.Should().Be(ServiceBusCheckState.Observed);
        var missing = check.RuntimeChecks.Single(c => c.CheckId == "sb-entity" && c.State == ServiceBusCheckState.NotFound);
        missing.Entity.Should().Be("person.barn/tjeneste-barnregistrert");
        check.Findings.Should().ContainSingle(f => f.Contains("tjeneste-barnregistrert"));
        check.RuntimeChecks.Where(c => c.CheckId == "sb-entity").Should().NotContain(c => c.State == ServiceBusCheckState.Pass);
    }

    [Fact]
    public void PropertiesPassOnlyOnExactExpectation_MismatchIsReported_UnknownIsNotAssessed()
    {
        var check = Evaluate(null, Runtime(e => e.Name == "leselogg" ? Observed(e) with { Properties = new() { ["maxDeliveryCount"] = "5", ["lockDuration"] = "PT60S" } } : Observed(e)));
        var leselogg = check.RuntimeChecks.Where(c => c.Entity == "leselogg").ToList();
        leselogg.Single(c => c.CheckId == "sb-property-maxDeliveryCount").State.Should().Be(ServiceBusCheckState.Mismatch);
        leselogg.Single(c => c.CheckId == "sb-property-lockDuration").State.Should().Be(ServiceBusCheckState.Pass, "PT60S and PT1M are the same duration");
        leselogg.Single(c => c.CheckId == "sb-property-defaultMessageTimeToLive").State.Should().Be(ServiceBusCheckState.NotAssessed, "Azure did not report it");
        check.RuntimeChecks.Single(c => c.CheckId == "sb-property-maxDeliveryCount" && c.Entity == "operasjonsregistrering").State.Should().Be(ServiceBusCheckState.Pass);
        // A topic has no MaxDeliveryCount expectation, so none is compared.
        check.RuntimeChecks.Should().NotContain(c => c.EntityType == ServiceBusEntityType.Topic && c.CheckId == "sb-property-maxDeliveryCount");
    }

    [Theory]
    [InlineData(0L, 0L)]
    [InlineData(50L, 3L)]
    public void CountsAreObservedWithoutInventedThresholds(long active, long dead)
    {
        var check = Evaluate(null, Runtime(e => Observed(e, active: active, dead: dead)));
        var counts = check.RuntimeChecks.Single(c => c.CheckId == "sb-counts" && c.Entity == "leselogg");
        counts.State.Should().Be(ServiceBusCheckState.Observed);
        counts.Observed.Should().Contain($"active {active}").And.Contain($"dead-letter {dead}");
        counts.Detail.Should().Contain(dead > 0 ? "Dead-letter messages observed (3)" : "Zero active messages is not a pass");
        check.Findings.Should().BeEmpty("counts without a configured threshold are never judged");
        var (reviewChecks, findings) = ServiceBusEvidenceService.ReviewChecks(Platform(), check);
        reviewChecks.Single(c => c.CheckId == "sb-dead-letter").Status.Should().Be(IntegrationCheckStatus.Observed);
        findings.Should().BeEmpty();
    }

    [Fact]
    public void StaleSnapshotIsNotCurrent()
    {
        var check = Evaluate(null, Runtime() with { CapturedAt = DateTimeOffset.UtcNow.AddDays(-2) });
        ServiceBusEvidenceService.IsStale(check, DateTimeOffset.UtcNow).Should().BeTrue();
        ServiceBusEvidenceService.IsStale(Evaluate(null, Runtime()), DateTimeOffset.UtcNow).Should().BeFalse();
    }

    // ── ARM source: typed failures, GET only ───────────────────────────────────────────────────────────────────────

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Url)> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request.Method, request.RequestUri!.ToString()));
            return Task.FromResult(respond(request));
        }
    }

    private sealed class Token : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext context, CancellationToken ct) => new("test-token", DateTimeOffset.UtcNow.AddHours(1));
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext context, CancellationToken ct) => new(GetToken(context, ct));
    }

    private sealed class FakeAzure(bool enabled) : IIntegrationAzureCredential
    {
        public TokenCredential? Credential { get; } = enabled ? new Token() : null;
        public string DisabledReason => IntegrationAzureCredential.DisabledMessage;
    }

    private static IIntegrationAzureCredential Azure(bool enabled) => new FakeAzure(enabled);

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static ArmServiceBusMetadataSource Arm(Handler handler) => new(Azure(true), new HttpClient(handler), NullLogger<ArmServiceBusMetadataSource>.Instance);

    [Fact]
    public async Task ArmReadsMetadataWithGetOnlyAndParsesCountDetails()
    {
        var handler = new Handler(r => r.RequestUri!.AbsolutePath switch
        {
            var p when p.EndsWith("/subscriptions", StringComparison.Ordinal) => Json("""{"value":[{"name":"tjeneste-barnregistrert","properties":{"maxDeliveryCount":10,"lockDuration":"PT1M","status":"Active","countDetails":{"activeMessageCount":2,"deadLetterMessageCount":1}}}]}"""),
            var p when p.EndsWith("/topics", StringComparison.Ordinal) => Json("""{"value":[{"name":"person.barn","properties":{"status":"Active","defaultMessageTimeToLive":"P14D"}}]}"""),
            var p when p.EndsWith("/queues", StringComparison.Ordinal) => Json("""{"value":[{"name":"leselogg","properties":{"maxDeliveryCount":10,"requiresSession":false,"status":"Active","countDetails":{"activeMessageCount":12,"deadLetterMessageCount":2,"scheduledMessageCount":0,"transferMessageCount":0,"transferDeadLetterMessageCount":0}}}]}"""),
            _ => Json("""{"name":"sbns-m2lb-dev-nwe-001","sku":{"name":"Premium"},"properties":{"status":"Active"}}"""),
        });
        var runtime = await Arm(handler).ReadAsync(Platform(), CancellationToken.None);
        runtime.State.Should().Be(IntegrationEvidenceState.Available);
        runtime.Sku.Should().Be("Premium");
        var queue = runtime.Entities.Single(e => e.Name == "leselogg");
        queue.ActiveMessageCount.Should().Be(12);
        queue.DeadLetterMessageCount.Should().Be(2);
        queue.Properties["requiresSession"].Should().Be("false");
        runtime.Entities.Single(e => e.EntityType == ServiceBusEntityType.Subscription).Topic.Should().Be("person.barn");
        handler.Requests.Should().OnlyContain(r => r.Method == HttpMethod.Get && r.Url.StartsWith("https://management.azure.com/subscriptions/", StringComparison.Ordinal))
            .And.Contain(r => r.Url.Contains("/providers/Microsoft.ServiceBus/namespaces/sbns-m2lb-dev-nwe-001/queues?api-version=", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, IntegrationEvidenceState.NotAuthorized)]
    [InlineData(HttpStatusCode.Unauthorized, IntegrationEvidenceState.NotAuthorized)]
    [InlineData(HttpStatusCode.NotFound, IntegrationEvidenceState.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError, IntegrationEvidenceState.Error)]
    public async Task NamespaceFailuresStayTyped(HttpStatusCode status, IntegrationEvidenceState expected)
    {
        var runtime = await Arm(new Handler(_ => Json("{}", status))).ReadAsync(Platform(), CancellationToken.None);
        runtime.State.Should().Be(expected);
        runtime.Entities.Should().BeEmpty();
        var check = Evaluate(null, runtime);
        var (reviewChecks, findings) = ServiceBusEvidenceService.ReviewChecks(Platform(), check);
        reviewChecks.Single(c => c.CheckId == "sb-namespace").Status.Should().Be(expected == IntegrationEvidenceState.NotFound ? IntegrationCheckStatus.Fail : IntegrationCheckStatus.Unavailable);
        findings.Should().HaveCount(expected == IntegrationEvidenceState.NotFound ? 1 : 0, "not authorized is never reported as a missing namespace");
    }

    [Fact]
    public async Task NetworkFailureIsUnavailableNotError()
    {
        var runtime = await Arm(new Handler(_ => throw new HttpRequestException("no route"))).ReadAsync(Platform(), CancellationToken.None);
        runtime.State.Should().Be(IntegrationEvidenceState.Unavailable);
    }

    [Fact]
    public async Task MissingSubscriptionIdIsNotConfiguredWithoutACall()
    {
        var handler = new Handler(_ => throw new InvalidOperationException("no call"));
        var runtime = await Arm(handler).ReadAsync(Platform(subscriptionId: null), CancellationToken.None);
        runtime.State.Should().Be(IntegrationEvidenceState.NotConfigured);
        runtime.Reason.Should().Contain("subscription id");
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public void ServiceBusReviewCodeNeverSendsReceivesSettlesOrMutates()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "backend/BirkNext.Api/Services/Integrations/ServiceBus"))) dir = dir.Parent;
        foreach (var file in Directory.GetFiles(Path.Combine(dir!.FullName, "backend/BirkNext.Api/Services/Integrations/ServiceBus"), "*.cs"))
        {
            var source = File.ReadAllText(file);
            source.Should().NotContainAny(
                ["ReceiveMessageAsync", "ReceiveMessagesAsync", "PeekMessage", "CompleteMessageAsync", "AbandonMessageAsync", "DeadLetterMessageAsync", "DeferMessageAsync", "RenewMessageLockAsync",
                 "SendMessageAsync", "SendMessagesAsync", "ScheduleMessageAsync", "ServiceBusSender", "ServiceBusReceiver", "ServiceBusProcessor", "ServiceBusAdministrationClient",
                 "CreateQueue", "CreateTopic", "CreateSubscription", "UpdateQueue", "UpdateTopic", "UpdateSubscription", "DeleteQueue", "DeleteTopic", "DeleteSubscription",
                 "HttpMethod.Put", "HttpMethod.Post", "HttpMethod.Patch", "HttpMethod.Delete", "PostAsync", "PutAsync", "DeleteAsync", "roleAssignments"],
                $"{Path.GetFileName(file)} is read-only metadata");
        }
    }

    // ── IQR contribution, history, seed ───────────────────────────────────────────────────────────────────────────

    private sealed class StaticSource(ServiceBusRuntimeEvidence runtime) : IServiceBusMetadataSource
    {
        public IntegrationEvidenceAdapterStatus Describe(IntegrationPlatform platform) => NotConfiguredEvidence.Status("sb", IntegrationEvidenceSource.AzureResourceManager, IntegrationEvidenceState.Available, "test");
        public Task<ServiceBusRuntimeEvidence> ReadAsync(IntegrationPlatform platform, CancellationToken ct) => Task.FromResult(runtime);
    }

    private sealed class NoEventHub : IEventHubMetadataSource, IEventHubConsumerGroupSource, ICheckpointEvidenceSource, ITelemetryEvidenceSource, IIntegrationNamespaceProbe
    {
        public IntegrationEvidenceAdapterStatus Describe(IntegrationPlatform platform) => NotConfiguredEvidence.Status("off", IntegrationEvidenceSource.AzureMetadata, IntegrationEvidenceState.NotConfigured, "off");
        public Task<EvidenceResult<EventHubRuntimeMetadata>> GetHubAsync(IntegrationPlatform p, string h, CancellationToken ct) => Task.FromResult(EvidenceResult<EventHubRuntimeMetadata>.Missing(IntegrationEvidenceState.NotConfigured, IntegrationEvidenceSource.AzureMetadata, "off"));
        public Task<EvidenceResult<ConsumerGroupList>> ListAsync(IntegrationPlatform p, string h, CancellationToken ct) => Task.FromResult(EvidenceResult<ConsumerGroupList>.Missing(IntegrationEvidenceState.NotConfigured, IntegrationEvidenceSource.AzureResourceManager, "off"));
        public Task<EvidenceResult<CheckpointEvidence>> GetAsync(IntegrationPlatform p, string h, string g, CancellationToken ct) => Task.FromResult(EvidenceResult<CheckpointEvidence>.Missing(IntegrationEvidenceState.NotConfigured, IntegrationEvidenceSource.CheckpointStore, "off"));
        public Task<EvidenceResult<ConsumerTelemetry>> GetConsumerAsync(IntegrationPlatform p, string r, int w, CancellationToken ct) => Task.FromResult(EvidenceResult<ConsumerTelemetry>.Missing(IntegrationEvidenceState.NotConfigured, IntegrationEvidenceSource.ApplicationInsights, "off"));
        public Task<NamespaceProbeResult> ProbeAsync(string fqdn, CancellationToken ct) => Task.FromResult(new NamespaceProbeResult(true, true, true, "ok (test)", 1, DateTimeOffset.UtcNow, "Tls13"));
    }

    private static IntegrationReviewEngine Engine(ServiceBusRuntimeEvidence runtime)
    {
        var none = new NoEventHub();
        return new IntegrationReviewEngine(none, none, none, none, none, new HttpClient(), NullLogger<IntegrationReviewEngine>.Instance,
            serviceBus: new ServiceBusEvidenceService(new StaticSource(runtime), NullLogger<ServiceBusEvidenceService>.Instance));
    }

    private static IntegrationCatalog Catalog() => new()
    {
        EnvironmentId = "dev", Platforms = [M2lbDevIntegrationSeed.Platform("dev", DateTimeOffset.UtcNow), Platform()],
        Integrations = M2lbDevIntegrationSeed.Integrations("dev", DateTimeOffset.UtcNow).ToList(),
    };

    [Fact]
    public async Task ServiceBusContributesToExistingDomainsWithoutOverCrediting()
    {
        var runtime = Runtime(e => e.Path == "person.barn/tjeneste-barnregistrert" ? null : Observed(e, dead: e.Name == "leselogg" ? 2 : 0));
        var engine = Engine(runtime);
        var readiness = engine.Readiness(Catalog(), IntegrationContractSet.Empty, null);
        readiness.ServiceBus.Should().ContainSingle().Which.Should().Match<ServiceBusReadiness>(s => s.Queues == 4 && s.Topics == 11 && s.Subscriptions == 8);
        readiness.EvidenceAdapters.Should().NotContain(a => a.Adapter.Contains("Service Bus", StringComparison.Ordinal), "Service Bus is not an Event Hub runtime source");
        readiness.Domains.Should().BeEquivalentTo(engine.Readiness(Catalog(), IntegrationContractSet.Empty).Domains);

        var messaging = Messaging(("M2LB.Revisjon.Worker", Route(MessagingRouteDirection.Listen, MessagingEndpointKind.Queue, "leselogg", message: "LeseloggHendelse")));
        var result = await engine.RunAsync(Catalog(), new IntegrationReviewRunRequest { EnvironmentId = "dev" }, IntegrationContractSet.Empty, [], messaging, CancellationToken.None);
        var system = result.Systems.Single(s => s.Kind == IntegrationKind.ServiceBus);
        system.Topics.Should().BeEmpty("Service Bus entities are not Event Hub business topics");
        var checks = system.PlatformChecks;
        checks.Single(c => c.CheckId == "sb-topology").Domain.Should().Be(IntegrationReviewDomain.Configuration);
        checks.Single(c => c.CheckId == "sb-routes").Status.Should().Be(IntegrationCheckStatus.Pass);
        checks.Single(c => c.CheckId == "sb-namespace").Domain.Should().Be(IntegrationReviewDomain.Connectivity);
        checks.Single(c => c.CheckId == "sb-entities").Status.Should().Be(IntegrationCheckStatus.Fail);
        checks.Single(c => c.CheckId == "sb-dead-letter").Status.Should().Be(IntegrationCheckStatus.Observed);
        checks.Single(c => c.CheckId == "sb-monitoring").Status.Should().Be(IntegrationCheckStatus.NotAssessed);
        checks.Single(c => c.CheckId == "sb-performance").Status.Should().Be(IntegrationCheckStatus.NotAssessed);
        checks.Should().NotContain(c => c.Domain == IntegrationReviewDomain.DataQuality);
        checks.Where(c => c.CheckId.StartsWith("sb-property-", StringComparison.Ordinal)).Should().NotBeEmpty();
        result.Findings.Should().Contain(f => f.RuleId == "sb-entity-not-found" && f.Subject == "person.barn/tjeneste-barnregistrert");
        result.Findings.Should().NotContain(f => f.Title.Contains("dead-letter", StringComparison.OrdinalIgnoreCase), "no dead-letter threshold is configured");
        result.ServiceBusSnapshot.Should().ContainSingle().Which.Runtime!.Entities.Single(e => e.Name == "leselogg").DeadLetterMessageCount.Should().Be(2);
        result.EvidenceAdapters.Should().Contain(a => a.Adapter.StartsWith(ArmServiceBusMetadataSource.Adapter, StringComparison.Ordinal) && a.State == IntegrationEvidenceState.Available);
    }

    [Fact]
    public async Task SeedV2UpgradeAddsOnlyTheServiceBusPlatform()
    {
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var catalog = new IntegrationCatalogService(db, NullLogger<IntegrationCatalogService>.Instance);
        const string url = "https://m2lbdev.bufetat.no/";
        var first = await catalog.GetAsync("dev", "Development", url);
        first.Platforms.Select(p => p.Id).Should().BeEquivalentTo([M2lbDevIntegrationSeed.PlatformId, M2lbDevIntegrationSeed.ServiceBusPlatformId]);
        // Simulate an environment seeded by v1 in which a person deleted an integration and the Service Bus platform does not exist yet.
        var deleted = first.Integrations.First().Id;
        await catalog.DeleteAsync("dev", deleted);
        db.IntegrationPlatforms.Remove(db.IntegrationPlatforms.Single(p => p.Id == M2lbDevIntegrationSeed.ServiceBusPlatformId));
        db.IntegrationEnvironmentStates.Single().SeedVersion = 1;
        await db.SaveChangesAsync();
        var upgraded = await catalog.GetAsync("dev", "Development", url);
        upgraded.Platforms.Should().Contain(p => p.Id == M2lbDevIntegrationSeed.ServiceBusPlatformId && p.ServiceBusTopology!.Entities.Count == 23);
        upgraded.Integrations.Should().NotContain(i => i.Id == deleted, "a v1 → v2 upgrade never brings back what a person deleted");
    }
}
