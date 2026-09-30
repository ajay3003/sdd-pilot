using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Azure.Core;
using BirkNext.Api.Data;
using BirkNext.Api.Services.Integrations;
using BirkNext.Api.Services.Integrations.EventHub;
using BirkNext.Integrations;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BirkNext.Api.Tests.Services.Integrations;

/// <summary>
/// Event Hub runtime evidence as part of Integration Quality Review: configured vs observed per domain, Azure runtime off is not a
/// misconfiguration, an observed <c>$Default</c> never confirms a mapping, checkpoint configuration stays apart from checkpoint runtime
/// evidence, no threshold means Observed only, partial evidence never fails the review, old runs are immutable, and no secret survives.
/// </summary>
public sealed class EventHubRuntimeEvidenceIqrTests
{
    private const string DevId = "dev-profile";
    private const string DevUrl = "https://m2lbdev.bufetat.no/";
    private const string Sentinel = "SENTINEL-7f3a9c";
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // ── Fakes ───────────────────────────────────────────────────────────────────────────────────────────────────────

    private static IntegrationEvidenceAdapterStatus Status(string adapter, IntegrationEvidenceSource source, bool on) =>
        NotConfiguredEvidence.Status(adapter, source, on ? IntegrationEvidenceState.Available : IntegrationEvidenceState.NotConfigured, on ? "Configured (test)." : $"{adapter} is not configured (test).");

