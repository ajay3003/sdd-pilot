using System.Net;
using BirkNext.Api.Services;
using BirkNext.Api.Services.ContractAnalysis;
using BirkNext.Api.Services.IntegrationQuality;
using BirkNext.Api.Services.LocalHttpsProxy;
using BirkNext.LocalHttpsProxy;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BirkNext.Api.Tests.Services.IntegrationQuality;

/// <summary>
/// Phase 3 Checkpoint 5: snapshot lifecycle across successive reviews.
///
/// The required order is: resolve previous, execute, compare, finalise, save. Several tests here
/// fail if that order is inverted, because a run would then resolve its own snapshot as its
/// baseline and report NoChange where BaselineUnavailable is correct.
/// </summary>
public class SnapshotLifecycleTests
{
    private static NormalizedContract Contract(string name, params (string Name, bool Required)[] props) =>
        new()
        {
            Name = name,
            Schemas =
            [
                new NormalizedSchema
                {
                    Name = name,
                    Type = "object",
                    Properties = props
                        .Select(p => new NormalizedProperty { Name = p.Name, Type = "string", Required = p.Required })
                        .ToList(),
                    Required = props.Where(p => p.Required).Select(p => p.Name).ToList()
                }
            ]
        };

    private static IntegrationConfigDto EventHub(string id = "m1", string name = "Placement Events") => new()
    {
        Id = id, Name = name, Type = IntegrationType.EventHub, Enabled = true,
        Endpoint = "ns", Resource = "hub", ContractName = "PlacementUpdated",
        LogicalProducerService = "PlacementService", LogicalConsumerService = "NotificationService"
    };

    private static IntegrationQualityRequest Request(string environment, params IntegrationConfigDto[] integrations) =>
        new() { EnvironmentName = environment, Integrations = integrations.ToList() };

    private static IntegrationQualityReviewService Service(
        IIntegrationQualitySnapshotRepository repository,
        NormalizedContract? currentContract = null) =>
        new(new HttpClient(new StubHandler()) { BaseAddress = new Uri("https://unused.example.test/") },
            NullLogger<IntegrationQualityReviewService>.Instance,
            new FakeGateway(),
            new IntegrationRelationshipPopulationService(),
            null,
            currentContract is null ? null : new FakeMessageSchemaDiscovery(currentContract),
            null,
            repository);

    // ── First run ────────────────────────────────────────────────────────────

    [Fact]
    public async Task FirstRun_ReportsBaselineUnavailable()
    {
        var repository = new InMemoryIntegrationQualitySnapshotRepository();
        var report = await Service(repository, Contract("PlacementUpdated", ("id", true)))
            .AnalyzeAsync(Request("Dev", EventHub()));

        Assert.False(report.BaselineAvailable);
        Assert.Null(report.PreviousSnapshotId);
        Assert.Equal(ContractDriftState.BaselineUnavailable,
            report.Statuses.Single(s => s.IntegrationId == "m1").DriftState);
    }

    [Fact]
    public async Task FirstRun_SavesSnapshotWithNoPredecessor()
    {
        var repository = new InMemoryIntegrationQualitySnapshotRepository();
        var report = await Service(repository, Contract("PlacementUpdated", ("id", true)))
            .AnalyzeAsync(Request("Dev", EventHub()));

        Assert.Equal(SnapshotPersistenceState.Saved, report.SnapshotPersistenceState);
        Assert.NotNull(report.CurrentSnapshotId);

        var saved = await repository.GetLatestAsync("Dev");
        Assert.NotNull(saved);
        Assert.Null(saved!.PreviousSnapshotId);
        Assert.Equal(report.CurrentSnapshotId, saved.SnapshotId);
    }

