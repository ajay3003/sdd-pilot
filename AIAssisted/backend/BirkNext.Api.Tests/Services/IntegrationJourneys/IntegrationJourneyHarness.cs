using BirkNext.Api.Data;
using BirkNext.Api.Services.ActiveEventTesting;
using BirkNext.Api.Services.Integrations;
using BirkNext.Api.Services.IntegrationJourneys;
using BirkNext.Api.Services.SourceAnalysis;
using BirkNext.Api.Tests.Services.ActiveEventTesting;
using BirkNext.Integrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace BirkNext.Api.Tests.Services.IntegrationJourneys;

/// <summary>
/// The real journey service, runner, registry, store and trusted-environment registry over fakes for the IQR catalog, Source Analysis and the
/// IQR message-flow configuration. Executors and observers are fakes: nothing is submitted, published or sent anywhere — these are component
/// tests of the production lifecycle, not integration tests against Altinn, Service Bus or Event Hubs.
/// </summary>
internal sealed class IntegrationJourneyHarness : IAsyncDisposable
{
    private readonly ServiceProvider _provider;
    public ActiveEventTestHarness Active { get; } = new();
    public FakeSources Sources { get; } = new();
    public FakeMessageFlows MessageFlows { get; } = new();
    public List<IJourneyStepExecutor> Executors { get; } = [];
    public List<IJourneyStepObserver> Observers { get; } = [];
    public IntegrationJourneyRunStore Store { get; }

