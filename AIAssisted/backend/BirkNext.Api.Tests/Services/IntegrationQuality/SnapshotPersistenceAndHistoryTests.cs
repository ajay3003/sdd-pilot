using System.Text.Json;
using BirkNext.Api.Services.ContractAnalysis;
using BirkNext.Api.Services.IntegrationQuality;
using Xunit;

namespace BirkNext.Api.Tests.Services.IntegrationQuality;

/// <summary>
/// Phase 3 Checkpoint 5: snapshot persistence semantics and historical change detection.
/// </summary>
public class SnapshotPersistenceAndHistoryTests
{
    private readonly IntegrationHistoryComparer _history = new();

    private static IntegrationSnapshotEntry Entry(
        string baselineKey = "key-1",
        string name = "Orders",
        string? producer = "OrderService",
        string? consumer = "BillingService",
        RelationshipSource relationshipSource = RelationshipSource.Configured,
        RuntimeEvidenceState evidence = RuntimeEvidenceState.Observed,
        bool? authRequired = true,
        bool? authCapability = true,
        NormalizedContract? contract = null) =>
        new()
        {
            BaselineKey = baselineKey,
            DisplayName = name,
            IntegrationType = IntegrationType.EventHub,
            Producer = producer,
            Consumer = consumer,
            RelationshipSource = relationshipSource,
            RuntimeEvidenceState = evidence,
            AuthenticationRequired = authRequired,
            AuthenticatedCapabilityAvailable = authCapability,
            NormalizedContract = contract
        };

    private static IntegrationQualitySnapshot Snapshot(
        string environmentId = "Dev",
        DateTimeOffset? capturedAt = null,
        params IntegrationSnapshotEntry[] entries) =>
        new()
        {
            EnvironmentId = environmentId,
            CapturedAt = capturedAt ?? DateTimeOffset.UtcNow,
            Integrations = entries.ToList()
        };

    // ── History detection ────────────────────────────────────────────────────

    [Fact]
    public void NoPreviousSnapshot_YieldsNoChanges() =>
        Assert.Empty(_history.Compare(null, [Entry()]));

    [Fact]
    public void IdenticalSets_YieldNoChanges() =>
        Assert.Empty(_history.Compare(Snapshot("Dev", null, Entry()), [Entry()]));

    [Fact]
    public void IntegrationAdded_IsDetected()
    {
        var changes = _history.Compare(
            Snapshot("Dev", null, Entry("key-1")),
            [Entry("key-1"), Entry("key-2", name: "Invoices")]);

        Assert.Contains(changes, c => c.Type == IntegrationHistoricalChangeType.IntegrationAdded
                                      && c.BaselineKey == "key-2");
    }

    [Fact]
    public void IntegrationRemoved_IsDetected()
    {
        var changes = _history.Compare(
            Snapshot("Dev", null, Entry("key-1"), Entry("key-2", name: "Invoices")),
            [Entry("key-1")]);

        Assert.Contains(changes, c => c.Type == IntegrationHistoricalChangeType.IntegrationRemoved
                                      && c.BaselineKey == "key-2");
    }

    [Fact]
    public void ProducerChanged_PreservesOldAndNew()
    {
        var changes = _history.Compare(
            Snapshot("Dev", null, Entry(producer: "OrderService")),
            [Entry(producer: "OrderServiceV2")]);

        var change = changes.Single(c => c.Type == IntegrationHistoricalChangeType.ProducerChanged);
        Assert.Equal("OrderService", change.OldValue);
        Assert.Equal("OrderServiceV2", change.NewValue);
    }

    [Fact]
    public void ConsumerChanged_PreservesOldAndNew()
    {
        var changes = _history.Compare(
            Snapshot("Dev", null, Entry(consumer: "BillingService")),
            [Entry(consumer: "InvoicingService")]);

        var change = changes.Single(c => c.Type == IntegrationHistoricalChangeType.ConsumerChanged);
        Assert.Equal("BillingService", change.OldValue);
        Assert.Equal("InvoicingService", change.NewValue);
    }

    [Fact]
    public void RelationshipSourceChanged_IsDetected()
    {
        var changes = _history.Compare(
            Snapshot("Dev", null, Entry(relationshipSource: RelationshipSource.Unknown)),
            [Entry(relationshipSource: RelationshipSource.Configured)]);

        Assert.Contains(changes, c => c.Type == IntegrationHistoricalChangeType.RelationshipSourceChanged);
    }