    [Fact]
    public async Task FirstRun_DoesNotCompareAgainstItself()
    {
        // Regression guard for save-after-compare ordering. If the snapshot were saved before the
        // baseline was resolved, this run would find its own snapshot and report NoChange.
        var repository = new InMemoryIntegrationQualitySnapshotRepository();
        var report = await Service(repository, Contract("PlacementUpdated", ("id", true)))
            .AnalyzeAsync(Request("Dev", EventHub()));

        var status = report.Statuses.Single(s => s.IntegrationId == "m1");

        Assert.NotEqual(ContractDriftState.NoChange, status.DriftState);
        Assert.Equal(ContractDriftState.BaselineUnavailable, status.DriftState);
        Assert.Empty(report.HistoricalChanges);
    }

    [Fact]
    public async Task FirstRun_HasNoHistoricalChanges()
    {
        var repository = new InMemoryIntegrationQualitySnapshotRepository();
        var report = await Service(repository, Contract("PlacementUpdated", ("id", true)))
            .AnalyzeAsync(Request("Dev", EventHub()));

        Assert.Equal(0, report.HistoricalChangeCount);
    }

    // ── Second run ───────────────────────────────────────────────────────────

    [Fact]
    public async Task SecondRun_LinksToFirstSnapshot()
    {
        var repository = new InMemoryIntegrationQualitySnapshotRepository();
        var contract = Contract("PlacementUpdated", ("id", true));

        var first = await Service(repository, contract).AnalyzeAsync(Request("Dev", EventHub()));
        var second = await Service(repository, contract).AnalyzeAsync(Request("Dev", EventHub()));

        Assert.True(second.BaselineAvailable);
        Assert.Equal(first.CurrentSnapshotId, second.PreviousSnapshotId);
        Assert.NotEqual(first.CurrentSnapshotId, second.CurrentSnapshotId);
        Assert.NotNull(second.PreviousSnapshotCapturedAt);
    }

    [Fact]
    public async Task SecondRun_WithUnchangedContract_ReportsNoChange()
    {
        var repository = new InMemoryIntegrationQualitySnapshotRepository();
        var contract = Contract("PlacementUpdated", ("id", true));

        await Service(repository, contract).AnalyzeAsync(Request("Dev", EventHub()));
        var second = await Service(repository, contract).AnalyzeAsync(Request("Dev", EventHub()));

        Assert.Equal(ContractDriftState.NoChange,
            second.Statuses.Single(s => s.IntegrationId == "m1").DriftState);
    }

    [Fact]
    public async Task SecondRun_WithRemovedRequiredField_ReportsBreakingDrift()
    {
        var repository = new InMemoryIntegrationQualitySnapshotRepository();

        await Service(repository, Contract("PlacementUpdated", ("id", true), ("legacy", true)))
            .AnalyzeAsync(Request("Dev", EventHub()));

        var second = await Service(repository, Contract("PlacementUpdated", ("id", true)))
            .AnalyzeAsync(Request("Dev", EventHub()));

        var status = second.Statuses.Single(s => s.IntegrationId == "m1");
        Assert.Equal(ContractDriftState.BreakingChange, status.DriftState);
        Assert.True(status.DriftBreakingCount > 0);
    }

    [Fact]
    public async Task SecondRun_WithAddedOptionalField_ReportsNonBreakingDrift()
    {
        var repository = new InMemoryIntegrationQualitySnapshotRepository();

        await Service(repository, Contract("PlacementUpdated", ("id", true)))
            .AnalyzeAsync(Request("Dev", EventHub()));

        var second = await Service(repository, Contract("PlacementUpdated", ("id", true), ("note", false)))
            .AnalyzeAsync(Request("Dev", EventHub()));

        Assert.Equal(ContractDriftState.NonBreakingChange,
            second.Statuses.Single(s => s.IntegrationId == "m1").DriftState);
    }

    // ── Third run ────────────────────────────────────────────────────────────

    [Fact]
    public async Task ThirdRun_ChainsToSecondNotFirst()
    {
        var repository = new InMemoryIntegrationQualitySnapshotRepository();
        var contract = Contract("PlacementUpdated", ("id", true));

        var first = await Service(repository, contract).AnalyzeAsync(Request("Dev", EventHub()));
        var second = await Service(repository, contract).AnalyzeAsync(Request("Dev", EventHub()));
        var third = await Service(repository, contract).AnalyzeAsync(Request("Dev", EventHub()));

        Assert.Equal(second.CurrentSnapshotId, third.PreviousSnapshotId);
        Assert.NotEqual(first.CurrentSnapshotId, third.PreviousSnapshotId);
    }

