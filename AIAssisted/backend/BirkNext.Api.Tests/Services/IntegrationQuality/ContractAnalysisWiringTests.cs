using System.Net;
using System.Text.Json;
using BirkNext.Api.Services;
using BirkNext.Api.Services.ContractAnalysis;
using BirkNext.Api.Services.IntegrationQuality;
using BirkNext.Api.Services.LocalHttpsProxy;
using BirkNext.LocalHttpsProxy;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BirkNext.Api.Tests.Services.IntegrationQuality;

/// <summary>
/// Phase 3 Checkpoint 4: compatibility and drift reach the authoritative Integration Quality
/// Review report, findings reach the normal findings collection, and the two states stay
/// independent. One failing comparison must not discard the rest of the review.
/// </summary>
public class ContractAnalysisWiringTests
{
    private static NormalizedContract Contract(string name, params (string Name, string Type, bool Required)[] props) =>
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
                        .Select(p => new NormalizedProperty { Name = p.Name, Type = p.Type, Required = p.Required })
                        .ToList(),
                    Required = props.Where(p => p.Required).Select(p => p.Name).ToList()
                }
            ]
        };

    private static IntegrationQualityReviewService Service(
        IContractDiscoveryService? discovery = null,
        IMessageSchemaDiscoveryService? messaging = null,
        IIntegrationQualitySnapshotRepository? snapshots = null) =>
        new(new HttpClient(new StubHandler()) { BaseAddress = new Uri("https://unused.example.test/") },
            NullLogger<IntegrationQualityReviewService>.Instance,
            new FakeGateway(),
            new IntegrationRelationshipPopulationService(),
            discovery,
            messaging,
            null,
            snapshots);

    /// <summary>
    /// Seeds history the way a previous review would have: one saved snapshot whose entry is
    /// keyed by the same deterministic baseline key the service will compute.
    /// </summary>
    private static IIntegrationQualitySnapshotRepository SeededHistory(
        IntegrationConfigDto integration,
        NormalizedContract baselineContract,
        DateTimeOffset? capturedAt = null)
    {
        var repository = new InMemoryIntegrationQualitySnapshotRepository();

        repository.SaveAsync(new IntegrationQualitySnapshot
        {
            EnvironmentId = "Dev",
            CapturedAt = capturedAt ?? DateTimeOffset.UtcNow.AddDays(-1),
            Integrations =
            [
                new IntegrationSnapshotEntry
                {
                    BaselineKey = IntegrationBaselineIdentity.Compute("Dev", integration),
                    IntegrationId = integration.Id,
                    DisplayName = integration.Name,
                    IntegrationType = integration.Type,
                    NormalizedContract = baselineContract
                }
            ]
        }).GetAwaiter().GetResult();

        return repository;
    }

    private static IntegrationQualityRequest Request(params IntegrationConfigDto[] integrations) =>
        new() { EnvironmentName = "Dev", Integrations = integrations.ToList() };

    private static IntegrationConfigDto Rest(string id = "i1") => new()
    {
        Id = id, Name = "Orders", Type = IntegrationType.REST, Enabled = true,
        Endpoint = "https://api.example.test", Resource = "orders"
    };

    private static IntegrationConfigDto EventHub(string id = "m1") => new()
    {
        Id = id, Name = "Placement Events", Type = IntegrationType.EventHub, Enabled = true,
        Endpoint = "ns", Resource = "hub", ContractName = "PlacementUpdated",
        LogicalProducerService = "PlacementService", LogicalConsumerService = "NotificationService"
    };

    // ── Compatibility reaches the report ─────────────────────────────────────

    [Fact]
    public async Task CompatibleComparison_PopulatesReportState()
    {
        var comparer = new ContractComparer();
        var result = comparer.Compare(
            Contract("Order", ("id", "string", true)),
            Contract("Order", ("id", "string", true)),
            "OrderService", "BillingService", "Order", null, null);

        var report = await Service(new FakeDiscovery(result)).AnalyzeAsync(Request(Rest()));
        var status = report.Statuses.Single(s => s.IntegrationId == "i1");

        Assert.Equal(ContractCompatibilityStatus.Compatible, status.CompatibilityState);
        Assert.Equal(0, status.CompatibilityDifferenceCount);
        Assert.Equal(0, status.CompatibilityBreakingCount);
        Assert.NotNull(status.CompatibilityComparedAt);
    }

    [Fact]
    public async Task ProducerOnly_ReachesReportAsNotComparable()
    {
        var comparer = new ContractComparer();
        var result = comparer.Compare(
            Contract("Order", ("id", "string", true)),
            new NormalizedContract { Name = "Order" },
            "P", "C", "Order", null, null);

        var report = await Service(new FakeDiscovery(result)).AnalyzeAsync(Request(Rest()));
        var status = report.Statuses.Single(s => s.IntegrationId == "i1");

        Assert.Equal(ContractCompatibilityStatus.NotComparable, status.CompatibilityState);
        Assert.Contains("consumer expectation unavailable", status.CompatibilityReason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConsumerOnly_ReachesReportAsNotComparable()
    {
        var comparer = new ContractComparer();
        var result = comparer.Compare(
            new NormalizedContract { Name = "Order" },
            Contract("Order", ("id", "string", true)),
            "P", "C", "Order", null, null);

        var report = await Service(new FakeDiscovery(result)).AnalyzeAsync(Request(Rest()));

        Assert.Equal(ContractCompatibilityStatus.NotComparable,
            report.Statuses.Single(s => s.IntegrationId == "i1").CompatibilityState);
    }

    [Fact]
    public async Task BreakingComparison_PopulatesDifferenceCounts()
    {
        var comparer = new ContractComparer();
        var result = comparer.Compare(
            Contract("Order", ("id", "string", true)),
            Contract("Order", ("id", "string", true), ("customerId", "string", true)),
            "P", "C", "Order", null, null);

        var report = await Service(new FakeDiscovery(result)).AnalyzeAsync(Request(Rest()));
        var status = report.Statuses.Single(s => s.IntegrationId == "i1");

        Assert.Equal(ContractCompatibilityStatus.Breaking, status.CompatibilityState);
        Assert.True(status.CompatibilityBreakingCount > 0);
        Assert.NotEmpty(status.CompatibilityDifferences);
    }

    // ── Findings reach the normal collection ─────────────────────────────────

    [Fact]
    public async Task BreakingCompatibility_ProducesReportFinding()
    {
        var comparer = new ContractComparer();
        var result = comparer.Compare(
            Contract("Order", ("id", "string", true)),
            Contract("Order", ("id", "string", true), ("customerId", "string", true)),
            "P", "C", "Order", null, null);

        var report = await Service(new FakeDiscovery(result)).AnalyzeAsync(Request(Rest()));

        var finding = report.Findings.SingleOrDefault(f => f.Id == "contract-compat-i1");
        Assert.NotNull(finding);
        Assert.Equal(IntegrationFindingSeverity.High, finding!.Severity);
    }

    [Fact]
    public async Task NotComparable_ProducesNoFinding()
    {
        var comparer = new ContractComparer();
        var result = comparer.Compare(
            Contract("Order", ("id", "string", true)),
            new NormalizedContract { Name = "Order" },
            "P", "C", "Order", null, null);

        var report = await Service(new FakeDiscovery(result)).AnalyzeAsync(Request(Rest()));

        Assert.DoesNotContain(report.Findings, f => f.Id.StartsWith("contract-compat-"));
    }

    [Fact]
    public async Task CompatibleResult_ProducesNoFinding()
    {
        var comparer = new ContractComparer();
        var result = comparer.Compare(
            Contract("Order", ("id", "string", true)),
            Contract("Order", ("id", "string", true)),
            "P", "C", "Order", null, null);

        var report = await Service(new FakeDiscovery(result)).AnalyzeAsync(Request(Rest()));

        Assert.DoesNotContain(report.Findings, f => f.Id.StartsWith("contract-compat-"));
    }

    // ── Drift ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task NoBaselineProvider_ReportsBaselineUnavailable()
    {
        var report = await Service().AnalyzeAsync(Request(Rest()));

        Assert.Equal(ContractDriftState.BaselineUnavailable,
            report.Statuses.Single(s => s.IntegrationId == "i1").DriftState);
    }

    [Fact]
    public async Task NoBaseline_ProducesNoDriftFinding()
    {
        var report = await Service().AnalyzeAsync(Request(Rest()));

        Assert.DoesNotContain(report.Findings, f => f.Id.StartsWith("contract-drift-"));
    }

    [Fact]
    public async Task SuppliedBaseline_WithMessagingContract_PopulatesDrift()
    {
        var current = Contract("PlacementUpdated", ("placementId", "string", true));
        var baseline = Contract("PlacementUpdated", ("placementId", "string", true), ("legacy", "string", true));
        var capturedAt = new DateTimeOffset(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);

        var report = await Service(
                messaging: new FakeMessageSchemaDiscovery(current),
                snapshots: SeededHistory(EventHub(), baseline, capturedAt))
            .AnalyzeAsync(Request(EventHub()));

        var status = report.Statuses.Single(s => s.IntegrationId == "m1");

        Assert.Equal(ContractDriftState.BreakingChange, status.DriftState);
        Assert.True(status.DriftBreakingCount > 0);
        Assert.Equal(capturedAt.UtcDateTime, status.PreviousBaselineTimestamp);
        Assert.NotNull(status.CurrentContractFingerprint);
        Assert.NotNull(status.PreviousContractFingerprint);
    }

    [Fact]
    public async Task BreakingDrift_ProducesReportFinding()
    {
        var current = Contract("PlacementUpdated", ("placementId", "string", true));
        var baseline = Contract("PlacementUpdated", ("placementId", "string", true), ("legacy", "string", true));

        var report = await Service(
                messaging: new FakeMessageSchemaDiscovery(current),
                snapshots: SeededHistory(EventHub(), baseline))
            .AnalyzeAsync(Request(EventHub()));

        var finding = report.Findings.SingleOrDefault(f => f.Id == "contract-drift-m1");
        Assert.NotNull(finding);
        Assert.Equal(IntegrationFindingSeverity.High, finding!.Severity);
    }

    [Fact]
    public async Task IdenticalBaseline_ReportsNoChangeWithoutFinding()
    {
        var current = Contract("PlacementUpdated", ("placementId", "string", true));
        var baseline = Contract("PlacementUpdated", ("placementId", "string", true));

        var report = await Service(
                messaging: new FakeMessageSchemaDiscovery(current),
                snapshots: SeededHistory(EventHub(), baseline))
            .AnalyzeAsync(Request(EventHub()));

        Assert.Equal(ContractDriftState.NoChange,
            report.Statuses.Single(s => s.IntegrationId == "m1").DriftState);
        Assert.DoesNotContain(report.Findings, f => f.Id.StartsWith("contract-drift-"));
    }

    // ── Independence ─────────────────────────────────────────────────────────

    [Fact]
    public async Task CompatibilityAndDrift_AreIndependent()
    {
        var comparer = new ContractComparer();
        var compatible = comparer.Compare(
            Contract("PlacementUpdated", ("placementId", "string", true)),
            Contract("PlacementUpdated", ("placementId", "string", true)),
            "P", "C", "PlacementUpdated", null, null);

        var current = Contract("PlacementUpdated", ("placementId", "string", true));
        var baseline = Contract("PlacementUpdated", ("placementId", "string", true), ("legacy", "string", true));

        var report = await Service(
                new FakeDiscovery(compatible),
                new FakeMessageSchemaDiscovery(current),
                SeededHistory(EventHub(), baseline))
            .AnalyzeAsync(Request(EventHub()));

        var status = report.Statuses.Single(s => s.IntegrationId == "m1");

        Assert.Equal(ContractCompatibilityStatus.Compatible, status.CompatibilityState);
        Assert.Equal(ContractDriftState.BreakingChange, status.DriftState);
    }

    // ── Isolation and resilience ─────────────────────────────────────────────

    [Fact]
    public async Task ComparisonThrows_DoesNotCrashReview()
    {
        var report = await Service(new ThrowingDiscovery()).AnalyzeAsync(Request(Rest("i1"), Rest("i2")));

        Assert.Equal(2, report.Statuses.Count);
        Assert.All(report.Statuses, s => Assert.Equal(ContractCompatibilityStatus.Error, s.CompatibilityState));
    }

    [Fact]
    public async Task OneFailingIntegration_DoesNotAffectOthers()
    {
        var comparer = new ContractComparer();
        var breaking = comparer.Compare(
            Contract("Order", ("id", "string", true)),
            Contract("Order", ("id", "string", true), ("customerId", "string", true)),
            "P", "C", "Order", null, null);

        var report = await Service(new PerIntegrationDiscovery("i2", breaking))
            .AnalyzeAsync(Request(Rest("i1"), Rest("i2")));

        Assert.Equal(ContractCompatibilityStatus.Error,
            report.Statuses.Single(s => s.IntegrationId == "i1").CompatibilityState);
        Assert.Equal(ContractCompatibilityStatus.Breaking,
            report.Statuses.Single(s => s.IntegrationId == "i2").CompatibilityState);
    }

    [Fact]
    public async Task ProducerAndConsumerIdentity_ReachReport()
    {
        var report = await Service().AnalyzeAsync(Request(EventHub()));
        var status = report.Statuses.Single(s => s.IntegrationId == "m1");

        Assert.Equal("PlacementService", status.ProducerService);
        Assert.Equal("NotificationService", status.ConsumerService);
    }

    // ── Legacy and serialization ─────────────────────────────────────────────

    [Fact]
    public void LegacyStatusWithoutContractFields_DoesNotDefaultToCompatible()
    {
        const string legacy = """
            {"integrationId":"i1","name":"Orders","type":0,"enabled":true,
             "hasRequiredFields":true,"score":100,"missingFields":[]}
            """;

        var status = JsonSerializer.Deserialize<IntegrationStatus>(legacy);

        Assert.NotNull(status);
        Assert.Null(status!.CompatibilityState);
        Assert.Null(status.DriftState);
        Assert.Null(status.CompatibilityDifferenceCount);
        Assert.Null(status.PreviousBaselineTimestamp);
    }

    [Fact]
    public void ContractStates_SurviveSerializationRoundTrip()
    {
        var original = new IntegrationStatus
        {
            IntegrationId = "i1", Name = "Orders", Type = IntegrationType.REST, Enabled = true,
            CompatibilityState = ContractCompatibilityStatus.NotComparable,
            DriftState = ContractDriftState.BaselineUnavailable,
            CompatibilityDifferenceCount = 3,
            CompatibilityBreakingCount = 1,
            PreviousBaselineTimestamp = new DateTime(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc)
        };

        var round = JsonSerializer.Deserialize<IntegrationStatus>(JsonSerializer.Serialize(original));

        Assert.Equal(ContractCompatibilityStatus.NotComparable, round!.CompatibilityState);
        Assert.Equal(ContractDriftState.BaselineUnavailable, round.DriftState);
        Assert.Equal(3, round.CompatibilityDifferenceCount);
        Assert.Equal(1, round.CompatibilityBreakingCount);
        Assert.Equal(original.PreviousBaselineTimestamp, round.PreviousBaselineTimestamp);
    }

    [Fact]
    public void NotComparableEnumValue_IsSix()
    {
        // Additive: existing persisted numeric values keep their meaning.
        Assert.Equal(6, (int)ContractCompatibilityStatus.NotComparable);
        Assert.Equal(0, (int)ContractCompatibilityStatus.Compatible);
        Assert.Equal(2, (int)ContractCompatibilityStatus.Breaking);
    }

    // ── Fakes ────────────────────────────────────────────────────────────────

    private sealed class FakeDiscovery(ContractCompatibilityResult result) : IContractDiscoveryService
    {
        public Task<ContractCompatibilityResult> AnalyzeAsync(
            IntegrationConfigDto integration, CancellationToken ct = default) => Task.FromResult(result);
    }

    private sealed class PerIntegrationDiscovery(string id, ContractCompatibilityResult result)
        : IContractDiscoveryService
    {
        public Task<ContractCompatibilityResult> AnalyzeAsync(
            IntegrationConfigDto integration, CancellationToken ct = default) =>
            integration.Id == id
                ? Task.FromResult(result)
                : throw new InvalidOperationException("contract source unreachable");
    }

    private sealed class ThrowingDiscovery : IContractDiscoveryService
    {
        public Task<ContractCompatibilityResult> AnalyzeAsync(
            IntegrationConfigDto integration, CancellationToken ct = default) =>
            throw new InvalidOperationException("contract source unreachable");
    }

    private sealed class FakeMessageSchemaDiscovery(NormalizedContract contract)
        : IMessageSchemaDiscoveryService
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
