using System.Collections.Concurrent;
using System.Text.Json;
using Azure.Core;
using Azure.Messaging.EventHubs;
using BirkNext.Api.Data;
using BirkNext.Api.Models;
using BirkNext.Api.Services.ActiveCdcTests;
using BirkNext.Api.Services.ActiveEventTesting;
using BirkNext.Api.Services.ActiveEventTesting.Observation;
using BirkNext.Api.Services.ActiveEventTesting.Providers.M2lbPerson;
using BirkNext.Api.Services.ActiveEventTesting.Providers.SkoleNaervaer;
using BirkNext.Api.Services.Integrations;
using BirkNext.Api.Services.IntegrationQuality;
using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.Integrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BirkNext.Api.Tests.Services.ActiveEventTesting;

/// <summary>
/// Everything an active event run touches, faked: no Azure call, no network. The Event Hub producer factory is a FAKE that records what
/// would have been sent — these are unit/component tests of the production lifecycle, never integration tests against Azure.
/// </summary>
internal sealed class ActiveEventTestHarness : IAsyncDisposable
{
    public const string Env = "dev-env";
    public const string IntegrationId = "dev:eventhub:birk-cdc:dbo.Person";
    public const string Fqdn = "evhns-m2lb-dev-nwe-001.servicebus.windows.net";
    public const string Hub = "m2lb-cdc-dev.BirkM2LB.dbo.Person";
    public const string SkoleIntegrationId = "dev:eventhub:birk-cdc:dbo.Utdanning";
    public const string SkoleHub = "m2lb-cdc-dev.BirkM2LB.dbo.Utdanning";
    public const string Sentinel = "SECRET_SENTINEL_CDC_123";

    private readonly ServiceProvider _provider;
    public Dictionary<string, string?> Settings { get; } = DefaultSettings();
    public FakeCatalog Catalog { get; } = new();
    public FakeProducerFactory Producers { get; } = new();
    public FakeMetadata Metadata { get; } = new();
    public FakeCheckpoints Checkpoints { get; } = new();
    public FakeTelemetry Telemetry { get; } = new();
    public FakeAzure Azure { get; } = new();
    public List<IActiveEventDownstreamVerifier> Verifiers { get; } = [];
    public ActiveEventRunCoordinator Coordinator { get; } = new();
    public ActiveCdcRunStore LegacyStore { get; }
    public ActiveEventRunStore History { get; }
    public CapturingLoggerProvider Logs { get; } = new();