    // ── Historical change detection through the service ──────────────────────

    [Fact]
    public async Task IntegrationAdded_IsDetected()
    {
        var repository = new InMemoryIntegrationQualitySnapshotRepository();
        var contract = Contract("PlacementUpdated", ("id", true));

        await Service(repository, contract).AnalyzeAsync(Request("Dev", EventHub()));

        var second = await Service(repository, contract)
            .AnalyzeAsync(Request("Dev", EventHub(), Rest("r1")));

        Assert.Contains(second.HistoricalChanges,
            c => c.Type == IntegrationHistoricalChangeType.IntegrationAdded);
    }

    [Fact]
    public async Task IntegrationRemoved_IsDetected()
    {
        var repository = new InMemoryIntegrationQualitySnapshotRepository();
        var contract = Contract("PlacementUpdated", ("id", true));

        await Service(repository, contract).AnalyzeAsync(Request("Dev", EventHub(), Rest("r1")));
        var second = await Service(repository, contract).AnalyzeAsync(Request("Dev", EventHub()));

        Assert.Contains(second.HistoricalChanges,
            c => c.Type == IntegrationHistoricalChangeType.IntegrationRemoved);
    }

    [Fact]
    public async Task ConsumerChanged_IsDetectedAndIdentityHolds()
    {
        var repository = new InMemoryIntegrationQualitySnapshotRepository();
        var contract = Contract("PlacementUpdated", ("id", true));

        await Service(repository, contract).AnalyzeAsync(Request("Dev", EventHub()));

        var changed = EventHub();
        changed.LogicalConsumerService = "ReportingService";

        var second = await Service(repository, contract).AnalyzeAsync(Request("Dev", changed));

        // Consumer is excluded from the baseline key, so this must appear as a change rather
        // than as a removal plus an addition.
        Assert.Contains(second.HistoricalChanges,
            c => c.Type == IntegrationHistoricalChangeType.ConsumerChanged);
        Assert.DoesNotContain(second.HistoricalChanges,
            c => c.Type == IntegrationHistoricalChangeType.IntegrationRemoved);
    }

    [Fact]
    public async Task RenamedIntegration_IsNotTreatedAsAddRemove()
    {
        var repository = new InMemoryIntegrationQualitySnapshotRepository();
        var contract = Contract("PlacementUpdated", ("id", true));

        await Service(repository, contract).AnalyzeAsync(Request("Dev", EventHub(name: "Placement Events")));
        var second = await Service(repository, contract)
            .AnalyzeAsync(Request("Dev", EventHub(name: "Placement Events (renamed)")));

        Assert.DoesNotContain(second.HistoricalChanges,
            c => c.Type is IntegrationHistoricalChangeType.IntegrationAdded
                        or IntegrationHistoricalChangeType.IntegrationRemoved);
    }

    // ── Environment isolation ────────────────────────────────────────────────

    [Fact]
    public async Task DevBaseline_IsNotUsedByQa()
    {
        var repository = new InMemoryIntegrationQualitySnapshotRepository();
        var contract = Contract("PlacementUpdated", ("id", true));

        await Service(repository, contract).AnalyzeAsync(Request("Dev", EventHub()));
        var qa = await Service(repository, contract).AnalyzeAsync(Request("QA", EventHub()));

        Assert.False(qa.BaselineAvailable);
        Assert.Null(qa.PreviousSnapshotId);
        Assert.Equal(ContractDriftState.BaselineUnavailable,
            qa.Statuses.Single(s => s.IntegrationId == "m1").DriftState);
    }

    [Fact]
    public async Task QaBaseline_IsNotUsedByProd()
    {
        var repository = new InMemoryIntegrationQualitySnapshotRepository();
        var contract = Contract("PlacementUpdated", ("id", true));

        await Service(repository, contract).AnalyzeAsync(Request("QA", EventHub()));
        var prod = await Service(repository, contract).AnalyzeAsync(Request("Prod", EventHub()));

        Assert.False(prod.BaselineAvailable);
    }