    public IntegrationJourneyHarness()
    {
        var services = new ServiceCollection();
        var db = Guid.NewGuid().ToString();
        services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(db));
        _provider = services.BuildServiceProvider();
        Store = new IntegrationJourneyRunStore(_provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrationJourneyRunStore>.Instance);
    }

    /// <summary>Enrolls an integration id in the trusted DEV environment (backend configuration, never a request).</summary>
    public void Enroll(string integrationId)
    {
        var index = Active.Settings.Keys.Count(key => key.StartsWith("TargetEnvironments:Trusted:0:IntegrationIds:", StringComparison.Ordinal));
        Active.Settings[$"TargetEnvironments:Trusted:0:IntegrationIds:{index}"] = integrationId;
    }

    public IntegrationJourneyService Service(params IIntegrationJourneyPack[] packs) =>
        new(new IntegrationJourneyPackRegistry(packs), Active.Catalog, TrustedExecutionEnvironmentRegistry.From(Active.Configuration()), Sources, Active.Lifecycle(),
            new IntegrationJourneyRunner(Executors, Observers, TimeProvider.System), Store, new IntegrationJourneyRunGate(), TimeProvider.System,
            NullLogger<IntegrationJourneyService>.Instance);

    public async ValueTask DisposeAsync()
    {
        await Active.DisposeAsync();
        await _provider.DisposeAsync();
    }

    internal sealed class FakeSources : IReviewSourceEvidenceProvider
    {
        public List<IqrSourceSnapshot> Snapshots { get; } = [];
        public bool SourceAnalysisEnabled { get; set; } = true;
        public Task<IReadOnlyList<IqrSourceSnapshot>> ListAsync(string environmentId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<IqrSourceSnapshot>>(Snapshots.ToArray());
        public Task<IqrSourceSnapshot?> ResolveAsync(string environmentId, Guid snapshotId, CancellationToken ct = default) => Task.FromResult(Snapshots.FirstOrDefault(item => item.Id == snapshotId));
    }

    internal sealed class FakeMessageFlows : IIntegrationMessageFlowStore
    {
        public AltinnTestConfiguration Configuration { get; set; } = MessageFlowReviewExamples.SkolenærværInitialConfiguration;
        public Task<IntegrationMessageFlowPackage> GetAsync(string environmentId, CancellationToken ct = default) =>
            Task.FromResult(new IntegrationMessageFlowPackage(new MessageFlowDefinition { EnvironmentId = environmentId }, Configuration, new(false, 0, 0, [], []), [], []));
        public Task<(IntegrationMessageFlowPackage? Package, string? Error)> SaveAsync(string environmentId, IntegrationMessageFlowPackage request, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}

/// <summary>An unrelated project's journey: external REST → ingestion → processor → Service Bus → downstream consumer. No Skolenærvær name anywhere.</summary>
internal sealed class ExampleOrdersJourneyPack : IIntegrationJourneyPack
{
    public const string IntakeId = "dev:http:orders-intake";
    public const string BusId = "dev:servicebus:orders-ready";

    public string PackId => "example.orders";
    public string PackVersion => "1";
    public string DisplayName => "Orders journeys";
    public string Description => "Example pack proving the journey engine is project neutral.";
    public IReadOnlyList<string> Limitations => [];
    public List<JourneyPrerequisite> Extra { get; } = [];

    public IReadOnlyList<IntegrationJourneyDefinition> Journeys { get; } =
    [
        new()
        {
            PackId = "example.orders", JourneyId = "order-flow", DisplayName = "Order flow", ExecutionMode = JourneyExecutionMode.JourneyRunner,
            Integrations =
            [
                new("intake", "Orders intake API", IntegrationKind.HttpApi, "Web shop", "Ingestion", "Order submission"),
                new("bus", "Orders ready topic", IntegrationKind.ServiceBus, "Processor", "Fulfilment", "Processed orders"),
            ],
            Steps =
            [
                new("submit", "Submit order", JourneyStepKind.ExternalSubmission, "Web shop", "Accepted", IntegrationKey: "intake"),
                new("ingest", "Ingestion stores order", JourneyStepKind.DomainIntake, "Ingestion", "Stored"),
                new("process", "Processor maps order", JourneyStepKind.Mapping, "Processor", "Mapped"),
                new("publish", "Order published", JourneyStepKind.Publication, "Processor", "Published", IntegrationKey: "bus"),
                new("consume", "Fulfilment consumes order", JourneyStepKind.ConsumerProcessing, "Fulfilment", "Consumed", IntegrationKey: "bus"),
                new("verify", "Fulfilment state verified", JourneyStepKind.DownstreamVerification, "Fulfilment", "Verified"),
            ],
            Scenarios =
            [
                new("happy", "Valid order", "One synthetic order.", JourneyScenarioSupport.Supported),
                new("duplicate", "Duplicate order", "Same order twice.", JourneyScenarioSupport.NotAssessed, "Not assessed: semantics undefined."),
            ],
        },
    ];

    public IReadOnlyList<ArchitectureResponsibilityRule> Rules { get; } = [];

    public bool Matches(JourneyIntegrationRequirement requirement, IntegrationDefinition integration) =>
        (requirement.Key == "intake" && integration.Id == IntakeId) || (requirement.Key == "bus" && integration.Id == BusId);

    public Task<IReadOnlyList<JourneyPrerequisite>> PrerequisitesAsync(IntegrationJourneyDefinition journey, JourneyPackContext context, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<JourneyPrerequisite>>(Extra.ToArray());

    public static IntegrationDefinition Intake => new() { Id = IntakeId, EnvironmentId = ActiveEventTestHarness.Env, DisplayName = "Orders intake", Kind = IntegrationKind.HttpApi, Enabled = true };
    public static IntegrationDefinition Bus => new() { Id = BusId, EnvironmentId = ActiveEventTestHarness.Env, DisplayName = "Orders ready", Kind = IntegrationKind.ServiceBus, Enabled = true };
}

/// <summary>Fake executor: "performs" one step kind and records it. Never touches a network.</summary>
internal sealed class FakeExecutor(JourneyStepKind kind, JourneyStepState result) : IJourneyStepExecutor
{
    public List<string> Executed { get; } = [];
    public bool CanExecute(JourneyStepContext context, out string reason)
    {
        reason = "fake";
        return context.Step.Kind == kind;
    }

    public Task<JourneyStepResult> ExecuteAsync(JourneyStepContext context, CancellationToken ct)
    {
        Executed.Add(context.Step.StepId);
        return Task.FromResult(new JourneyStepResult { State = result, Evidence = "fake submission", EvidenceSource = "Fake executor", Correlation = "TEST-REF-1" });
    }
}

/// <summary>Fake observer: returns a configured state per step id.</summary>
internal sealed class FakeObserver(IReadOnlyDictionary<string, JourneyStepState> states) : IJourneyStepObserver
{
    public bool CanObserve(JourneyStepContext context, out string reason)
    {
        reason = "fake";
        return states.ContainsKey(context.Step.StepId);
    }

    public Task<JourneyStepResult> ObserveAsync(JourneyStepContext context, CancellationToken ct) =>
        Task.FromResult(new JourneyStepResult { State = states[context.Step.StepId], Evidence = "fake observation", EvidenceSource = "Fake observer" });
}