    public ActiveEventTestHarness()
    {
        var services = new ServiceCollection();
        var db = Guid.NewGuid().ToString();
        services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(db));
        _provider = services.BuildServiceProvider();
        LegacyStore = new ActiveCdcRunStore(_provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<ActiveCdcRunStore>.Instance);
        History = new ActiveEventRunStore(_provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<ActiveEventRunStore>.Instance);
    }

    public static Dictionary<string, string?> DefaultSettings() => new()
    {
        ["ActiveEventTesting:Enabled"] = "true",
        ["ActiveEventTesting:AllowedDestinations:0:NamespaceFqdn"] = Fqdn,
        ["ActiveEventTesting:AllowedDestinations:0:EventHub"] = Hub,
        ["ActiveEventTesting:AllowedDestinations:1:NamespaceFqdn"] = Fqdn,
        ["ActiveEventTesting:AllowedDestinations:1:EventHub"] = SkoleHub,
        ["ActiveEventTesting:SendTimeoutSeconds"] = "5",
        ["ActiveEventTesting:ObservationSeconds"] = "0",
        ["ActiveEventTesting:PollSeconds"] = "1",
        ["ActiveEventTesting:SyntheticIdentityRanges:m2lb.person.PersonPK:Min"] = "900000000",
        ["ActiveEventTesting:SyntheticIdentityRanges:m2lb.person.PersonPK:Max"] = "900000099",
        ["TargetEnvironments:Trusted:0:EnvironmentId"] = Env,
        ["TargetEnvironments:Trusted:0:DisplayName"] = "M2LB DEV",
        ["TargetEnvironments:Trusted:0:EnvironmentType"] = "Development",
        ["TargetEnvironments:Trusted:0:ExecutionAllowed"] = "true",
        ["TargetEnvironments:Trusted:0:TargetUrl"] = "https://m2lb-dev.example.test",
        ["TargetEnvironments:Trusted:0:IntegrationIds:0"] = IntegrationId,
        ["TargetEnvironments:Trusted:0:IntegrationIds:1"] = SkoleIntegrationId,
        ["TargetEnvironments:Trusted:0:IntegrationIds:2"] = "dev:eventhub:orders",
    };

    public IConfiguration Configuration() => new ConfigurationBuilder().AddInMemoryCollection(Settings).Build();
    public ActiveEventPolicy Policy() => new(ActiveEventOptions.From(Configuration()));
    public AppDbContext Db() => _provider.CreateScope().ServiceProvider.GetRequiredService<AppDbContext>();
    public IActiveEventSyntheticIdentityReservation Identities() => new SyntheticIdentityReservation(Configuration(), _provider.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System);

    public M2lbPersonScenarioProvider Person() => new(new IqrSourceStore(Db()), Policy(), Identities(), LegacyStore, Configuration(), TimeProvider.System);
    public SkoleNaervaerScenarioProvider Skole() => new(new IqrSourceStore(Db()), Policy(), Identities(), Configuration(), TimeProvider.System);

    public IActiveEventLifecycleService Lifecycle(params IActiveEventScenarioProvider[] additionalProviders) =>
        Lifecycle([Person(), Skole(), .. additionalProviders], out _);

    public IActiveEventLifecycleService Lifecycle(IActiveEventScenarioProvider[] providers, out ActiveEventExecutionRunner runner)
    {
        var policy = Policy();
        var loggers = LoggerFactory.Create(builder => builder.AddProvider(Logs));
        var sender = new AzureEventHubTestSender(Azure, Producers, loggers.CreateLogger<AzureEventHubTestSender>());
        var transports = new ActiveEventTransportRegistry([new EventHubActiveEventTransportProvider(policy, sender, TimeProvider.System), .. ExtraTransports]);
        runner = new ActiveEventExecutionRunner(transports,
            [new TelemetryConsumerActivityProvider(Telemetry, TimeProvider.System)],
            [new EventHubCheckpointContinuityProvider(Metadata, Checkpoints, policy, TimeProvider.System)],
            Verifiers, TimeProvider.System, policy.Options.MaxEventsPerRun);
        return new ActiveEventLifecycleService(Catalog, policy, TrustedExecutionEnvironmentRegistry.From(Configuration()), new ActiveEventScenarioRegistry(providers),
            transports, runner, History, new LegacyActiveCdcHistory(LegacyStore, TimeProvider.System), Coordinator, TimeProvider.System,
            loggers.CreateLogger<ActiveEventLifecycleService>());
    }

    public List<IActiveEventTransportProvider> ExtraTransports { get; } = [];

    public Task<IqrSourceSnapshot> AddSnapshotAsync(IEnumerable<string>? cdcFields = null, DateTimeOffset? at = null, string? archiveSha = null) =>
        AddSnapshotAsync(Snapshot(cdcFields ?? M2lbPersonScenarios.Fields, archiveSha: archiveSha), IntegrationId, at);

    public async Task<IqrSourceSnapshot> AddSnapshotAsync(IqrSourceSnapshot template, string integrationId, DateTimeOffset? at = null)
    {
        var snapshot = template with { AnalyzedAt = at ?? DateTimeOffset.UtcNow, IntegrationId = integrationId };
        using var db = Db();
        db.IqrSourceSnapshots.Add(new IqrSourceSnapshotRecord { Id = snapshot.Id, EnvironmentId = Env, IntegrationId = integrationId, AnalyzedAt = snapshot.AnalyzedAt,
            EvidenceJson = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions(JsonSerializerDefaults.Web)) });
        await db.SaveChangesAsync();
        return snapshot;
    }

    public static IqrSourceSnapshot Snapshot(IEnumerable<string> cdcFields, string record = "PersonRecord", string? archiveSha = null) => new()
    {
        Archive = new("adapter.zip", archiveSha ?? new string('a', 64), 10), Status = SourceAnalysisStatus.Ready, AnalyzerVersion = 2, Commit = "abc123",
        IntegrationPath = new IntegrationPathEvidence
        {
            Stages = [new(record, SourceStageKind.AdapterModel, record, "Adapter.Domain", ["Id"], new($"Adapter/{record}.cs", 3), SourceConfidence.StrongSourceEvidence, "")],
            Fields = cdcFields.Select(f => new FieldTrace { Key = $"CDC {record}.{f}", OriginField = f }).ToList(),
        },
    };

    public static ActiveEventRunRequest Request(string scenarioId = M2lbPersonScenarios.NormalPersonId, string extensionId = M2lbPersonScenarioProvider.Id,
        string integrationId = IntegrationId, string hub = Hub, Guid? snapshot = null) => new()
    {
        TargetEnvironmentId = Env, IntegrationId = integrationId, ExtensionId = extensionId, ScenarioId = scenarioId, SourceSnapshotId = snapshot,
        Confirmed = true, ConfirmedDestination = hub,
    };

    public async Task<ActiveEventRunResult> CompletedAsync(IActiveEventLifecycleService lifecycle, ActiveEventRunResult started)
    {
        await Coordinator.Completion(started.RunId).WaitAsync(TimeSpan.FromSeconds(30));
        return (await lifecycle.GetAsync(started.RunId, default))!;
    }

    public async ValueTask DisposeAsync() => await _provider.DisposeAsync();

    internal sealed class FakeCatalog : IIntegrationCatalogService
    {
        public IntegrationPlatform Platform { get; set; } = new()
        {
            Id = "dev:eventhub:m2lb", EnvironmentId = Env, Name = "M2LB DEV Event Hubs", NamespaceFqdn = Fqdn,
            RuntimeEvidence = new IntegrationRuntimeEvidenceSettings { EventHubMetadata = true, ExpectedConsumerGroup = "$Default", CheckpointBlobEndpoint = "https://stm2bbirkdevnwe001.blob.core.windows.net/", CheckpointContainerName = "person-adapter" },
        };
        public IntegrationDefinition Integration { get; set; } = new()
        {
            Id = IntegrationId, EnvironmentId = Env, PlatformId = "dev:eventhub:m2lb", DisplayName = "BIRK Person CDC", Kind = IntegrationKind.EventHub, Enabled = true,
            SourceResource = "BirkM2LB.dbo.Person", EndpointOrTopic = Hub, Consumer = new IntegrationConsumer { DisplayName = "Person Adapter", ContainerApp = "ca-person-adapter" },
        };
        public List<IntegrationDefinition> Extra { get; } = [];
        public List<(string? Type, string? Url)> Reads { get; } = [];
        public Task<IntegrationCatalog> GetAsync(string environmentId, string? environmentType, string? targetUrl, CancellationToken ct = default)
        {
            lock (Reads) Reads.Add((environmentType, targetUrl));
            return Task.FromResult(new IntegrationCatalog { EnvironmentId = environmentId, Platforms = [Platform], Integrations = [Integration, .. Extra] });
        }
        public Task<IntegrationCatalog?> ApplyTemplateAsync(string environmentId, string templateId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IntegrationDefinition> CreateAsync(string environmentId, IntegrationDefinition definition, CancellationToken ct = default) => Task.FromResult(definition);
        public Task<IntegrationDefinition?> UpdateAsync(string environmentId, string id, IntegrationDefinition definition, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IntegrationDefinition?> SetEnabledAsync(string environmentId, string id, bool enabled, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(string environmentId, string id, CancellationToken ct = default) => Task.FromResult(true);
        public Task<IntegrationPlatform?> UpdatePlatformAsync(string environmentId, string id, IntegrationPlatform platform, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> ImportLegacyAsync(string environmentId, IReadOnlyList<IntegrationConfigDto> legacy, CancellationToken ct = default) => throw new NotSupportedException();
    }

    internal sealed class FakeAzure : IIntegrationAzureCredential
    {
        public bool Enabled { get; set; } = true;
        public TokenCredential? Credential => Enabled ? new StaticToken() : null;
        public string DisabledReason => "Azure is disabled for this test instance.";
        private sealed class StaticToken : TokenCredential
        {
            public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) => new("test", DateTimeOffset.MaxValue);
            public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) => new(GetToken(requestContext, cancellationToken));
        }
    }

    /// <summary>FAKE producer: records the EventData (and partition key) that would have been sent. Nothing reaches Azure.</summary>
    internal sealed class FakeProducerFactory : IEventHubTestProducerFactory
    {
        public ConcurrentQueue<(string Fqdn, string Hub, TimeSpan TryTimeout)> Created { get; } = new();
        public ConcurrentQueue<EventData> Sent { get; } = new();
        public ConcurrentQueue<string?> PartitionKeys { get; } = new();
        public Func<EventData, CancellationToken, Task> Behaviour { get; set; } = (_, _) => Task.CompletedTask;
        public IEventHubTestProducer Create(string namespaceFqdn, string eventHub, TokenCredential credential, TimeSpan tryTimeout)
        {
            Created.Enqueue((namespaceFqdn, eventHub, tryTimeout));
            return new Producer(this);
        }
        private sealed class Producer(FakeProducerFactory owner) : IEventHubTestProducer
        {
            public async Task SendAsync(EventData eventData, string? partitionKey, CancellationToken ct)
            {
                owner.Sent.Enqueue(eventData);
                owner.PartitionKeys.Enqueue(partitionKey);
                await owner.Behaviour(eventData, ct);
            }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    internal sealed class FakeMetadata : IEventHubMetadataSource
    {
        private int _calls;
        public List<PartitionRuntime> Before { get; set; } = [new("0", 10, DateTimeOffset.UtcNow, false), new("1", 5, DateTimeOffset.UtcNow, false)];
        public List<PartitionRuntime> After { get; set; } = [new("0", 11, DateTimeOffset.UtcNow, false), new("1", 5, DateTimeOffset.UtcNow, false)];
        public IntegrationEvidenceState State { get; set; } = IntegrationEvidenceState.Available;
        public int Calls => _calls;
        public IntegrationEvidenceAdapterStatus Describe(IntegrationPlatform platform) => new() { State = State, Reason = State == IntegrationEvidenceState.Available ? "Configured (test)." : "Azure is disabled for this test instance." };
        public Task<EvidenceResult<EventHubRuntimeMetadata>> GetHubAsync(IntegrationPlatform platform, string hubName, CancellationToken ct)
        {
            var first = Interlocked.Increment(ref _calls) == 1;
            return Task.FromResult(EvidenceResult<EventHubRuntimeMetadata>.Available(IntegrationEvidenceSource.AzureMetadata, new(true, first ? Before : After)));
        }
    }

    internal sealed class FakeCheckpoints : ICheckpointEvidenceSource
    {
        private int _calls;
        public List<PartitionCheckpoint> Before { get; set; } = [new("0", 9, 0, DateTimeOffset.UtcNow), new("1", 5, 0, DateTimeOffset.UtcNow)];
        public List<PartitionCheckpoint> After { get; set; } = [new("0", 11, 0, DateTimeOffset.UtcNow), new("1", 5, 0, DateTimeOffset.UtcNow)];
        public List<string> Groups { get; } = [];
        public IntegrationEvidenceAdapterStatus Describe(IntegrationPlatform platform) => new() { State = IntegrationEvidenceState.Available, Reason = "Configured (test)." };
        public Task<EvidenceResult<CheckpointEvidence>> GetAsync(IntegrationPlatform platform, string hubName, string consumerGroup, CancellationToken ct)
        {
            lock (Groups) Groups.Add(consumerGroup);
            var first = Interlocked.Increment(ref _calls) == 1;
            return Task.FromResult(EvidenceResult<CheckpointEvidence>.Available(IntegrationEvidenceSource.CheckpointStore, new(consumerGroup, first ? Before : After, 1)));
        }
    }

    internal sealed class FakeTelemetry : ITelemetryEvidenceSource
    {
        public IntegrationEvidenceState State { get; set; } = IntegrationEvidenceState.NotConfigured;
        public DateTimeOffset? LastActivity { get; set; }
        public IntegrationEvidenceAdapterStatus Describe(IntegrationPlatform platform) => new() { State = State, Reason = State == IntegrationEvidenceState.Available ? "Configured (test)." : "No telemetry workspace is configured (test)." };
        public Task<EvidenceResult<ConsumerTelemetry>> GetConsumerAsync(IntegrationPlatform platform, string roleName, int windowHours, CancellationToken ct) =>
            Task.FromResult(EvidenceResult<ConsumerTelemetry>.Available(IntegrationEvidenceSource.ApplicationInsights,
                new ConsumerTelemetry(0, 0, 0, 0, 0, 12, 0, 0, 0, null, LastActivity, null, windowHours)));
    }

    internal sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<string> Lines { get; } = new();
        public ILogger CreateLogger(string categoryName) => new Logger(this);
        public void Dispose() { }
        private sealed class Logger(CapturingLoggerProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                owner.Lines.Enqueue(formatter(state, exception) + (exception is null ? "" : " " + exception));
        }
    }
}
