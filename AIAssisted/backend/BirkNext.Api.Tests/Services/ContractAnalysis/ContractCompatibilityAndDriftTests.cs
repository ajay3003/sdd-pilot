using BirkNext.Api.Services.ContractAnalysis;
using BirkNext.Api.Services.IntegrationQuality;
using Xunit;

namespace BirkNext.Api.Tests.Services.ContractAnalysis;

/// <summary>
/// Phase 3 Checkpoint 4: contract compatibility and drift.
/// Compatibility is producer -> consumer. Drift is current -> previous baseline.
/// The two are independent and neither is derived from the other.
/// </summary>
public class ContractCompatibilityAndDriftTests
{
    private readonly ContractComparer _comparer = new();

    private static NormalizedContract Contract(string name, params NormalizedProperty[] props) =>
        new()
        {
            Name = name,
            Schemas =
            [
                new NormalizedSchema
                {
                    Name = name,
                    Type = "object",
                    Properties = props.ToList(),
                    Required = props.Where(p => p.Required).Select(p => p.Name).ToList()
                }
            ]
        };

    private static NormalizedProperty Prop(
        string name, string type = "string", bool required = false, bool nullable = false) =>
        new() { Name = name, Type = type, Required = required, Nullable = nullable };

    // ---------- Compatibility: must not overclaim ----------

