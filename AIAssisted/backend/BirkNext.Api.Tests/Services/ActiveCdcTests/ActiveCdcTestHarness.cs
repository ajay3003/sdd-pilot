using System.Collections.Concurrent;
using System.Text.Json;
using Azure.Core;
using Azure.Messaging.EventHubs;
using BirkNext.Api.Data;
using BirkNext.Api.Models;
using BirkNext.Api.Services.ActiveCdcTests;
using BirkNext.Api.Services.Integrations;
using BirkNext.Api.Services.IntegrationQuality;
using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.Integrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BirkNext.Api.Tests.Services.ActiveCdcTests;

/// <summary>Everything an active CDC run touches, faked: no Azure call, no network. The producer factory records what would have been sent.</summary>
internal sealed class ActiveCdcTestHarness : IAsyncDisposable
{
    public const string Env = "dev-env";
    public const string IntegrationId = "dev:eventhub:birk-cdc:dbo.Person";
    public const string Fqdn = "evhns-m2lb-dev-nwe-001.servicebus.windows.net";
    public const string Hub = "m2lb-cdc-dev.BirkM2LB.dbo.Person";
    public const string Sentinel = "SECRET_SENTINEL_CDC_123";

    private readonly ServiceProvider _provider;
    public ActiveCdcOptions Options { get; set; }
    public FakeCatalog Catalog { get; } = new();
    public FakeProducerFactory Producers { get; } = new();
    public FakeMetadata Metadata { get; } = new();
    public FakeCheckpoints Checkpoints { get; } = new();
    public FakeAzure Azure { get; } = new();
    public ActiveCdcRunCoordinator Coordinator { get; } = new();
    public ActiveCdcRunStore Store { get; }
    public CapturingLoggerProvider Logs { get; } = new();