    [Fact]
    public void AuthenticationRequirementChanged_IsDetected()
    {
        var changes = _history.Compare(
            Snapshot("Dev", null, Entry(authRequired: false)),
            [Entry(authRequired: true)]);

        Assert.Contains(changes, c => c.Type == IntegrationHistoricalChangeType.AuthenticationRequiredChanged);
    }

    [Fact]
    public void AuthenticatedCapabilityChanged_IsReportedSeparatelyFromRequirement()
    {
        var changes = _history.Compare(
            Snapshot("Dev", null, Entry(authRequired: true, authCapability: true)),
            [Entry(authRequired: true, authCapability: false)]);

        Assert.Contains(changes, c => c.Type == IntegrationHistoricalChangeType.AuthenticatedCapabilityChanged);
        Assert.DoesNotContain(changes, c => c.Type == IntegrationHistoricalChangeType.AuthenticationRequiredChanged);
    }

    [Fact]
    public void RuntimeEvidenceDisappearing_IsReportedAsEvidenceChangeNotFailure()
    {
        var changes = _history.Compare(
            Snapshot("Dev", null, Entry(evidence: RuntimeEvidenceState.Observed)),
            [Entry(evidence: RuntimeEvidenceState.NoEvidence)]);

        var change = changes.Single(c => c.Type == IntegrationHistoricalChangeType.RuntimeEvidenceStateChanged);
        Assert.Contains("observed in the previous review", change.Description, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fail", change.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RuntimeEvidenceAppearing_IsDetected()
    {
        var changes = _history.Compare(
            Snapshot("Dev", null, Entry(evidence: RuntimeEvidenceState.NoEvidence)),
            [Entry(evidence: RuntimeEvidenceState.Observed)]);

        Assert.Contains(changes, c => c.Type == IntegrationHistoricalChangeType.RuntimeEvidenceStateChanged);
    }

    [Fact]
    public void HistoricalChanges_CarryNoSeverity()
    {
        // Historical changes are observations. Severity would be policy, and policy is not
        // invented here.
        var change = _history.Compare(
            Snapshot("Dev", null, Entry(consumer: "A")), [Entry(consumer: "B")]).First();

        Assert.False(change.GetType().GetProperties().Any(p =>
            p.Name.Contains("Severity", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void ChangeOrdering_IsDeterministic()
    {
        var previous = Snapshot("Dev", null, Entry("k1", "Alpha", consumer: "A"), Entry("k2", "Beta", consumer: "B"));
        var current = new[] { Entry("k1", "Alpha", consumer: "X"), Entry("k2", "Beta", consumer: "Y") };

        var first = _history.Compare(previous, current).Select(c => c.IntegrationName + c.Type).ToList();
        var second = _history.Compare(previous, current).Select(c => c.IntegrationName + c.Type).ToList();

        Assert.Equal(first, second);
    }

    // ── Repository semantics ─────────────────────────────────────────────────

    [Fact]
    public async Task Repository_RejectsDuplicateSnapshotId()
    {
        var repository = new InMemoryIntegrationQualitySnapshotRepository();
        var snapshot = Snapshot("Dev", null, Entry());

        await repository.SaveAsync(snapshot);

        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.SaveAsync(snapshot));
    }

    [Fact]
    public async Task Repository_ReturnsNewestSnapshot()
    {
        var repository = new InMemoryIntegrationQualitySnapshotRepository();
        var older = Snapshot("Dev", DateTimeOffset.UtcNow.AddDays(-2), Entry());
        var newer = Snapshot("Dev", DateTimeOffset.UtcNow.AddDays(-1), Entry());

        await repository.SaveAsync(older);
        await repository.SaveAsync(newer);

        var latest = await repository.GetLatestAsync("Dev");
        Assert.Equal(newer.SnapshotId, latest!.SnapshotId);
    }

    [Fact]
    public async Task Repository_DoesNotCrossEnvironments()
    {
        var repository = new InMemoryIntegrationQualitySnapshotRepository();
        await repository.SaveAsync(Snapshot("Dev", null, Entry()));

        Assert.Null(await repository.GetLatestAsync("QA"));
        Assert.Null(await repository.GetLatestForIntegrationAsync("QA", "key-1"));
    }

    [Fact]
    public async Task Repository_ResolvesEntryByBaselineKeyOnly()
    {
        var repository = new InMemoryIntegrationQualitySnapshotRepository();
        await repository.SaveAsync(Snapshot("Dev", null, Entry("key-1", name: "Original Name")));

        var entry = await repository.GetLatestForIntegrationAsync("Dev", "key-1");
        Assert.NotNull(entry);

        // A different display name is irrelevant; a different key finds nothing.
        Assert.Null(await repository.GetLatestForIntegrationAsync("Dev", "key-unknown"));
    }

    [Fact]
    public async Task Repository_FallsBackToEarlierSnapshotWhenIntegrationAbsentFromLatest()
    {
        var repository = new InMemoryIntegrationQualitySnapshotRepository();

        await repository.SaveAsync(Snapshot("Dev", DateTimeOffset.UtcNow.AddDays(-2), Entry("key-1")));
        await repository.SaveAsync(Snapshot("Dev", DateTimeOffset.UtcNow.AddDays(-1), Entry("key-2")));

        // key-1 is absent from the newest snapshot but present earlier.
        Assert.NotNull(await repository.GetLatestForIntegrationAsync("Dev", "key-1"));
    }

    [Fact]
    public async Task Repository_IgnoresPartialSnapshotsAsBaseline()
    {
        var repository = new InMemoryIntegrationQualitySnapshotRepository();

        await repository.SaveAsync(new IntegrationQualitySnapshot
        {
            EnvironmentId = "Dev",
            Completeness = SnapshotCompleteness.Partial,
            Integrations = [Entry()]
        });

        Assert.Null(await repository.GetLatestAsync("Dev"));
    }

    // ── Serialization ────────────────────────────────────────────────────────

    [Fact]
    public void Snapshot_RoundTripsThroughJson()
    {
        var contract = new NormalizedContract
        {
            Name = "PlacementUpdated",
            Schemas =
            [
                new NormalizedSchema
                {
                    Name = "PlacementUpdated", Type = "object",
                    Properties = [new NormalizedProperty { Name = "id", Type = "string", Required = true }],
                    Required = ["id"]
                }
            ]
        };

        var original = new IntegrationQualitySnapshot
        {
            EnvironmentId = "Dev",
            PreviousSnapshotId = Guid.NewGuid(),
            Integrations = [Entry(contract: contract)]
        };

        var round = JsonSerializer.Deserialize<IntegrationQualitySnapshot>(JsonSerializer.Serialize(original));

        Assert.Equal(original.SnapshotId, round!.SnapshotId);
        Assert.Equal(original.EnvironmentId, round.EnvironmentId);
        Assert.Equal(original.PreviousSnapshotId, round.PreviousSnapshotId);
        Assert.Equal(1, round.SnapshotVersion);
        Assert.Equal(1, round.BaselineIdentityVersion);
        Assert.Equal(SnapshotCompleteness.Complete, round.Completeness);

        var entry = round.Integrations.Single();
        Assert.Equal("key-1", entry.BaselineKey);
        Assert.Equal("PlacementUpdated", entry.NormalizedContract!.Name);
        Assert.Single(entry.NormalizedContract.Schemas);
        Assert.Equal("id", entry.NormalizedContract.Schemas[0].Properties[0].Name);
        Assert.True(entry.NormalizedContract.Schemas[0].Properties[0].Required);
    }

    [Fact]
    public void Snapshot_VersionsArePinned()
    {
        Assert.Equal(1, IntegrationQualitySnapshot.CurrentSnapshotVersion);
        Assert.Equal(1, new IntegrationQualitySnapshot().BaselineIdentityVersion);
    }

    [Fact]
    public void Snapshot_PerformanceIsNullNotFabricated()
    {
        // Checkpoint 6 owns performance. A zero would be indistinguishable from a measurement.
        Assert.Null(Entry().Performance);
    }

    [Fact]
    public void SnapshotEntry_DoesNotCarryCredentials()
    {
        var integration = new IntegrationConfigDto
        {
            Id = "i1", Name = "Orders", Type = IntegrationType.REST,
            Endpoint = "https://user:secret123@api.example.test/orders?token=abc", Resource = "orders"
        };

        var identity = IntegrationBaselineIdentity.Describe("Dev", integration);

        var entry = new IntegrationSnapshotEntry
        {
            BaselineKey = identity.Key,
            CanonicalEndpoint = identity.CanonicalEndpoint,
            CanonicalResource = identity.CanonicalResource
        };

        var json = JsonSerializer.Serialize(entry);

        Assert.DoesNotContain("secret123", json);
        Assert.DoesNotContain("token=abc", json);
    }
}