    [Fact]
    public void EmptyBothSides_IsNotComparable_NotCompatible()
    {
        var result = _comparer.Compare(
            new NormalizedContract { Name = "X" }, new NormalizedContract { Name = "X" },
            "P", "C", "X", null, null);

        Assert.Equal(ContractCompatibilityStatus.NotComparable, result.Status);
        Assert.False(result.Compatible);
        Assert.Contains("neither", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProducerOnly_IsNotComparable()
    {
        var result = _comparer.Compare(
            Contract("Order", Prop("id", required: true)),
            new NormalizedContract { Name = "Order" },
            "P", "C", "Order", null, null);

        Assert.Equal(ContractCompatibilityStatus.NotComparable, result.Status);
        Assert.False(result.Compatible);
        Assert.Contains("consumer expectation unavailable", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ConsumerOnly_IsNotComparable()
    {
        var result = _comparer.Compare(
            new NormalizedContract { Name = "Order" },
            Contract("Order", Prop("id", required: true)),
            "P", "C", "Order", null, null);

        Assert.Equal(ContractCompatibilityStatus.NotComparable, result.Status);
        Assert.False(result.Compatible);
        Assert.Contains("producer contract unavailable", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NotComparable_IsDistinctFromCompatible()
    {
        var notComparable = _comparer.Compare(
            new NormalizedContract { Name = "X" }, new NormalizedContract { Name = "X" },
            "P", "C", "X", null, null);

        var compatible = _comparer.Compare(
            Contract("X", Prop("id", required: true)),
            Contract("X", Prop("id", required: true)),
            "P", "C", "X", null, null);

        Assert.NotEqual(compatible.Status, notComparable.Status);
        Assert.True(compatible.Compatible);
        Assert.False(notComparable.Compatible);
    }

    // ---------- Compatibility: directional semantics ----------

    [Fact]
    public void IdenticalContracts_AreCompatible()
    {
        var result = _comparer.Compare(
            Contract("Order", Prop("id", required: true), Prop("note")),
            Contract("Order", Prop("id", required: true), Prop("note")),
            "P", "C", "Order", null, null);

        Assert.Equal(ContractCompatibilityStatus.Compatible, result.Status);
        Assert.True(result.Compatible);
        Assert.Empty(result.Differences);
    }

    [Fact]
    public void ConsumerExpectsSchemaProducerDoesNotDefine_IsBreaking()
    {
        var producer = Contract("OrderPlaced", Prop("id", required: true));
        var consumer = new NormalizedContract
        {
            Name = "OrderPlaced",
            Schemas =
            [
                new NormalizedSchema
                {
                    Name = "OrderPlaced", Type = "object",
                    Properties = [Prop("id", required: true)], Required = ["id"]
                },
                new NormalizedSchema
                {
                    Name = "OrderCancelled", Type = "object",
                    Properties = [Prop("id", required: true)], Required = ["id"]
                }
            ]
        };

        var result = _comparer.Compare(producer, consumer, "P", "C", "OrderPlaced", null, null);

        Assert.Equal(ContractCompatibilityStatus.Breaking, result.Status);
        Assert.False(result.Compatible);
        Assert.Contains(result.Differences, d =>
            d.Path == "OrderCancelled" && d.Severity == ContractDifferenceSeverity.Breaking);
    }

    [Fact]
    public void ProducerDefinesExtraSchema_IsNotBreaking()
    {
        var producer = new NormalizedContract
        {
            Name = "Order",
            Schemas =
            [
                new NormalizedSchema
                {
                    Name = "Order", Type = "object",
                    Properties = [Prop("id", required: true)], Required = ["id"]
                },
                new NormalizedSchema
                {
                    Name = "Extra", Type = "object",
                    Properties = [Prop("x")], Required = []
                }
            ]
        };

        var result = _comparer.Compare(
            producer, Contract("Order", Prop("id", required: true)), "P", "C", "Order", null, null);

        Assert.NotEqual(ContractCompatibilityStatus.Breaking, result.Status);
        Assert.DoesNotContain(result.Differences, d => d.Severity == ContractDifferenceSeverity.Breaking);
    }

    [Fact]
    public void AdditiveOptionalField_IsNotBreaking()
    {
        var result = _comparer.Compare(
            Contract("Order", Prop("id", required: true), Prop("added")),
            Contract("Order", Prop("id", required: true)),
            "P", "C", "Order", null, null);

        Assert.DoesNotContain(result.Differences, d => d.Severity == ContractDifferenceSeverity.Breaking);
    }

    [Fact]
    public void ComparisonResultCarriesIdentityAndTimestamp()
    {
        var before = DateTime.UtcNow.AddSeconds(-1);
        var result = _comparer.Compare(
            Contract("Order", Prop("id", required: true)),
            Contract("Order", Prop("id", required: true)),
            "OrderService", "NotificationService", "Order", "assembly://p.dll", "assembly://c.dll");

        Assert.Equal("OrderService", result.Producer);
        Assert.Equal("NotificationService", result.Consumer);
        Assert.Equal("Order", result.Contract);
        Assert.True(result.ComparedAt >= before);
    }

    // ---------- Fingerprint ----------

    [Fact]
    public void Fingerprint_IsStableAcrossCalls()
    {
        var c = Contract("Order", Prop("id", required: true), Prop("note"));
        Assert.Equal(ContractComparer.Fingerprint(c), ContractComparer.Fingerprint(c));
    }

    [Fact]
    public void Fingerprint_IgnoresPropertyOrder()
    {
        var a = Contract("Order", Prop("id", required: true), Prop("note"));
        var b = Contract("Order", Prop("note"), Prop("id", required: true));

        Assert.Equal(ContractComparer.Fingerprint(a), ContractComparer.Fingerprint(b));
    }

    [Fact]
    public void Fingerprint_ChangesWhenTypeChanges()
    {
        var a = Contract("Order", Prop("id", "string", required: true));
        var b = Contract("Order", Prop("id", "integer", required: true));

        Assert.NotEqual(ContractComparer.Fingerprint(a), ContractComparer.Fingerprint(b));
    }

    [Fact]
    public void Fingerprint_IgnoresSourceLocationAndFetchTime()
    {
        var a = Contract("Order", Prop("id", required: true));
        a.Source = new ContractSource
        {
            Type = ContractSourceType.Assembly, Location = "/a.dll", FetchedAt = DateTime.UtcNow.AddDays(-5)
        };

        var b = Contract("Order", Prop("id", required: true));
        b.Source = new ContractSource
        {
            Type = ContractSourceType.SchemaFile, Location = "/b.json", FetchedAt = DateTime.UtcNow
        };

        Assert.Equal(ContractComparer.Fingerprint(a), ContractComparer.Fingerprint(b));
    }

    // ---------- Drift ----------

    [Fact]
    public void Drift_NoBaseline_IsBaselineUnavailable()
    {
        var result = _comparer.CompareForDrift(
            Contract("Order", Prop("id", required: true)), null, "Order");

        Assert.Equal(ContractDriftState.BaselineUnavailable, result.State);
        Assert.Empty(result.Differences);
    }

    [Fact]
    public void Drift_IdenticalBaseline_IsNoChange()
    {
        var current = Contract("Order", Prop("id", required: true), Prop("note"));
        var baseline = Contract("Order", Prop("id", required: true), Prop("note"));

        var result = _comparer.CompareForDrift(current, baseline, "Order");

        Assert.Equal(ContractDriftState.NoChange, result.State);
        Assert.Empty(result.Differences);
        Assert.Equal(result.BaselineFingerprint, result.CurrentFingerprint);
    }

    [Fact]
    public void Drift_ReorderedPropertiesOnly_IsNoChange()
    {
        var current = Contract("Order", Prop("note"), Prop("id", required: true));
        var baseline = Contract("Order", Prop("id", required: true), Prop("note"));

        Assert.Equal(ContractDriftState.NoChange, _comparer.CompareForDrift(current, baseline, "Order").State);
    }

    [Fact]
    public void Drift_AddedOptionalField_IsNonBreaking()
    {
        var current = Contract("Order", Prop("id", required: true), Prop("added"));
        var baseline = Contract("Order", Prop("id", required: true));

        var result = _comparer.CompareForDrift(current, baseline, "Order");

        Assert.Equal(ContractDriftState.NonBreakingChange, result.State);
    }

    [Fact]
    public void Drift_RemovedRequiredField_IsBreaking()
    {
        var current = Contract("Order", Prop("id", required: true));
        var baseline = Contract("Order", Prop("id", required: true), Prop("customerId", required: true));

        var result = _comparer.CompareForDrift(current, baseline, "Order");

        Assert.Equal(ContractDriftState.BreakingChange, result.State);
        Assert.Contains(result.Differences, d => d.Severity == ContractDifferenceSeverity.Breaking);
    }

    [Fact]
    public void Drift_TypeChange_IsBreaking()
    {
        var current = Contract("Order", Prop("id", "integer", required: true));
        var baseline = Contract("Order", Prop("id", "string", required: true));

        var result = _comparer.CompareForDrift(current, baseline, "Order");

        Assert.Equal(ContractDriftState.BreakingChange, result.State);
        Assert.Contains(result.Differences, d => d.Type == ContractDifferenceType.TypeMismatch);
    }

    [Fact]
    public void Drift_RemovedSchema_IsBreaking()
    {
        var baseline = new NormalizedContract
        {
            Name = "Events",
            Schemas =
            [
                new NormalizedSchema
                {
                    Name = "OrderPlaced", Type = "object",
                    Properties = [Prop("id", required: true)], Required = ["id"]
                },
                new NormalizedSchema
                {
                    Name = "OrderCancelled", Type = "object",
                    Properties = [Prop("id", required: true)], Required = ["id"]
                }
            ]
        };
        var current = Contract("OrderPlaced", Prop("id", required: true));
        current.Name = "Events";

        var result = _comparer.CompareForDrift(current, baseline, "Events");

        Assert.Equal(ContractDriftState.BreakingChange, result.State);
        Assert.Contains(result.Differences, d =>
            d.Path == "OrderCancelled" && d.Severity == ContractDifferenceSeverity.Breaking);
    }

    [Fact]
    public void Drift_NewSchema_IsInformationalNotBreaking()
    {
        var baseline = Contract("OrderPlaced", Prop("id", required: true));
        baseline.Name = "Events";

        var current = new NormalizedContract
        {
            Name = "Events",
            Schemas =
            [
                new NormalizedSchema
                {
                    Name = "OrderPlaced", Type = "object",
                    Properties = [Prop("id", required: true)], Required = ["id"]
                },
                new NormalizedSchema
                {
                    Name = "OrderShipped", Type = "object",
                    Properties = [Prop("id", required: true)], Required = ["id"]
                }
            ]
        };

        var result = _comparer.CompareForDrift(current, baseline, "Events");

        Assert.Equal(ContractDriftState.NonBreakingChange, result.State);
        Assert.Contains(result.Differences, d =>
            d.Path == "OrderShipped" && d.Severity == ContractDifferenceSeverity.Info);
    }

    [Fact]
    public void Drift_EmptyCurrent_IsNotComparable()
    {
        var result = _comparer.CompareForDrift(
            new NormalizedContract { Name = "Order" },
            Contract("Order", Prop("id", required: true)),
            "Order");

        Assert.Equal(ContractDriftState.NotComparable, result.State);
    }

    [Fact]
    public void Drift_EmptyBaseline_IsNotComparable()
    {
        var result = _comparer.CompareForDrift(
            Contract("Order", Prop("id", required: true)),
            new NormalizedContract { Name = "Order" },
            "Order");

        Assert.Equal(ContractDriftState.NotComparable, result.State);
    }

    [Fact]
    public void Drift_PreservesBaselineTimestamp()
    {
        var captured = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);
        var result = _comparer.CompareForDrift(
            Contract("Order", Prop("id", required: true)),
            Contract("Order", Prop("id", required: true)),
            "Order", captured);

        Assert.Equal(captured, result.BaselineCapturedAt);
    }

    // ---------- Independence of the two concepts ----------

    [Fact]
    public void CompatibleProducerConsumer_CanStillHaveDrift()
    {
        var producer = Contract("Order", Prop("id", required: true), Prop("note"));
        var consumer = Contract("Order", Prop("id", required: true), Prop("note"));
        var baseline = Contract("Order", Prop("id", required: true), Prop("note"), Prop("legacy", required: true));

        var compatibility = _comparer.Compare(producer, consumer, "P", "C", "Order", null, null);
        var drift = _comparer.CompareForDrift(producer, baseline, "Order");

        Assert.Equal(ContractCompatibilityStatus.Compatible, compatibility.Status);
        Assert.True(compatibility.Compatible);
        Assert.Equal(ContractDriftState.BreakingChange, drift.State);
    }

    [Fact]
    public void IncompatibleProducerConsumer_CanHaveNoDrift()
    {
        var producer = Contract("Order", Prop("id", required: true));
        var consumer = Contract("Order", Prop("id", required: true), Prop("customerId", required: true));
        var baseline = Contract("Order", Prop("id", required: true));

        var compatibility = _comparer.Compare(producer, consumer, "P", "C", "Order", null, null);
        var drift = _comparer.CompareForDrift(producer, baseline, "Order");

        Assert.False(compatibility.Compatible);
        Assert.Equal(ContractDriftState.NoChange, drift.State);
    }

    [Fact]
    public void NotComparableCompatibility_CanStillHaveDrift()
    {
        var current = Contract("Order", Prop("id", required: true));
        var baseline = Contract("Order", Prop("id", required: true), Prop("removed", required: true));

        var compatibility = _comparer.Compare(
            current, new NormalizedContract { Name = "Order" }, "P", "C", "Order", null, null);
        var drift = _comparer.CompareForDrift(current, baseline, "Order");

        Assert.Equal(ContractCompatibilityStatus.NotComparable, compatibility.Status);
        Assert.Equal(ContractDriftState.BreakingChange, drift.State);
    }

    [Fact]
    public void CompatibleWithBaselineUnavailable_IsRepresentable()
    {
        var producer = Contract("Order", Prop("id", required: true));
        var consumer = Contract("Order", Prop("id", required: true));

        var compatibility = _comparer.Compare(producer, consumer, "P", "C", "Order", null, null);
        var drift = _comparer.CompareForDrift(producer, null, "Order");

        Assert.True(compatibility.Compatible);
        Assert.Equal(ContractDriftState.BaselineUnavailable, drift.State);
    }

    // ---------- Messaging contracts use the same generic path ----------

    [Fact]
    public void MessagingContracts_CompareThroughGenericPath()
    {
        var producer = Contract("PlacementUpdated", Prop("placementId", required: true), Prop("status", required: true));
        var consumer = Contract("PlacementUpdated", Prop("placementId", required: true), Prop("status", required: true));

        var result = _comparer.Compare(
            producer, consumer, "PlacementService", "NotificationService", "PlacementUpdated", null, null);

        Assert.Equal(ContractCompatibilityStatus.Compatible, result.Status);
    }

    [Fact]
    public void MessagingContracts_BreakingChangeDetected()
    {
        var producer = Contract("PlacementUpdated", Prop("placementId", required: true));
        var consumer = Contract("PlacementUpdated", Prop("placementId", required: true), Prop("status", required: true));

        var result = _comparer.Compare(
            producer, consumer, "PlacementService", "NotificationService", "PlacementUpdated", null, null);

        Assert.Equal(ContractCompatibilityStatus.Breaking, result.Status);
        Assert.False(result.Compatible);
    }

    // ---------- Sanitization ----------

    [Fact]
    public void CredentialsInSourceAreRedacted()
    {
        var result = _comparer.Compare(
            Contract("Order", Prop("id", required: true)),
            Contract("Order", Prop("id", required: true)),
            "P", "C", "Order",
            "https://user:secret123@example.test/spec.json",
            "https://user:secret123@example.test/spec.json");

        Assert.DoesNotContain("secret123", result.ProducerSource ?? "");
        Assert.DoesNotContain("secret123", result.ConsumerSource ?? "");
    }
}