    public ActiveCdcTestHarness(ActiveCdcOptions? options = null)
    {
        Options = options ?? Enabled();
        var services = new ServiceCollection();
        var db = Guid.NewGuid().ToString();
        services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(db));
        _provider = services.BuildServiceProvider();
        Store = new ActiveCdcRunStore(_provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<ActiveCdcRunStore>.Instance);
    }

    public static ActiveCdcOptions Enabled() => new()
    {
        Enabled = true, AllowedDestinations = [new(Fqdn, Hub)], SyntheticPersonPkMin = 900_000_000, SyntheticPersonPkMax = 900_000_099,
        SendTimeoutSeconds = 5, ObservationSeconds = 0, PollSeconds = 1,
    };

    public AppDbContext Db() => _provider.CreateScope().ServiceProvider.GetRequiredService<AppDbContext>();

    public IActiveCdcTestService Service()
    {
        var policy = new ActiveCdcPolicy(Options);
        var loggers = LoggerFactory.Create(b => b.AddProvider(Logs));
        var sender = new AzureEventHubTestSender(Azure, Producers, loggers.CreateLogger<AzureEventHubTestSender>());
        var runner = new ActiveCdcRunner(policy, sender, Metadata, Checkpoints, Store, TimeProvider.System, loggers.CreateLogger<ActiveCdcRunner>());
        return new ActiveCdcTestService(Catalog, new IqrSourceStore(Db()), policy, sender, Checkpoints, Store, Coordinator, runner, TimeProvider.System);
    }

    public Task<IqrSourceSnapshot> AddSnapshotAsync(IEnumerable<string>? cdcFields = null, DateTimeOffset? at = null) =>
        AddSnapshotAsync(Snapshot(cdcFields ?? ActiveCdcScenarioCatalog.NormalPerson.Fields), at);

    public async Task<IqrSourceSnapshot> AddSnapshotAsync(IqrSourceSnapshot template, DateTimeOffset? at = null)
    {
        var snapshot = template with { AnalyzedAt = at ?? DateTimeOffset.UtcNow };
        using var db = Db();
        db.IqrSourceSnapshots.Add(new IqrSourceSnapshotRecord { Id = snapshot.Id, EnvironmentId = Env, IntegrationId = IntegrationId, AnalyzedAt = snapshot.AnalyzedAt,
            EvidenceJson = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions(JsonSerializerDefaults.Web)) });
        await db.SaveChangesAsync();
        return snapshot;
    }

    public static IqrSourceSnapshot Snapshot(IEnumerable<string> cdcFields) => new()
    {
        IntegrationId = IntegrationId, Archive = new("adapter.zip", new string('a', 64), 10), Status = SourceAnalysisStatus.Ready, AnalyzerVersion = 2, Commit = "abc123",
        IntegrationPath = new IntegrationPathEvidence
        {
            Stages = [new("PersonRecord", SourceStageKind.AdapterModel, "PersonRecord", "Adapter.Domain", ["PersonId"], new("Adapter/PersonRecord.cs", 3), SourceConfidence.StrongSourceEvidence, "")],
            Fields = cdcFields.Select(f => new FieldTrace { Key = $"CDC PersonRecord.{f}", OriginField = f }).ToList(),
        },
    };

    public static ActiveCdcRunRequest Request(IqrSourceSnapshot? snapshot = null) => new()
    {
        EnvironmentId = Env, EnvironmentName = "M2LB DEV", IntegrationId = IntegrationId, ScenarioId = ActiveCdcScenarioCatalog.NormalPersonId,
        SourceSnapshotId = snapshot?.Id, ConfirmedSend = true, ConfirmedEventHub = Hub,
    };

    public async Task<ActiveCdcRun> CompletedAsync(ActiveCdcRun started)
    {
        await Coordinator.Completion(started.RunId).WaitAsync(TimeSpan.FromSeconds(30));
        return (await Store.GetAsync(started.RunId, default))!;
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
            SourceResource = "BirkM2LB.dbo.Person", EndpointOrTopic = Hub, Consumer = new IntegrationConsumer { DisplayName = "Person Adapter" },
        };
        public Task<IntegrationCatalog> GetAsync(string environmentId, string? environmentType, string? targetUrl, CancellationToken ct = default) =>
            Task.FromResult(new IntegrationCatalog { EnvironmentId = environmentId, Platforms = [Platform], Integrations = [Integration] });
        public Task<IntegrationCatalog?> ApplyTemplateAsync(string environmentId, string templateId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IntegrationDefinition> CreateAsync(string environmentId, IntegrationDefinition definition, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IntegrationDefinition?> UpdateAsync(string environmentId, string id, IntegrationDefinition definition, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IntegrationDefinition?> SetEnabledAsync(string environmentId, string id, bool enabled, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(string environmentId, string id, CancellationToken ct = default) => throw new NotSupportedException();
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

    internal sealed class FakeProducerFactory : IEventHubTestProducerFactory
    {
        public ConcurrentQueue<(string Fqdn, string Hub, TimeSpan TryTimeout)> Created { get; } = new();
        public ConcurrentQueue<EventData> Sent { get; } = new();
        /// <summary>What a send does (default: accepted). Throw to simulate the service's answer.</summary>
        public Func<EventData, CancellationToken, Task> Behaviour { get; set; } = (_, _) => Task.CompletedTask;
        public IEventHubTestProducer Create(string namespaceFqdn, string eventHub, TokenCredential credential, TimeSpan tryTimeout)
        {
            Created.Enqueue((namespaceFqdn, eventHub, tryTimeout));
            return new Producer(this);
        }
        private sealed class Producer(FakeProducerFactory owner) : IEventHubTestProducer
        {
            public async Task SendAsync(EventData eventData, CancellationToken ct) { owner.Sent.Enqueue(eventData); await owner.Behaviour(eventData, ct); }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    internal sealed class FakeMetadata : IEventHubMetadataSource
    {
        private int _calls;
        public List<PartitionRuntime> Before { get; set; } = [new("0", 10, DateTimeOffset.UtcNow, false), new("1", 5, DateTimeOffset.UtcNow, false)];
        public List<PartitionRuntime> After { get; set; } = [new("0", 11, DateTimeOffset.UtcNow, false), new("1", 5, DateTimeOffset.UtcNow, false)];
        public bool Available { get; set; } = true;
        public Func<CancellationToken, Task>? Gate { get; set; }
        /// <summary>Per-call partition positions (call index from 0). Wins over Before/After when set.</summary>
        public Func<int, List<PartitionRuntime>>? Script { get; set; }
        public int Calls => _calls;
        public IntegrationEvidenceAdapterStatus Describe(IntegrationPlatform platform) => new() { State = IntegrationEvidenceState.Available };
        public async Task<EvidenceResult<EventHubRuntimeMetadata>> GetHubAsync(IntegrationPlatform platform, string hubName, CancellationToken ct)
        {
            if (Gate is not null) await Gate(ct);
            var call = Interlocked.Increment(ref _calls) - 1;
            var first = call == 0;
            return Available ? EvidenceResult<EventHubRuntimeMetadata>.Available(IntegrationEvidenceSource.AzureMetadata, new(true, Script?.Invoke(call) ?? (first ? Before : After)))
                : EvidenceResult<EventHubRuntimeMetadata>.Missing(IntegrationEvidenceState.NotConfigured, IntegrationEvidenceSource.AzureMetadata, "Azure is disabled for this test instance.");
        }
    }

    internal sealed class FakeCheckpoints : ICheckpointEvidenceSource
    {
        private int _calls;
        public List<PartitionCheckpoint> Before { get; set; } = [new("0", 9, 0, DateTimeOffset.UtcNow), new("1", 5, 0, DateTimeOffset.UtcNow)];
        public List<PartitionCheckpoint> After { get; set; } = [new("0", 11, 0, DateTimeOffset.UtcNow), new("1", 5, 0, DateTimeOffset.UtcNow)];
        public List<string> Groups { get; } = [];
        /// <summary>Per-call checkpoints (call index from 0). Wins over Before/After when set.</summary>
        public Func<int, List<PartitionCheckpoint>>? Script { get; set; }
        public bool Available { get; set; } = true;
        public IntegrationEvidenceAdapterStatus Describe(IntegrationPlatform platform) => new() { State = IntegrationEvidenceState.Available, Reason = "Configured (test)." };
        public Task<EvidenceResult<CheckpointEvidence>> GetAsync(IntegrationPlatform platform, string hubName, string consumerGroup, CancellationToken ct)
        {
            lock (Groups) Groups.Add(consumerGroup);
            var call = Interlocked.Increment(ref _calls) - 1;
            var first = call == 0;
            if (!Available) return Task.FromResult(EvidenceResult<CheckpointEvidence>.Missing(IntegrationEvidenceState.Unavailable, IntegrationEvidenceSource.CheckpointStore, "Checkpoint store unavailable (test)."));
            return Task.FromResult(EvidenceResult<CheckpointEvidence>.Available(IntegrationEvidenceSource.CheckpointStore, new(consumerGroup, Script?.Invoke(call) ?? (first ? Before : After), 1)));
        }
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