    private sealed class Probe : IIntegrationNamespaceProbe
    {
        public int Calls { get; private set; }
        public Task<NamespaceProbeResult> ProbeAsync(string fqdn, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new NamespaceProbeResult(true, true, true, "Resolved, TCP 443 connected and TLS established (Tls13).", 9, DateTimeOffset.UtcNow, "Tls13"));
        }
    }

    private sealed class Metadata(Func<string, EvidenceResult<EventHubRuntimeMetadata>>? respond) : IEventHubMetadataSource
    {
        public int Calls { get; private set; }
        public IntegrationEvidenceAdapterStatus Describe(IntegrationPlatform platform) => Status("Event Hub metadata", IntegrationEvidenceSource.AzureMetadata, respond is not null);
        public Task<EvidenceResult<EventHubRuntimeMetadata>> GetHubAsync(IntegrationPlatform platform, string hubName, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(respond?.Invoke(hubName) ?? EvidenceResult<EventHubRuntimeMetadata>.Missing(IntegrationEvidenceState.NotConfigured, IntegrationEvidenceSource.AzureMetadata, "Not configured (test)."));
        }
    }

    private sealed class Groups(Func<string, EvidenceResult<ConsumerGroupList>>? respond) : IEventHubConsumerGroupSource
    {
        public int Calls { get; private set; }
        public IntegrationEvidenceAdapterStatus Describe(IntegrationPlatform platform) => Status("Consumer groups", IntegrationEvidenceSource.AzureResourceManager, respond is not null);
        public Task<EvidenceResult<ConsumerGroupList>> ListAsync(IntegrationPlatform platform, string hubName, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(respond?.Invoke(hubName) ?? EvidenceResult<ConsumerGroupList>.Missing(IntegrationEvidenceState.NotConfigured, IntegrationEvidenceSource.AzureResourceManager, "Not configured (test)."));
        }
    }

    private sealed class Checkpoints(Func<string, string, EvidenceResult<CheckpointEvidence>>? respond) : ICheckpointEvidenceSource
    {
        public int Calls { get; private set; }
        public IntegrationEvidenceAdapterStatus Describe(IntegrationPlatform platform) => Status("Consumer checkpoints", IntegrationEvidenceSource.CheckpointStore, respond is not null);
        public Task<EvidenceResult<CheckpointEvidence>> GetAsync(IntegrationPlatform platform, string hubName, string consumerGroup, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(respond?.Invoke(hubName, consumerGroup) ?? EvidenceResult<CheckpointEvidence>.Missing(IntegrationEvidenceState.NotConfigured, IntegrationEvidenceSource.CheckpointStore, "Not configured (test)."));
        }
    }

    private sealed class Telemetry(Func<string, EvidenceResult<ConsumerTelemetry>>? respond) : ITelemetryEvidenceSource
    {
        public int Calls { get; private set; }
        public IntegrationEvidenceAdapterStatus Describe(IntegrationPlatform platform) => Status("Consumer telemetry", IntegrationEvidenceSource.ApplicationInsights, respond is not null);
        public Task<EvidenceResult<ConsumerTelemetry>> GetConsumerAsync(IntegrationPlatform platform, string roleName, int windowHours, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(respond?.Invoke(roleName) ?? EvidenceResult<ConsumerTelemetry>.Missing(IntegrationEvidenceState.NotConfigured, IntegrationEvidenceSource.ApplicationInsights, "Not configured (test)."));
        }
    }

    private sealed class Namespaces(Func<IntegrationPlatform, EventHubNamespaceObservation>? respond) : IEventHubNamespaceSource
    {
        public int Calls { get; private set; }
        public IntegrationEvidenceAdapterStatus Describe(IntegrationPlatform platform) => Status(ArmEventHubNamespaceSource.Adapter, IntegrationEvidenceSource.AzureResourceManager, respond is not null);
        public Task<EventHubNamespaceObservation> ReadAsync(IntegrationPlatform platform, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(respond?.Invoke(platform) ?? new EventHubNamespaceObservation { State = IntegrationEvidenceState.NotConfigured, Reason = "Not configured (test).", CapturedAt = DateTimeOffset.UtcNow });
        }
    }

    private sealed class Metrics(Func<EventHubMetricsEvidence>? respond) : IEventHubMetricsSource
    {
        public int Calls { get; private set; }
        public IntegrationEvidenceAdapterStatus Describe(IntegrationPlatform platform) => Status(AzureMonitorEventHubMetricsSource.Adapter, IntegrationEvidenceSource.AzureMonitor, respond is not null);
        public Task<EventHubMetricsEvidence> ReadAsync(IntegrationPlatform platform, int windowHours, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(respond?.Invoke() ?? new EventHubMetricsEvidence { State = IntegrationEvidenceState.NotConfigured, Reason = "Not configured (test).", CapturedAt = DateTimeOffset.UtcNow, WindowHours = windowHours });
        }
    }

    private sealed record Fakes(Probe Probe, Metadata Metadata, Groups Groups, Checkpoints Checkpoints, Telemetry Telemetry, Namespaces Namespaces, Metrics Metrics)
    {
        public int AzureCalls => Metadata.Calls + Groups.Calls + Checkpoints.Calls + Telemetry.Calls + Namespaces.Calls + Metrics.Calls;
        public IntegrationReviewEngine Engine() =>
            new(Probe, Metadata, Groups, Checkpoints, Telemetry, new HttpClient(), NullLogger<IntegrationReviewEngine>.Instance, namespaces: Namespaces, eventHubMetrics: Metrics);
    }

    private static Fakes Evidence(
        Func<string, EvidenceResult<EventHubRuntimeMetadata>>? metadata = null, Func<string, EvidenceResult<ConsumerGroupList>>? groups = null,
        Func<string, string, EvidenceResult<CheckpointEvidence>>? checkpoints = null, Func<string, EvidenceResult<ConsumerTelemetry>>? telemetry = null,
        Func<IntegrationPlatform, EventHubNamespaceObservation>? namespaces = null, Func<EventHubMetricsEvidence>? metrics = null) =>
        new(new Probe(), new Metadata(metadata), new Groups(groups), new Checkpoints(checkpoints), new Telemetry(telemetry), new Namespaces(namespaces), new Metrics(metrics));

    private static AppDbContext Db() => new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    /// <summary>The seeded DEV catalog with its verified runtime defaults ($Default assumption, checkpoint store, Application Insights, Azure Monitor logs).</summary>
    private static Task<IntegrationCatalog> Catalog(AppDbContext? db = null) =>
        new IntegrationCatalogService(db ?? Db(), NullLogger<IntegrationCatalogService>.Instance).GetAsync(DevId, "Development", DevUrl);

    private static IntegrationReviewRunRequest Request => new() { EnvironmentId = DevId, EnvironmentName = "M2LB DEV" };

    private static Task<IntegrationReviewResult> Run(Fakes fakes, IntegrationCatalog catalog) =>
        fakes.Engine().RunAsync(catalog, Request, IntegrationContractSet.Empty, [], CancellationToken.None);

    private static EventHubNamespaceObservation Observed(IntegrationCatalog catalog, Func<string, bool>? keep = null, params string[] extra)
    {
        var platform = catalog.Platforms.Single(p => p.Id == M2lbDevIntegrationSeed.PlatformId);
        var names = catalog.Integrations.Select(i => i.EndpointOrTopic!).Where(keep ?? (_ => true)).Concat(platform.TechnicalTopics.Select(t => t.Name)).Concat(extra);
        return new EventHubNamespaceObservation
        {
            State = IntegrationEvidenceState.Available, Reason = "Namespace metadata read (test).", CapturedAt = DateTimeOffset.UtcNow, Status = "Active", Sku = "Premium", Location = "norwayeast",
            PublicNetworkAccess = "Enabled", DisableLocalAuth = false, MinimumTlsVersion = "1.2", PrivateEndpointConnections = 0,
            HubListState = IntegrationEvidenceState.Available, HubListReason = "listed (test)",
            Hubs = names.Select(n => new EventHubObservedHub { Name = n, Status = "Active", PartitionCount = 1, RetentionHours = 168 }).ToList(),
        };
    }

    private static EvidenceResult<EventHubRuntimeMetadata> Hub() => EvidenceResult<EventHubRuntimeMetadata>.Available(IntegrationEvidenceSource.AzureMetadata,
        new EventHubRuntimeMetadata(true, [new PartitionRuntime("0", 500, Now.AddMinutes(-3), false)]));

    private static EvidenceResult<ConsumerGroupList> DefaultGroup(string _) => EvidenceResult<ConsumerGroupList>.Available(IntegrationEvidenceSource.AzureResourceManager, new ConsumerGroupList(["$Default"]));

    private static EvidenceResult<CheckpointEvidence> Checkpoint(string _, string group) => EvidenceResult<CheckpointEvidence>.Available(IntegrationEvidenceSource.CheckpointStore,
        new CheckpointEvidence(group, [new PartitionCheckpoint("0", 380, 1000, Now.AddMinutes(-17))], 1));

    private static EventHubMetricsEvidence MetricValues() => new()
    {
        State = IntegrationEvidenceState.Available, Reason = "Azure Monitor (test).", CapturedAt = DateTimeOffset.UtcNow, WindowHours = 24,
        Metrics = [new("IncomingMessages", "Total", 86400, "Count"), new("OutgoingMessages", "Total", 86000, "Count"), new("IncomingRequests", "Total", 90000, "Count"),
            new("ServerErrors", "Total", 0, "Count"), new("UserErrors", "Total", 3, "Count"), new("ThrottledRequests", "Total", 0, "Count")],
    };

    private static EvidenceResult<ConsumerTelemetry> Tel(string _) => EvidenceResult<ConsumerTelemetry>.Available(IntegrationEvidenceSource.ApplicationInsights,
        new ConsumerTelemetry(0, 0, 0, 0, 0, 40, 40, 10, 0, 18.5, Now.AddMinutes(-2), null, 24));

    private static IntegrationCheck TopicCheck(IntegrationReviewResult result, string table, string checkId) =>
        result.Systems.SelectMany(s => s.Topics).Single(t => t.IntegrationId.EndsWith($"dbo.{table}")).Checks.Single(c => c.CheckId == checkId);

    private static IntegrationCheck PlatformCheck(IntegrationReviewResult result, string checkId) => result.Systems.SelectMany(s => s.PlatformChecks).Single(c => c.CheckId == checkId);

    private static IntegrationDomainResult Domain(IntegrationReviewResult result, IntegrationReviewDomain domain) => result.Domains.Single(d => d.Domain == domain);

    private static IIntegrationAzureCredential Azure(bool enabled) =>
        new IntegrationAzureCredential(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["IntegrationReview:Azure:Enabled"] = enabled.ToString() }).Build());

    /// <summary>Counts every HTTP request; answers with the configured response or throws.</summary>
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    private sealed class StaticCredential : IIntegrationAzureCredential
    {
        public TokenCredential? Credential { get; } = new Token();
        public string DisabledReason => "";
        private sealed class Token : TokenCredential
        {
            public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) => new($"token-{Sentinel}", DateTimeOffset.UtcNow.AddHours(1));
            public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) => new(GetToken(requestContext, cancellationToken));
        }
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Lines { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception) + (exception is null ? "" : " " + exception));
    }

    // ── §49 Azure disabled ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AzureDisabled_SourcesConfigured_IsNotAMisconfiguration_AndMakesNoAzureCall()
    {
        var azure = Azure(false);
        var handler = new Handler(_ => throw new InvalidOperationException("No Azure call may be made while Azure runtime is disabled."));
        var http = new HttpClient(handler);
        var probe = new Probe();
        var engine = new IntegrationReviewEngine(probe, new AzureEventHubMetadataSource(azure, NullLogger<AzureEventHubMetadataSource>.Instance),
            new ArmConsumerGroupSource(azure, http, NullLogger<ArmConsumerGroupSource>.Instance), new BlobCheckpointEvidenceSource(azure, NullLogger<BlobCheckpointEvidenceSource>.Instance),
            new LogAnalyticsTelemetrySource(azure, NullLogger<LogAnalyticsTelemetrySource>.Instance), http, NullLogger<IntegrationReviewEngine>.Instance, azure: azure,
            namespaces: new ArmEventHubNamespaceSource(azure, http, NullLogger<ArmEventHubNamespaceSource>.Instance),
            eventHubMetrics: new AzureMonitorEventHubMetricsSource(azure, NullLogger<AzureMonitorEventHubMetricsSource>.Instance));
        var catalog = await Catalog();

        var readiness = engine.Readiness(catalog, IntegrationContractSet.Empty);
        readiness.AzureRuntimeEnabled.Should().BeFalse();
        readiness.AzureRuntimeReason.Should().Contain("IntegrationReview:Azure:Enabled is not true");
        readiness.RuntimeSourcesConfigured.Should().Be(4, "metadata, Resource Manager, checkpoints and telemetry are configured by the seed");
        readiness.Reasons.Should().Contain(r => r.Contains("Runtime evidence sources: configured") && r.Contains("Azure runtime: not configured") && r.Contains("not an integration misconfiguration"));
        var domains = readiness.Domains.ToDictionary(d => d.Domain);
        domains[IntegrationReviewDomain.Configuration].Readiness.Should().Be(IntegrationDomainReadiness.Ready);
        domains[IntegrationReviewDomain.Connectivity].Readiness.Should().Be(IntegrationDomainReadiness.Limited);
        domains[IntegrationReviewDomain.MessageFlow].Readiness.Should().Be(IntegrationDomainReadiness.NotAssessable);
        domains[IntegrationReviewDomain.MessageFlow].Explanation.Should().Contain("not read because Azure runtime is not enabled");
        domains[IntegrationReviewDomain.MessageFlow].Missing.Should().Contain(m => m.Contains("configured, not read — Azure runtime is not enabled"));

        var result = await engine.RunAsync(catalog, Request, IntegrationContractSet.Empty, [], CancellationToken.None);
        handler.Requests.Should().BeEmpty("no Azure Resource Manager / Azure Monitor / Event Hub call is made while Azure runtime is disabled");
        probe.Calls.Should().Be(1, "the network probe is not an Azure call and still runs");
        result.AllChecks.Should().NotContain(c => c.Status == IntegrationCheckStatus.Fail, "runtime evidence being unavailable is never a failure");
        result.AllChecks.Where(c => c.Provenance is IntegrationEvidenceSource.AzureResourceManager or IntegrationEvidenceSource.AzureMonitor or IntegrationEvidenceSource.AzureMetadata
            or IntegrationEvidenceSource.CheckpointStore or IntegrationEvidenceSource.ApplicationInsights).Should().OnlyContain(c => c.Status != IntegrationCheckStatus.Warning && c.Status != IntegrationCheckStatus.NotConfigured || c.CheckId == "obs-telemetry-access" || c.CheckId == "conn-metadata-access",
            "an Azure source that was not read is Not assessed, not a configuration gap");
        result.Findings.Select(f => f.RuleId).Should().NotContain(["hub-missing", "namespace-not-found", "consumer-group-missing"], "unavailable evidence is never a finding");
        PlatformCheck(result, "conn-arm-access").Status.Should().Be(IntegrationCheckStatus.NotAssessed);
        PlatformCheck(result, "conn-arm-access").Explanation.Should().Contain("IntegrationReview:Azure:Enabled");
        result.EventHubSnapshot.Should().ContainSingle().Which.AzureRuntimeEnabled.Should().BeFalse();
        result.WhatWasTested.Should().ContainSingle(t => t.StartsWith("Namespace DNS/TCP/TLS"), "only the probe was executed");
        result.WhatWasNotAssessed.Should().Contain(n => n.Contains(ArmEventHubNamespaceSource.Adapter) && n.Contains("IntegrationReview:Azure:Enabled"));
    }

    // ── §50–52 Configured vs observed hubs ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task EventHubsObserved_AreObservedMatch_NeverAnAutomaticPass()
    {
        var catalog = await Catalog();
        var fakes = Evidence(namespaces: _ => Observed(catalog, extra: "m2lb-cdc-dev.birkm2lb.dbo.unconfigured"));
        var result = await Run(fakes, catalog);

        PlatformCheck(result, "cfg-namespace-observed").Status.Should().Be(IntegrationCheckStatus.Observed);
        PlatformCheck(result, "cfg-namespace-observed").Evidence.Should().Contain("Observed match: evhns-m2lb-dev-nwe-001");
        var person = TopicCheck(result, "Person", "cfg-hub-observed");
        person.Status.Should().Be(IntegrationCheckStatus.Observed);
        person.Evidence.Should().Contain("Observed match").And.Contain("1 partition").And.Contain("168 h retention");
        person.Provenance.Should().Be(IntegrationEvidenceSource.AzureResourceManager);
        var topology = PlatformCheck(result, "cfg-topology");
        topology.Status.Should().Be(IntegrationCheckStatus.Observed);
        topology.Evidence.Should().Contain("16 observed match").And.Contain("1 additional observed").And.Contain("5 technical/support hub(s)");
        result.AllChecks.Where(c => c.CheckId is "cfg-hub-observed" or "cfg-topology" or "cfg-namespace-observed" or "conn-arm-access").Should().NotContain(c => c.Status == IntegrationCheckStatus.Pass);

        var snapshot = result.EventHubSnapshot.Single();
        snapshot.Hubs.Where(h => h.Technical).Select(h => h.Hub).Should().Contain(["connect-configs", "connect-offsets", "connect-status", "schemahistory", "m2lb-cdc-dev"]);
        snapshot.Hubs.Where(h => h.Technical).Should().OnlyContain(h => h.State == EventHubComparisonState.TechnicalObserved);
        snapshot.Hubs.Single(h => h.Hub.EndsWith("unconfigured")).State.Should().Be(EventHubComparisonState.AdditionalObserved);
        result.Findings.Should().BeEmpty("additional and technical hubs are not errors, and observed matches are not findings");
        PlatformCheck(result, "conn-arm-access").Status.Should().Be(IntegrationCheckStatus.Observed);
        Domain(result, IntegrationReviewDomain.Configuration).Observed.Should().Contain(o => o.StartsWith("Configured Event Hub in Azure: Observed"));
        result.WhatWasTested.Should().Contain(t => t.StartsWith("Configured vs observed Event Hub topology — 16 configured"));
    }

    [Fact]
    public async Task ConfiguredHubMissingInAzure_IsAPotentialFinding()
    {
        var catalog = await Catalog();
        var result = await Run(Evidence(namespaces: _ => Observed(catalog, keep: h => !h.EndsWith("dbo.Person"))), catalog);
        var check = TopicCheck(result, "Person", "cfg-hub-observed");
        check.Status.Should().Be(IntegrationCheckStatus.Warning);
        check.Evidence.Should().Be("Configured but not found in Azure.");
        result.Findings.Should().ContainSingle(f => f.RuleId == "hub-missing").Which.AffectedIntegrations.Should().ContainSingle().Which.Should().Be("BIRK Person CDC");
        PlatformCheck(result, "cfg-topology").Status.Should().Be(IntegrationCheckStatus.Warning);
        TopicCheck(result, "Barn", "cfg-hub-observed").Status.Should().Be(IntegrationCheckStatus.Observed);
    }

    [Fact]
    public async Task RetentionDifference_IsDifferenceObserved_NotAFailure()
    {
        var catalog = await Catalog();
        var result = await Run(Evidence(namespaces: _ => Observed(catalog) with
        {
            Hubs = Observed(catalog).Hubs.Select(h => h.Name.EndsWith("dbo.Barn") ? h with { RetentionHours = 24 } : h).ToList(),
        }), catalog);
        var check = TopicCheck(result, "Barn", "cfg-hub-observed");
        check.Status.Should().Be(IntegrationCheckStatus.Warning);
        check.Evidence.Should().Contain("Difference observed").And.Contain("retention configured 168 h, observed 24 h");
        result.AllChecks.Should().NotContain(c => c.Status == IntegrationCheckStatus.Fail);
        result.Findings.Should().ContainSingle(f => f.RuleId == "retention-drift").Which.Severity.Should().Be(IntegrationFindingSeverityV2.Low);
    }

    [Fact]
    public async Task HubListNotAuthorized_IsNotAuthorized_NeverMissing()
    {
        var catalog = await Catalog();
        var result = await Run(Evidence(namespaces: _ => Observed(catalog) with { HubListState = IntegrationEvidenceState.NotAuthorized, HubListReason = "Hub list: not authorized (HTTP 403).", Hubs = [] }), catalog);
        TopicCheck(result, "Person", "cfg-hub-observed").Status.Should().Be(IntegrationCheckStatus.NotAssessed);
        TopicCheck(result, "Person", "cfg-hub-observed").Explanation.Should().Contain("Not authorized");
        result.EventHubSnapshot.Single().Hubs.Should().OnlyContain(h => h.State == EventHubComparisonState.NotAuthorized);
        result.Findings.Should().NotContain(f => f.RuleId == "hub-missing");
        PlatformCheck(result, "cfg-namespace-observed").Status.Should().Be(IntegrationCheckStatus.Observed, "the namespace read itself succeeded — failure isolation");
    }

    // ── §51 $Default ────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DefaultObserved_IsObservedMatch_AndTheMappingStillNeedsConfirmation()
    {
        var catalog = await Catalog();
        var result = await Run(Evidence(groups: DefaultGroup), catalog);
        var check = TopicCheck(result, "Person", "flow-consumer-group");
        check.Status.Should().Be(IntegrationCheckStatus.Observed);
        check.Evidence.Should().Contain("Expected: $Default (Configured assumption)").And.Contain("Observed: $Default").And.Contain("Result: Observed match").And.Contain("Application mapping: Needs confirmation");
        check.Explanation.Should().Contain("does not show which application reads with it");
        var person = result.EventHubSnapshot.Single().ConsumerGroups.Single(g => g.IntegrationId.EndsWith("dbo.Person"));
        person.State.Should().Be(EventHubComparisonState.ObservedMatch);
        person.ExpectedProvenance.Should().Be("Configured assumption");
        person.Mapping.Should().Be("Needs confirmation", "even the confirmed Person consumer: the group is an assumption, and observing it never confirms the mapping");
        TopicCheck(result, "Person", "cfg-consumer-group").Status.Should().Be(IntegrationCheckStatus.NeedsConfirmation);
        result.WhatWasNotAssessed.Should().Contain(n => n.StartsWith("Consumer application mapping") && n.Contains("$Default is a configured assumption"));
        result.Findings.Should().NotContain(f => f.RuleId == "consumer-group-missing");
    }

    [Fact]
    public async Task AssumedGroupNotObserved_NeedsConfirmation_NotAFinding()
    {
        var catalog = await Catalog();
        var result = await Run(Evidence(groups: _ => EvidenceResult<ConsumerGroupList>.Available(IntegrationEvidenceSource.AzureResourceManager, new ConsumerGroupList(["person-adapter"]))), catalog);
        TopicCheck(result, "Person", "flow-consumer-group").Status.Should().Be(IntegrationCheckStatus.NeedsConfirmation);
        result.Findings.Should().NotContain(f => f.RuleId == "consumer-group-missing", "an assumption that does not hold is a confirmation task, not a defect");
    }

    // ── §53–54 Checkpoints ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CheckpointObserved_ConfigurationAndRuntimeStaySeparate_AndReliabilityIsStillPartial()
    {
        var catalog = await Catalog();
        var result = await Run(Evidence(metadata: _ => Hub(), groups: DefaultGroup, checkpoints: Checkpoint), catalog);
        var config = PlatformCheck(result, "rel-checkpoint-config");
        config.Status.Should().Be(IntegrationCheckStatus.Configured);
        config.Evidence.Should().Contain("https://stm2bbirkdevnwe001.blob.core.windows.net/person-adapter").And.Contain("Verified");
        var runtime = TopicCheck(result, "Person", "rel-checkpoint");
        runtime.Status.Should().Be(IntegrationCheckStatus.Observed, "checkpoints for an assumed group are observed without a verdict");
        runtime.Provenance.Should().Be(IntegrationEvidenceSource.CheckpointStore);
        TopicCheck(result, "Person", "rel-checkpoint-freshness").Status.Should().Be(IntegrationCheckStatus.Observed);
        TopicCheck(result, "Person", "rel-checkpoint-freshness").Evidence.Should().Contain("17 min ago");
        Domain(result, IntegrationReviewDomain.Reliability).StateLabel.Should().Be("Partially assessed");
        result.AllChecks.Where(c => c.Domain == IntegrationReviewDomain.Reliability).Should().NotContain(c => c.Status == IntegrationCheckStatus.Pass);
        var lag = TopicCheck(result, "Person", "perf-lag");
        lag.Status.Should().Be(IntegrationCheckStatus.Observed);
        lag.Evidence.Should().StartWith("120 event(s) behind");
        var summary = result.EventHubSnapshot.Single().Checkpoints.Single(c => c.IntegrationId.EndsWith("dbo.Person"));
        (summary.Configuration, summary.Runtime).Should().Be(("Verified", IntegrationEvidenceState.Available));
        TopicCheck(result, "Person", "flow-end-to-end").Status.Should().Be(IntegrationCheckStatus.NotAssessed);
        TopicCheck(result, "Person", "flow-end-to-end").Evidence.Should().Contain("Producer Configured → Event Hub Observed → Consumer group $Default Observed match (configured assumption) → Checkpoint Observed → Application handler Not observed");
        TopicCheck(result, "Person", "flow-end-to-end").Explanation.Should().Contain("Transport progression evidence exists, but application processing has not been observed");
        result.WhatWasTested.Should().Contain(t => t.StartsWith("Checkpoint metadata — 16 hub(s)"));
    }

    [Fact]
    public async Task CheckpointNotAuthorized_IsNotAuthorized_NeverAMissingCheckpoint_AndOtherEvidenceIsKept()
    {
        var catalog = await Catalog();
        var result = await Run(Evidence(metadata: _ => Hub(), groups: DefaultGroup, namespaces: _ => Observed(catalog),
            checkpoints: (_, _) => EvidenceResult<CheckpointEvidence>.Missing(IntegrationEvidenceState.NotAuthorized, IntegrationEvidenceSource.CheckpointStore, "Checkpoint store could not be read (RequestFailedException (HTTP 403, AuthorizationPermissionMismatch)).")), catalog);
        var check = TopicCheck(result, "Person", "rel-checkpoint");
        check.Status.Should().Be(IntegrationCheckStatus.NotAssessed);
        check.Explanation.Should().Contain("Not authorized");
        result.AllChecks.Where(c => c.CheckId == "rel-checkpoint").Should().NotContain(c => c.Status == IntegrationCheckStatus.NoRecentEvidence, "an unreadable store never becomes 'no checkpoint recorded'");
        PlatformCheck(result, "rel-checkpoint-config").Status.Should().Be(IntegrationCheckStatus.Configured, "configuration stays verified when runtime evidence is unauthorized");
        TopicCheck(result, "Person", "cfg-hub-observed").Status.Should().Be(IntegrationCheckStatus.Observed, "hub evidence is retained");
        TopicCheck(result, "Person", "flow-producer").Status.Should().Be(IntegrationCheckStatus.Observed);
        Domain(result, IntegrationReviewDomain.MessageFlow).StateLabel.Should().Be("Partially assessed");
        Domain(result, IntegrationReviewDomain.Reliability).StateLabel.Should().NotBe("Assessed");
        result.Outcome.Should().NotBe(IntegrationReviewOutcome.NothingAssessed);
        result.Findings.Should().BeEmpty("unauthorized evidence is a limitation, never a finding");
        result.EventHubSnapshot.Single().Checkpoints.Should().OnlyContain(c => c.Runtime == IntegrationEvidenceState.NotAuthorized && c.Configuration == "Verified");
        result.WhatWasNotAssessed.Should().Contain(n => n.Contains("Consumer checkpoints") && n.Contains("Not authorized"));
    }

    // ── §55–57 Observability ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ApplicationInsightsConfiguredOnly_IsLimited_NeverPass()
    {
        var catalog = await Catalog();
        var fakes = Evidence(metadata: _ => Hub());
        fakes.Engine().Readiness(catalog, IntegrationContractSet.Empty).Domains.Single(d => d.Domain == IntegrationReviewDomain.Observability).Readiness.Should().Be(IntegrationDomainReadiness.Limited);
        var result = await Run(fakes, catalog);
        PlatformCheck(result, "obs-appinsights").Status.Should().Be(IntegrationCheckStatus.Configured);
        PlatformCheck(result, "obs-appinsights").Evidence.Should().Contain("appi-m2lb-dev-nwe-001");
        PlatformCheck(result, "obs-telemetry-access").Status.Should().NotBe(IntegrationCheckStatus.Pass);
        TopicCheck(result, "Person", "obs-traces").Status.Should().Be(IntegrationCheckStatus.NotAssessed);
        Domain(result, IntegrationReviewDomain.Observability).StateLabel.Should().NotBe("Assessed");
        result.AllChecks.Where(c => c.Domain == IntegrationReviewDomain.Observability && c.CheckId is "obs-appinsights" or "obs-log-destination")
            .Should().OnlyContain(c => c.Status == IntegrationCheckStatus.Configured, "configured monitoring is never Pass or runtime telemetry");
    }

    [Fact]
    public async Task TelemetryObserved_IsObserved_WithoutClaimingFullObservability()
    {
        var catalog = await Catalog();
        var fakes = Evidence(metadata: _ => Hub(), telemetry: Tel);
        fakes.Engine().Readiness(catalog, IntegrationContractSet.Empty).Domains.Single(d => d.Domain == IntegrationReviewDomain.Observability).Readiness.Should().Be(IntegrationDomainReadiness.Partial);
        var result = await Run(fakes, catalog);
        TopicCheck(result, "Person", "obs-traces").Status.Should().Be(IntegrationCheckStatus.Observed);
        TopicCheck(result, "Person", "obs-traces").Freshness.Should().Be(IntegrationEvidenceItemFreshness.Current);
        Domain(result, IntegrationReviewDomain.Observability).StateLabel.Should().Be("Partially assessed");
        result.WhatWasTested.Should().Contain("Application Insights telemetry — bounded aggregate queries");
    }

    [Fact]
    public async Task NoLogAnalyticsWorkspace_WithAzureMonitorLogs_IsNotRequired_AndBlocksNothing()
    {
        var catalog = await Catalog();
        catalog.Platforms.Single(p => p.Id == M2lbDevIntegrationSeed.PlatformId).RuntimeEvidence!.TelemetryWorkspaceId.Should().BeNull();
        var fakes = Evidence(metadata: _ => Hub(), telemetry: Tel);
        var readiness = fakes.Engine().Readiness(catalog, IntegrationContractSet.Empty);
        var observability = readiness.Domains.Single(d => d.Domain == IntegrationReviewDomain.Observability);
        observability.Explanation.Should().Contain("Dedicated Log Analytics workspace: Not configured — not required for this platform configuration").And.Contain("Azure Monitor");
        observability.Missing.Should().NotContain(m => m.Contains("workspace", StringComparison.OrdinalIgnoreCase));
        readiness.Reasons.Should().NotContain(r => r.Contains("workspace", StringComparison.OrdinalIgnoreCase));
        var result = await Run(fakes, catalog);
        var destination = PlatformCheck(result, "obs-log-destination");
        destination.Status.Should().Be(IntegrationCheckStatus.Configured);
        destination.Evidence.Should().Be("Azure Monitor · Dedicated Log Analytics workspace: Not configured — not required for this platform configuration");
        result.Limitations.Concat(result.WhatWasNotAssessed).Should().NotContain(l => l.Contains("workspace", StringComparison.OrdinalIgnoreCase));
        result.AllChecks.Should().NotContain(c => c.Evidence.Contains("workspace not configured", StringComparison.OrdinalIgnoreCase));
    }

    // ── §58 Performance without thresholds ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RuntimeMetricsWithoutThresholds_AreObservedOnly()
    {
        var catalog = await Catalog();
        var result = await Run(Evidence(metadata: _ => Hub(), groups: DefaultGroup, checkpoints: Checkpoint, metrics: MetricValues), catalog);
        var throughput = PlatformCheck(result, "perf-throughput");
        throughput.Status.Should().Be(IntegrationCheckStatus.Observed);
        throughput.Evidence.Should().Contain("Incoming 86,400 (3,600/h)").And.Contain("outgoing 86,000");
        throughput.Provenance.Should().Be(IntegrationEvidenceSource.AzureMonitor);
        result.AllChecks.Where(c => c.Domain == IntegrationReviewDomain.Performance)
            .Select(c => c.Status).Should().NotContain([IntegrationCheckStatus.Pass, IntegrationCheckStatus.Fail, IntegrationCheckStatus.Warning], "no threshold means Observed only");
        PlatformCheck(result, "err-transport-server").Status.Should().Be(IntegrationCheckStatus.NoIndicatorsObserved);
        PlatformCheck(result, "err-transport-user").Status.Should().Be(IntegrationCheckStatus.Observed);
        PlatformCheck(result, "err-transport-user").Explanation.Should().Contain("application error handling is assessed separately");
        result.Findings.Should().BeEmpty("observed transport errors without a threshold are not findings");
        result.ManualFollowUp.Should().Contain(m => m.Title == "Define IQR lag/checkpoint thresholds");
        result.EventHubSnapshot.Single().Metrics!.Total("IncomingMessages").Should().Be(86400);
    }

    // ── §59–60 Contracts and data quality never upgraded by runtime metadata ───────────────────────────────────────────

    [Fact]
    public async Task RuntimeMetadataNeverMakesContractsOrDataQualityAssessable()
    {
        var catalog = await Catalog();
        var fakes = Evidence(metadata: _ => Hub(), groups: DefaultGroup, checkpoints: Checkpoint, telemetry: Tel, namespaces: _ => Observed(catalog), metrics: MetricValues);
        var domains = fakes.Engine().Readiness(catalog, IntegrationContractSet.Empty).Domains.ToDictionary(d => d.Domain);
        domains[IntegrationReviewDomain.Contract].Readiness.Should().Be(IntegrationDomainReadiness.NotAssessable);
        domains[IntegrationReviewDomain.Contract].Explanation.Should().Contain("Event Hub runtime metadata does not make contracts assessable");
        domains[IntegrationReviewDomain.DataQuality].Readiness.Should().Be(IntegrationDomainReadiness.NotAssessable);
        var result = await Run(fakes, catalog);
        result.AllChecks.Where(c => c.CheckId == "contract-compatibility").Should().OnlyContain(c => c.Status == IntegrationCheckStatus.NotAssessed);
        result.AllChecks.Where(c => c.CheckId == "dq-runtime-structure").Should().OnlyContain(c => c.Status == IntegrationCheckStatus.NotAssessed);
        Domain(result, IntegrationReviewDomain.DataQuality).StateLabel.Should().Be("Not assessed");
        result.WhatWasNotAssessed.Should().Contain(n => n.StartsWith("Contract compatibility — no producer and consumer schemas for 16"));
        result.WhatWasNotAssessed.Should().Contain("Business payload correctness — events are never read or consumed.");
    }

    // ── §61 Partial evidence ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PartialEvidence_IsDomainByDomain_NeverAFailedReview()
    {
        var catalog = await Catalog();
        var fakes = Evidence(metadata: _ => Hub(), groups: DefaultGroup, namespaces: _ => Observed(catalog),
            checkpoints: (_, _) => EvidenceResult<CheckpointEvidence>.Missing(IntegrationEvidenceState.Unavailable, IntegrationEvidenceSource.CheckpointStore, "Timed out (test)."),
            telemetry: _ => EvidenceResult<ConsumerTelemetry>.Missing(IntegrationEvidenceState.NotAuthorized, IntegrationEvidenceSource.ApplicationInsights, "Telemetry access unauthorized (test)."));
        var readiness = fakes.Engine().Readiness(catalog, IntegrationContractSet.Empty);
        readiness.Domains.Single(d => d.Domain == IntegrationReviewDomain.MessageFlow).Readiness.Should().Be(IntegrationDomainReadiness.Partial);
        readiness.Domains.Single(d => d.Domain == IntegrationReviewDomain.Security).Readiness.Should().Be(IntegrationDomainReadiness.Limited);
        var result = await Run(fakes, catalog);
        result.Outcome.Should().Be(IntegrationReviewOutcome.CompletedWithLimitations);
        result.Findings.Should().BeEmpty();
        result.AllChecks.Should().NotContain(c => c.Status == IntegrationCheckStatus.Fail);
        Domain(result, IntegrationReviewDomain.MessageFlow).StateLabel.Should().Be("Partially assessed");
        Domain(result, IntegrationReviewDomain.MessageFlow).Missing.Should().Contain(m => m.StartsWith("End-to-end message flow"));
        Domain(result, IntegrationReviewDomain.Configuration).StateLabel.Should().NotBe("Not assessed");
        Domain(result, IntegrationReviewDomain.Security).StateLabel.Should().Be("Partially assessed", "consumer RBAC is never evaluated");
        result.WhatWasTested.Should().Contain(t => t.StartsWith("Consumer group existence — 16 hub(s)"));
        result.WhatWasNotAssessed.Should().Contain(n => n.Contains("Consumer telemetry") && n.Contains("Not authorized"));
    }

    // ── §62 History immutability ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task StoredReview_RendersItsSnapshot_WithoutReadingAzureAgain()
    {
        await using var db = Db();
        var catalogService = new IntegrationCatalogService(db, NullLogger<IntegrationCatalogService>.Instance);
        var catalog = await catalogService.GetAsync(DevId, "Development", DevUrl);
        var fakes = Evidence(metadata: _ => Hub(), groups: DefaultGroup, checkpoints: Checkpoint, namespaces: _ => Observed(catalog), metrics: MetricValues);
        var service = new IntegrationReviewService(catalogService, fakes.Engine(), new IntegrationContractStore(db, catalogService, NullLogger<IntegrationContractStore>.Instance), db, NullLogger<IntegrationReviewService>.Instance);
        var run = await service.RunAsync(Request, "Development", DevUrl);
        var calls = fakes.AzureCalls;
        calls.Should().BeGreaterThan(0);

        var stored = await service.GetRunAsync(run.RunId);
        fakes.AzureCalls.Should().Be(calls, "opening a stored review never re-queries Azure");
        fakes.Probe.Calls.Should().Be(1);
        JsonSerializer.Serialize(stored!.EventHubSnapshot, Json).Should().Be(JsonSerializer.Serialize(run.EventHubSnapshot, Json));
        stored.WhatWasTested.Should().Equal(run.WhatWasTested);
        stored.WhatWasNotAssessed.Should().Equal(run.WhatWasNotAssessed);
        stored.Domains.Select(d => (d.Domain, d.StateLabel)).Should().Equal(run.Domains.Select(d => (d.Domain, d.StateLabel)));
        stored.Domains.SelectMany(d => d.Observed).Should().Equal(run.Domains.SelectMany(d => d.Observed));
    }

    [Fact]
    public void AReviewStoredBeforeEventHubSnapshots_StillReads()
    {
        var legacy = """{"runId":"8f0c7a4e-0d2c-4c55-9b0e-2d6a3b1b9d11","environmentId":"dev","outcome":"CompletedWithLimitations","domains":[{"domain":"MessageFlow","stateLabel":"Not assessed"}]}""";
        var result = JsonSerializer.Deserialize<IntegrationReviewResult>(legacy, Json)!;
        result.EventHubSnapshot.Should().BeEmpty();
        result.WhatWasTested.Should().BeEmpty();
        result.Domains.Single().Observed.Should().BeEmpty();
    }

    // ── §63 Secret redaction ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SecretsInAzureErrors_NeverReachTheResultTheDatabaseOrTheLog()
    {
        var secret = $"Endpoint=sb://x.servicebus.windows.net/;SharedAccessKeyName=root;SharedAccessKey={Sentinel};sig={Sentinel}";
        var logger = new CapturingLogger<ArmEventHubNamespaceSource>();
        var calls = 0;
        var handler = new Handler(request => ++calls == 1
            ? new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent($"{{\"error\":{{\"message\":\"{secret}\"}}}}", Encoding.UTF8, "application/json") }
            : throw new HttpRequestException($"Connection failed: {secret}"));
        var source = new ArmEventHubNamespaceSource(new StaticCredential(), new HttpClient(handler), logger);
        await using var db = Db();
        var catalogService = new IntegrationCatalogService(db, NullLogger<IntegrationCatalogService>.Instance);
        var catalog = await catalogService.GetAsync(DevId, "Development", DevUrl);
        var platform = catalog.Platforms.Single(p => p.Id == M2lbDevIntegrationSeed.PlatformId);

        var forbidden = await source.ReadAsync(platform, CancellationToken.None);
        forbidden.State.Should().Be(IntegrationEvidenceState.NotAuthorized);
        var failed = await source.ReadAsync(platform, CancellationToken.None);
        failed.State.Should().Be(IntegrationEvidenceState.Unavailable);
        failed.Reason.Should().Be("Event Hub namespace metadata could not be read (HttpRequestException).");

        var fakes = Evidence(namespaces: _ => failed, metrics: () => new EventHubMetricsEvidence { State = IntegrationEvidenceState.NotAuthorized, Reason = forbidden.Reason, CapturedAt = DateTimeOffset.UtcNow, WindowHours = 24 });
        var service = new IntegrationReviewService(catalogService, fakes.Engine(), new IntegrationContractStore(db, catalogService, NullLogger<IntegrationContractStore>.Instance), db, NullLogger<IntegrationReviewService>.Instance);
        var run = await service.RunAsync(Request, "Development", DevUrl);
        var stored = (await db.IntegrationReviewRuns.AsNoTracking().SingleAsync(r => r.Id == run.RunId)).ResultJson;

        foreach (var text in new[] { JsonSerializer.Serialize(forbidden, Json), JsonSerializer.Serialize(failed, Json), JsonSerializer.Serialize(run, Json), stored, string.Join('\n', logger.Lines) })
        {
            text.Should().NotContain(Sentinel);
            text.Should().NotContainAny("SharedAccessKey", "sig=", "Endpoint=sb://", "token-");
        }
        handler.Requests.Should().OnlyContain(r => r.Method == HttpMethod.Get);
    }

    // ── Adapter parsing: GET only, paged, retention in hours ───────────────────────────────────────────────────────

    [Fact]
    public async Task NamespaceSource_ReadsNamespaceAndPagedHubList_WithGetOnly()
    {
        var handler = new Handler(request =>
        {
            var url = request.RequestUri!.ToString();
            var body = url.Contains("skip=1") ? """{"value":[{"name":"connect-status","properties":{"status":"Active","partitionCount":1,"messageRetentionInDays":7}}]}"""
                : url.Contains("/eventhubs") ? """{"value":[{"name":"m2lb-cdc-dev.birkm2lb.dbo.barntype","properties":{"status":"Active","partitionCount":1,"retentionDescription":{"retentionTimeInHours":168}}}],"nextLink":"https://management.azure.com/next?skip=1"}"""
                : """{"location":"Norway East","sku":{"name":"Premium"},"properties":{"status":"Active","publicNetworkAccess":"Enabled","disableLocalAuth":false,"minimumTlsVersion":"1.2","privateEndpointConnections":[]}}""";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        });
        var catalog = await Catalog();
        var platform = catalog.Platforms.Single(p => p.Id == M2lbDevIntegrationSeed.PlatformId);
        var observation = await new ArmEventHubNamespaceSource(new StaticCredential(), new HttpClient(handler), NullLogger<ArmEventHubNamespaceSource>.Instance).ReadAsync(platform, CancellationToken.None);

        observation.State.Should().Be(IntegrationEvidenceState.Available);
        (observation.Status, observation.Sku, observation.PublicNetworkAccess, observation.DisableLocalAuth, observation.MinimumTlsVersion, observation.PrivateEndpointConnections)
            .Should().Be(("Active", "Premium", "Enabled", false, "1.2", 0));
        observation.Hubs.Should().HaveCount(2);
        observation.Hubs[0].Should().Be(new EventHubObservedHub { Name = "m2lb-cdc-dev.birkm2lb.dbo.barntype", Status = "Active", PartitionCount = 1, RetentionHours = 168 });
        observation.Hubs[1].RetentionHours.Should().Be(168, "days are converted to hours");
        handler.Requests.Should().HaveCount(3).And.OnlyContain(r => r.Method == HttpMethod.Get);
        handler.Requests[0].RequestUri!.AbsolutePath.Should().Be("/subscriptions/2fcdb9d0-22eb-43b0-b95e-7cbba08c34b0/resourceGroups/rg-m2lb-dev-integration-nwe/providers/Microsoft.EventHub/namespaces/evhns-m2lb-dev-nwe-001");
    }

    [Fact]
    public async Task NamespaceSource_WithoutEventHubMetadataEnabled_IsNotConfigured_AndMakesNoCall()
    {
        var handler = new Handler(_ => throw new InvalidOperationException("no call"));
        var catalog = await Catalog();
        var platform = catalog.Platforms.Single(p => p.Id == M2lbDevIntegrationSeed.PlatformId);
        platform = platform with { RuntimeEvidence = platform.RuntimeEvidence! with { EventHubMetadata = false } };
        var observation = await new ArmEventHubNamespaceSource(new StaticCredential(), new HttpClient(handler), NullLogger<ArmEventHubNamespaceSource>.Instance).ReadAsync(platform, CancellationToken.None);
        observation.State.Should().Be(IntegrationEvidenceState.NotConfigured);
        handler.Requests.Should().BeEmpty();
    }

    // ── §45 No data-plane message operation ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void EventHubRuntimeEvidence_HasNoSendReceiveProcessorOrWritePath()
    {
        var root = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(root, "BirkNext.Api"))) root = Path.GetDirectoryName(root)!;
        var integrations = Path.Combine(root, "BirkNext.Api", "Services", "Integrations");
        var files = Directory.GetFiles(Path.Combine(integrations, "EventHub"), "*.cs").Append(Path.Combine(integrations, "AzureIntegrationEvidence.cs"))
            .Append(Path.Combine(integrations, "IntegrationReviewEngine.cs")).ToList();
        files.Should().HaveCountGreaterThanOrEqualTo(5);
        var forbidden = new Regex(
            @"\b(EventHubProducerClient|EventProcessorClient|EventProcessor<|PartitionReceiver|EventHubBufferedProducerClient|ReceiveBatchAsync|ReadEventsAsync|ReadEventsFromPartitionAsync|ReceiveEvents|SendAsync\(\s*(new\s+)?(EventData|\w*[Bb]atch)|CreateBatchAsync|UpdateCheckpointAsync|ClaimOwnershipAsync|BlobLeaseClient|UploadAsync|SetMetadataAsync|DeleteAsync|DeleteIfExistsAsync|CreateIfNotExistsAsync|CreateOrUpdate|ServiceBusReceiver|ServiceBusSender)\b|HttpMethod\.(Put|Post|Delete|Patch)\b");
        forbidden.IsMatch("await client.ReceiveBatchAsync(10)").Should().BeTrue("the guard must match a receive");
        forbidden.IsMatch("await producer.SendAsync(new EventData(x))").Should().BeTrue("the guard must match a send");
        forbidden.IsMatch("new HttpRequestMessage(HttpMethod.Put, url)").Should().BeTrue("the guard must match an ARM write");
        foreach (var file in files)
        {
            // Code only: doc comments name the SDK types being avoided.
            var code = string.Join('\n', File.ReadAllLines(file).Where(l => !l.TrimStart().StartsWith("//")));
            forbidden.Matches(code).Select(m => m.Value).Should().BeEmpty($"{Path.GetFileName(file)} is administrative metadata only");
        }
        // The one data-plane client used reads management properties only; it never opens a receive link.
        var evidence = File.ReadAllText(Path.Combine(integrations, "AzureIntegrationEvidence.cs"));
        var metadataSource = evidence[evidence.IndexOf("class AzureEventHubMetadataSource", StringComparison.Ordinal)..evidence.IndexOf("class ArmConsumerGroupSource", StringComparison.Ordinal)];
        Regex.Matches(metadataSource, @"client\.(\w+)Async").Select(m => m.Groups[1].Value).Distinct().Should().BeEquivalentTo(["GetEventHubProperties", "GetPartitionProperties"]);
    }
}
