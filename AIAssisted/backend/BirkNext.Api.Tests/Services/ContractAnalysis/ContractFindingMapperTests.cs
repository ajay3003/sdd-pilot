using BirkNext.Api.Services.ContractAnalysis;
using BirkNext.Api.Services.IntegrationQuality;
using Xunit;

namespace BirkNext.Api.Tests.Services.ContractAnalysis;

/// <summary>
/// Phase 3 Checkpoint 4: finding generation from compatibility and drift results.
/// Findings are raised only for deterministic evidence. A comparison that did not occur
/// produces no finding rather than a failure.
/// </summary>
public class ContractFindingMapperTests
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

    private static NormalizedProperty Prop(string name, string type = "string", bool required = false) =>
        new() { Name = name, Type = type, Required = required };

    [Fact]
    public void BreakingCompatibility_CreatesHighSeverityFinding()
    {
        var result = _comparer.Compare(
            Contract("Order", Prop("id", required: true)),
            Contract("Order", Prop("id", required: true), Prop("customerId", required: true)),
            "OrderService", "BillingService", "Order", null, null);

        var finding = ContractFindingMapper.ForCompatibility(result, "int-1", "Orders");

        Assert.NotNull(finding);
        Assert.Equal(IntegrationFindingSeverity.High, finding!.Severity);
        Assert.Equal("int-1", finding.IntegrationId);
        Assert.NotEmpty(finding.Evidence);
    }

    [Fact]
    public void CompatibleResult_CreatesNoFinding()
    {
        var result = _comparer.Compare(
            Contract("Order", Prop("id", required: true)),
            Contract("Order", Prop("id", required: true)),
            "P", "C", "Order", null, null);

        Assert.Null(ContractFindingMapper.ForCompatibility(result, "int-1", "Orders"));
    }

    [Fact]
    public void NotComparableCompatibility_CreatesNoFinding()
    {
        var result = _comparer.Compare(
            Contract("Order", Prop("id", required: true)),
            new NormalizedContract { Name = "Order" },
            "P", "C", "Order", null, null);

        Assert.Equal(ContractCompatibilityStatus.NotComparable, result.Status);
        Assert.Null(ContractFindingMapper.ForCompatibility(result, "int-1", "Orders"));
    }

    [Fact]
    public void NonBreakingCompatibilityDifference_IsNotHighSeverity()
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
        var finding = ContractFindingMapper.ForCompatibility(result, "int-1", "Orders");

        Assert.NotNull(finding);
        Assert.NotEqual(IntegrationFindingSeverity.High, finding!.Severity);
        Assert.NotEqual(IntegrationFindingSeverity.Critical, finding.Severity);
    }

    [Fact]
    public void BreakingDrift_CreatesHighSeverityFinding()
    {
        var drift = _comparer.CompareForDrift(
            Contract("Order", Prop("id", required: true)),
            Contract("Order", Prop("id", required: true), Prop("legacy", required: true)),
            "Order");

        var finding = ContractFindingMapper.ForDrift(drift, "int-1", "Orders");

        Assert.NotNull(finding);
        Assert.Equal(IntegrationFindingSeverity.High, finding!.Severity);
    }

    [Fact]
    public void NonBreakingDrift_IsInformational()
    {
        var drift = _comparer.CompareForDrift(
            Contract("Order", Prop("id", required: true), Prop("added")),
            Contract("Order", Prop("id", required: true)),
            "Order");

        var finding = ContractFindingMapper.ForDrift(drift, "int-1", "Orders");

        Assert.NotNull(finding);
        Assert.Equal(IntegrationFindingSeverity.Info, finding!.Severity);
    }

    [Fact]
    public void NoDriftChange_CreatesNoFinding()
    {
        var drift = _comparer.CompareForDrift(
            Contract("Order", Prop("id", required: true)),
            Contract("Order", Prop("id", required: true)),
            "Order");

        Assert.Null(ContractFindingMapper.ForDrift(drift, "int-1", "Orders"));
    }

    [Fact]
    public void BaselineUnavailable_CreatesNoFinding()
    {
        var drift = _comparer.CompareForDrift(
            Contract("Order", Prop("id", required: true)), null, "Order");

        Assert.Equal(ContractDriftState.BaselineUnavailable, drift.State);
        Assert.Null(ContractFindingMapper.ForDrift(drift, "int-1", "Orders"));
    }

    [Fact]
    public void FindingIds_AreDeterministic()
    {
        var result = _comparer.Compare(
            Contract("Order", Prop("id", required: true)),
            Contract("Order", Prop("id", required: true), Prop("customerId", required: true)),
            "P", "C", "Order", null, null);

        var first = ContractFindingMapper.ForCompatibility(result, "int-1", "Orders");
        var second = ContractFindingMapper.ForCompatibility(result, "int-1", "Orders");

        Assert.Equal(first!.Id, second!.Id);
        Assert.Equal("contract-compat-int-1", first.Id);
    }

    [Fact]
    public void CompatibilityAndDriftFindingIds_DoNotCollide()
    {
        var compat = _comparer.Compare(
            Contract("Order", Prop("id", required: true)),
            Contract("Order", Prop("id", required: true), Prop("customerId", required: true)),
            "P", "C", "Order", null, null);

        var drift = _comparer.CompareForDrift(
            Contract("Order", Prop("id", required: true)),
            Contract("Order", Prop("id", required: true), Prop("legacy", required: true)),
            "Order");

        var compatFinding = ContractFindingMapper.ForCompatibility(compat, "int-1", "Orders");
        var driftFinding = ContractFindingMapper.ForDrift(drift, "int-1", "Orders");

        Assert.NotEqual(compatFinding!.Id, driftFinding!.Id);
    }

    [Fact]
    public void FindingEvidence_DoesNotLeakCredentials()
    {
        var result = _comparer.Compare(
            Contract("Order", Prop("id", required: true)),
            Contract("Order", Prop("id", required: true), Prop("customerId", required: true)),
            "P", "C", "Order",
            "https://user:secret123@example.test/p.json",
            "https://user:secret123@example.test/c.json");

        var finding = ContractFindingMapper.ForCompatibility(result, "int-1", "Orders");

        Assert.NotNull(finding);
        Assert.DoesNotContain(finding!.Evidence, e => e.Contains("secret123"));
        Assert.DoesNotContain("secret123", finding.Description);
    }

    [Fact]
    public void FindingEvidence_IsOrderedDeterministically()
    {
        var consumer = Contract(
            "Order", Prop("id", required: true), Prop("zeta", required: true), Prop("alpha", required: true));

        var result = _comparer.Compare(
            Contract("Order", Prop("id", required: true)), consumer, "P", "C", "Order", null, null);

        var first = ContractFindingMapper.ForCompatibility(result, "int-1", "Orders");
        var second = ContractFindingMapper.ForCompatibility(result, "int-1", "Orders");

        Assert.Equal(first!.Evidence, second!.Evidence);
    }
}
