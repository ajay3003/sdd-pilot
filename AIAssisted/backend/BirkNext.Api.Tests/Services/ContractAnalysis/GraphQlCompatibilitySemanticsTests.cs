using BirkNext.Api.Services.ContractAnalysis;
using Xunit;

namespace BirkNext.Api.Tests.Services.ContractAnalysis;

/// <summary>
/// Phase 3 Checkpoint 4: GraphQL aggregate-status semantics.
/// A GraphQL comparison that did not occur must not be reported as compatible.
/// Existing GraphQL difference generation is unchanged.
/// </summary>
public class GraphQlCompatibilitySemanticsTests
{
    private readonly ContractComparer _comparer = new();

    private static GraphQlNormalizedContract Gql(params string[] operationNames) =>
        new()
        {
            Name = "Schema",
            Operations = operationNames
                .Select(n => new GraphQlOperation
                {
                    Kind = "query",
                    Name = n,
                    RootType = "Query",
                    RootField = n
                })
                .ToList()
        };

    private static GraphQlNormalizedContract Empty() => new() { Name = "Schema" };

    [Fact]
    public void ProducerOnly_IsNotComparable()
    {
        var result = _comparer.CompareGraphQL(
            Gql("GetUser"), Empty(), "P", "C", "Schema", null, null);

        Assert.Equal(ContractCompatibilityStatus.NotComparable, result.Status);
        Assert.False(result.Compatible);
        Assert.Contains("consumer expectation unavailable", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ConsumerOnly_IsNotComparable()
    {
        var result = _comparer.CompareGraphQL(
            Empty(), Gql("GetUser"), "P", "C", "Schema", null, null);

        Assert.Equal(ContractCompatibilityStatus.NotComparable, result.Status);
        Assert.False(result.Compatible);
        Assert.Contains("producer schema unavailable", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BothEmpty_IsNotComparable()
    {
        var result = _comparer.CompareGraphQL(
            Empty(), Empty(), "P", "C", "Schema", null, null);

        Assert.Equal(ContractCompatibilityStatus.NotComparable, result.Status);
        Assert.False(result.Compatible);
        Assert.Contains("neither", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ComparableWithNoDifferences_IsCompatible()
    {
        var result = _comparer.CompareGraphQL(
            Gql("GetUser"), Gql("GetUser"), "P", "C", "Schema", null, null);

        Assert.Equal(ContractCompatibilityStatus.Compatible, result.Status);
        Assert.True(result.Compatible);
        Assert.Empty(result.Differences);
    }

    [Fact]
    public void ConsumerOperationMissingFromProducer_IsBreaking()
    {
        var result = _comparer.CompareGraphQL(
            Gql("SomethingElse"), Gql("GetUser"), "P", "C", "Schema", null, null);

        Assert.Equal(ContractCompatibilityStatus.Breaking, result.Status);
        Assert.False(result.Compatible);
        Assert.Contains(result.Differences, d =>
            d.Type == ContractDifferenceType.MissingOperation
            && d.Severity == ContractDifferenceSeverity.Breaking);
    }

    [Fact]
    public void NotComparable_DoesNotUseAbsoluteCompatibleWording()
    {
        var result = _comparer.CompareGraphQL(
            Gql("GetUser"), Empty(), "P", "C", "Schema", null, null);

        Assert.DoesNotContain("are compatible", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fully compatible", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CompatibleWording_IsNotAnAbsoluteClaim()
    {
        var result = _comparer.CompareGraphQL(
            Gql("GetUser"), Gql("GetUser"), "P", "C", "Schema", null, null);

        Assert.DoesNotContain("fully compatible", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("compared", result.Message, StringComparison.OrdinalIgnoreCase);
    }
}