    [Fact]
    public async Task EachEnvironmentKeepsItsOwnChain()
    {
        var repository = new InMemoryIntegrationQualitySnapshotRepository();
        var contract = Contract("PlacementUpdated", ("id", true));

        var dev1 = await Service(repository, contract).AnalyzeAsync(Request("Dev", EventHub()));
        await Service(repository, contract).AnalyzeAsync(Request("QA", EventHub()));
        var dev2 = await Service(repository, contract).AnalyzeAsync(Request("Dev", EventHub()));

        Assert.Equal(dev1.CurrentSnapshotId, dev2.PreviousSnapshotId);
    }

    // ── Persistence behaviour ────────────────────────────────────────────────

    [Fact]
    public async Task SaveFailure_PreservesCompletedReview()
    {
        var report = await Service(new ThrowingRepository(), Contract("PlacementUpdated", ("id", true)))
            .AnalyzeAsync(Request("Dev", EventHub()));

        // The review result survives; the failure is reported rather than implied away.
        Assert.NotEmpty(report.Statuses);
        Assert.Equal(SnapshotPersistenceState.Failed, report.SnapshotPersistenceState);
        Assert.Null(report.CurrentSnapshotId);
        Assert.Contains(report.Limitations, l => l.Contains("historical snapshot", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SaveFailure_KeepsComputedDrift()
    {
        var repository = new InMemoryIntegrationQualitySnapshotRepository();
        var contract = Contract("PlacementUpdated", ("id", true));

        await Service(repository, contract).AnalyzeAsync(Request("Dev", EventHub()));

        // Second run: history readable, save fails.
        var failing = new FailingSaveRepository(repository);
        var second = await Service(failing, contract).AnalyzeAsync(Request("Dev", EventHub()));

        Assert.Equal(SnapshotPersistenceState.Failed, second.SnapshotPersistenceState);
        Assert.True(second.BaselineAvailable);
        Assert.Equal(ContractDriftState.NoChange,
            second.Statuses.Single(s => s.IntegrationId == "m1").DriftState);
    }

    [Fact]
    public async Task ReviewWithNoIntegrations_DoesNotCreateBaseline()
    {
        var repository = new InMemoryIntegrationQualitySnapshotRepository();
        var report = await Service(repository).AnalyzeAsync(Request("Dev"));

        Assert.Equal(SnapshotPersistenceState.SkippedIncompleteReview, report.SnapshotPersistenceState);
        Assert.Null(await repository.GetLatestAsync("Dev"));
    }

    [Fact]
    public async Task IncompleteIntegration_ProducesPartialSnapshotNotUsedAsBaseline()
    {
        var repository = new InMemoryIntegrationQualitySnapshotRepository();

        var incomplete = new IntegrationConfigDto
        {
            Id = "x1", Name = "Incomplete", Type = IntegrationType.EventHub, Enabled = true
            // no Endpoint, no Resource -> missing required fields
        };

        var first = await Service(repository, Contract("PlacementUpdated", ("id", true)))
            .AnalyzeAsync(Request("Dev", incomplete));

        Assert.Equal(SnapshotPersistenceState.Saved, first.SnapshotPersistenceState);

        // Recorded as partial, so it is not offered as a complete baseline to the next run.
        Assert.Null(await repository.GetLatestAsync("Dev"));

        var second = await Service(repository, Contract("PlacementUpdated", ("id", true)))
            .AnalyzeAsync(Request("Dev", incomplete));

        Assert.False(second.BaselineAvailable);
    }

    [Fact]
    public async Task BaselineKeyIsRecordedOnStatus()
    {
        var repository = new InMemoryIntegrationQualitySnapshotRepository();
        var report = await Service(repository, Contract("PlacementUpdated", ("id", true)))
            .AnalyzeAsync(Request("Dev", EventHub()));

        var status = report.Statuses.Single(s => s.IntegrationId == "m1");
        Assert.False(string.IsNullOrWhiteSpace(status.BaselineKey));
        Assert.Equal(IntegrationBaselineIdentity.Compute("Dev", EventHub()), status.BaselineKey);
    }

    private static IntegrationConfigDto Rest(string id) => new()
    {
        Id = id, Name = "Orders", Type = IntegrationType.REST, Enabled = true,
        Endpoint = "https://api.example.test/orders", Resource = "orders"
    };

    // ── Fakes ────────────────────────────────────────────────────────────────

    private sealed class ThrowingRepository : IIntegrationQualitySnapshotRepository
    {
        public Task SaveAsync(IntegrationQualitySnapshot snapshot, CancellationToken ct = default) =>
            throw new InvalidOperationException("database unavailable");

        public Task<IntegrationQualitySnapshot?> GetLatestAsync(string environmentId, CancellationToken ct = default) =>
            Task.FromResult<IntegrationQualitySnapshot?>(null);

        public Task<IntegrationSnapshotEntry?> GetLatestForIntegrationAsync(
            string environmentId, string baselineKey, CancellationToken ct = default) =>
            Task.FromResult<IntegrationSnapshotEntry?>(null);

        public Task<IntegrationQualitySnapshot?> GetByIdAsync(Guid snapshotId, CancellationToken ct = default) =>
            Task.FromResult<IntegrationQualitySnapshot?>(null);
    }

    private sealed class FailingSaveRepository(IIntegrationQualitySnapshotRepository inner)
        : IIntegrationQualitySnapshotRepository
    {
        public Task SaveAsync(IntegrationQualitySnapshot snapshot, CancellationToken ct = default) =>
            throw new InvalidOperationException("database unavailable");

        public Task<IntegrationQualitySnapshot?> GetLatestAsync(string environmentId, CancellationToken ct = default) =>
            inner.GetLatestAsync(environmentId, ct);

        public Task<IntegrationSnapshotEntry?> GetLatestForIntegrationAsync(
            string environmentId, string baselineKey, CancellationToken ct = default) =>
            inner.GetLatestForIntegrationAsync(environmentId, baselineKey, ct);

        public Task<IntegrationQualitySnapshot?> GetByIdAsync(Guid snapshotId, CancellationToken ct = default) =>
            inner.GetByIdAsync(snapshotId, ct);
    }

    private sealed class FakeMessageSchemaDiscovery(NormalizedContract contract) : IMessageSchemaDiscoveryService
    {
        public Task<MessageSchemaExtractionResult> ExtractSchemaAsync(
            IntegrationConfigDto integration, CancellationToken ct = default) =>
            Task.FromResult(MessageSchemaExtractionResult.Success(
                integration.Id, contract.Name, contract,
                ContractSourceType.Assembly, "/fake.dll",
                integration.LogicalProducerService, integration.LogicalConsumerService));
    }

    private sealed class FakeGateway : IAuthenticatedReviewGateway
    {
        public AuthenticatedReviewCapabilities Resolve(AuthenticatedReviewIdentity identity) =>
            new() { Method = identity.Method, ContextStatus = AuthenticatedApiContextStatus.NotApplicable, PublicApi = true, Reason = "Public only" };

        public Task<AuthenticatedReviewExecutionOutcome> ExecuteRestAsync(
            AuthenticatedReviewIdentity identity, string httpMethod, string url, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AuthenticatedReviewExecutionOutcome { Status = AuthenticatedExecutionStatus.NoContext });

        public Task<AuthenticatedReviewExecutionOutcome> ExecuteGraphQlQueryAsync(
            AuthenticatedReviewIdentity identity, string endpointUrl, string query, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AuthenticatedReviewExecutionOutcome { Status = AuthenticatedExecutionStatus.NoContext });

        public Task<AuthenticatedGraphQlSchemaOutcome> FetchGraphQlSchemaAsync(
            AuthenticatedReviewIdentity identity, string endpointUrl, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AuthenticatedGraphQlSchemaOutcome { Status = AuthenticatedExecutionStatus.NoContext });
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
                RequestMessage = request
            });
    }
}
