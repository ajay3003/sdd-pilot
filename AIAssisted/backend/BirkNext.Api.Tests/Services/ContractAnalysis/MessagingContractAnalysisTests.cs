using BirkNext.Api.Services.ContractAnalysis;
using Xunit;

namespace BirkNext.Api.Tests.Services.ContractAnalysis;

/// <summary>
/// Phase 5 messaging contract analysis tests.
/// Focus: EventHub producer→consumer compatibility analysis.
/// Reuses normalized contract model and comparison logic from Phase 3/4.
/// </summary>
public class MessagingContractAnalysisTests
{
    private readonly IContractComparer _comparer = new ContractComparer();

    [Fact]
    public void CompatibleContracts_ReturnsPass()
    {
        // Arrange
        var producer = new NormalizedContract
        {
            Name = "ChildUpdated",
            Schemas = new()
            {
                new NormalizedSchema
                {
                    Name = "ChildUpdated",
                    Type = "object",
                    Properties = new()
                    {
                        new NormalizedProperty { Name = "childId", Type = "string", Required = true, Nullable = false },
                        new NormalizedProperty { Name = "status", Type = "string", Required = false, Nullable = true }
                    },
                    Required = new() { "childId" }
                }
            }
        };

        var consumer = new NormalizedContract
        {
            Name = "ChildUpdated",
            Schemas = new()
            {
                new NormalizedSchema
                {
                    Name = "ChildUpdated",
                    Type = "object",
                    Properties = new()
                    {
                        new NormalizedProperty { Name = "childId", Type = "string", Required = true, Nullable = false },
                        new NormalizedProperty { Name = "status", Type = "string", Required = false, Nullable = true }
                    },
                    Required = new() { "childId" }
                }
            }
        };

        // Act
        var result = _comparer.Compare(
            producer, consumer,
            "ChildService", "PlacementService",
            "ChildUpdated",
            "assembly://producer.dll", "assembly://consumer.dll");

        // Assert
        Assert.True(result.Compatible);
        Assert.Equal(ContractCompatibilityStatus.Compatible, result.Status);
        Assert.Empty(result.Differences);
    }

    [Fact]
    public void ProducerMissingRequiredField_ReturnsBreaking()
    {
        // Arrange
        var producer = new NormalizedContract
        {
            Name = "ChildUpdated",
            Schemas = new()
            {
                new NormalizedSchema
                {
                    Name = "ChildUpdated",
                    Type = "object",
                    Properties = new()
                    {
                        new NormalizedProperty { Name = "status", Type = "string", Required = false, Nullable = true }
                    },
                    Required = new()
                }
            }
        };

        var consumer = new NormalizedContract
        {
            Name = "ChildUpdated",
            Schemas = new()
            {
                new NormalizedSchema
                {
                    Name = "ChildUpdated",
                    Type = "object",
                    Properties = new()
                    {
                        new NormalizedProperty { Name = "childId", Type = "string", Required = true, Nullable = false },
                        new NormalizedProperty { Name = "status", Type = "string", Required = false, Nullable = true }
                    },
                    Required = new() { "childId" }
                }
            }
        };

        // Act
        var result = _comparer.Compare(
            producer, consumer,
            "ChildService", "PlacementService",
            "ChildUpdated",
            "assembly://producer.dll", "assembly://consumer.dll");

        // Assert
        Assert.False(result.Compatible);
        Assert.Equal(ContractCompatibilityStatus.Breaking, result.Status);
        var childIdDiff = result.Differences.FirstOrDefault(d => d.Property == "childId");
        Assert.NotNull(childIdDiff);
        Assert.Equal(ContractDifferenceType.MissingRequiredProperty, childIdDiff.Type);
        Assert.Equal(ContractDifferenceSeverity.Breaking, childIdDiff.Severity);
    }

    [Fact]
    public void ProducerTypeMismatch_ReturnsBreaking()
    {
        // Arrange
        var producer = new NormalizedContract
        {
            Name = "Payment",
            Schemas = new()
            {
                new NormalizedSchema
                {
                    Name = "Payment",
                    Type = "object",
                    Properties = new()
                    {
                        new NormalizedProperty { Name = "amount", Type = "integer", Required = true, Nullable = false }
                    },
                    Required = new() { "amount" }
                }
            }
        };

        var consumer = new NormalizedContract
        {
            Name = "Payment",
            Schemas = new()
            {
                new NormalizedSchema
                {
                    Name = "Payment",
                    Type = "object",
                    Properties = new()
                    {
                        new NormalizedProperty { Name = "amount", Type = "string", Required = true, Nullable = false }
                    },
                    Required = new() { "amount" }
                }
            }
        };

        // Act
        var result = _comparer.Compare(
            producer, consumer,
            "PaymentService", "BillingService",
            "Payment",
            "assembly://producer.dll", "assembly://consumer.dll");

        // Assert
        Assert.False(result.Compatible);
        Assert.Equal(ContractCompatibilityStatus.Breaking, result.Status);
        var amountDiff = result.Differences.FirstOrDefault(d => d.Property == "amount");
        Assert.NotNull(amountDiff);
        Assert.Equal(ContractDifferenceType.TypeMismatch, amountDiff.Type);
    }

    [Fact]
    public void ProducerNullableConsumerNonNull_ReturnsBreaking()
    {
        // Arrange
        var producer = new NormalizedContract
        {
            Name = "Event",
            Schemas = new()
            {
                new NormalizedSchema
                {
                    Name = "Event",
                    Type = "object",
                    Properties = new()
                    {
                        new NormalizedProperty { Name = "timestamp", Type = "string", Required = false, Nullable = true }
                    },
                    Required = new()
                }
            }
        };

        var consumer = new NormalizedContract
        {
            Name = "Event",
            Schemas = new()
            {
                new NormalizedSchema
                {
                    Name = "Event",
                    Type = "object",
                    Properties = new()
                    {
                        new NormalizedProperty { Name = "timestamp", Type = "string", Required = true, Nullable = false }
                    },
                    Required = new() { "timestamp" }
                }
            }
        };

        // Act
        var result = _comparer.Compare(
            producer, consumer,
            "Producer", "Consumer",
            "Event",
            "assembly://producer.dll", "assembly://consumer.dll");

        // Assert
        Assert.False(result.Compatible);
        var nullDiff = result.Differences.FirstOrDefault(d => d.Type == ContractDifferenceType.NullabilityMismatch);
        Assert.NotNull(nullDiff);
        Assert.Equal(ContractDifferenceSeverity.Breaking, nullDiff.Severity);
    }

    [Fact]
    public void EnumIncompatibility_ReturnsBreaking()
    {
        // Arrange
        var producer = new NormalizedContract
        {
            Name = "Order",
            Schemas = new()
            {
                new NormalizedSchema
                {
                    Name = "OrderStatus",
                    Type = "string",
                    EnumValues = new() { "Pending", "Processing", "Shipped", "Cancelled" },
                    Required = new()
                }
            }
        };

        var consumer = new NormalizedContract
        {
            Name = "Order",
            Schemas = new()
            {
                new NormalizedSchema
                {
                    Name = "OrderStatus",
                    Type = "string",
                    EnumValues = new() { "Pending", "Shipped" },
                    Required = new()
                }
            }
        };

        // Act
        var result = _comparer.Compare(
            producer, consumer,
            "OrderService", "FulfillmentService",
            "Order",
            "assembly://producer.dll", "assembly://consumer.dll");

        // Assert
        Assert.False(result.Compatible);
        var enumDiff = result.Differences.FirstOrDefault(d => d.Type == ContractDifferenceType.EnumValueMismatch);
        Assert.NotNull(enumDiff);
        Assert.Contains("Processing", enumDiff.ProducerValue ?? "");
        Assert.Contains("Cancelled", enumDiff.ProducerValue ?? "");
    }

    [Fact]
    public void ArrayElementTypeMismatch_ReturnsBreaking()
    {
        // Arrange
        var producer = new NormalizedContract
        {
            Name = "Batch",
            Schemas = new()
            {
                new NormalizedSchema
                {
                    Name = "Batch",
                    Type = "object",
                    Properties = new()
                    {
                        new NormalizedProperty { Name = "ids", Type = "array", ArrayItemType = "string", Required = true, Nullable = false }
                    },
                    Required = new() { "ids" }
                }
            }
        };

        var consumer = new NormalizedContract
        {
            Name = "Batch",
            Schemas = new()
            {
                new NormalizedSchema
                {
                    Name = "Batch",
                    Type = "object",
                    Properties = new()
                    {
                        new NormalizedProperty { Name = "ids", Type = "array", ArrayItemType = "integer", Required = true, Nullable = false }
                    },
                    Required = new() { "ids" }
                }
            }
        };

        // Act
        var result = _comparer.Compare(
            producer, consumer,
            "Producer", "Consumer",
            "Batch",
            "assembly://producer.dll", "assembly://consumer.dll");

        // Assert
        Assert.False(result.Compatible);
        var arrayDiff = result.Differences.FirstOrDefault(d => d.Type == ContractDifferenceType.ArrayItemTypeMismatch);
        Assert.NotNull(arrayDiff);
    }

    [Fact]
    public void ExtraProducerPropertyAllowed_IsNonBreaking()
    {
        // Arrange
        var producer = new NormalizedContract
        {
            Name = "Message",
            Schemas = new()
            {
                new NormalizedSchema
                {
                    Name = "Message",
                    Type = "object",
                    Properties = new()
                    {
                        new NormalizedProperty { Name = "id", Type = "string", Required = true, Nullable = false },
                        new NormalizedProperty { Name = "extraField", Type = "string", Required = false, Nullable = true }
                    },
                    Required = new() { "id" },
                    AllowsAdditionalProperties = true
                }
            }
        };

        var consumer = new NormalizedContract
        {
            Name = "Message",
            Schemas = new()
            {
                new NormalizedSchema
                {
                    Name = "Message",
                    Type = "object",
                    Properties = new()
                    {
                        new NormalizedProperty { Name = "id", Type = "string", Required = true, Nullable = false }
                    },
                    Required = new() { "id" },
                    AllowsAdditionalProperties = true
                }
            }
        };

        // Act
        var result = _comparer.Compare(
            producer, consumer,
            "Producer", "Consumer",
            "Message",
            "assembly://producer.dll", "assembly://consumer.dll");

        // Assert
        Assert.True(result.Compatible);
        Assert.Equal(ContractCompatibilityStatus.Warning, result.Status);
        var extraPropDiff = result.Differences.FirstOrDefault(d => d.Type == ContractDifferenceType.AdditionalProducerProperty);
        Assert.NotNull(extraPropDiff);
        Assert.Equal(ContractDifferenceSeverity.Info, extraPropDiff.Severity);
    }

    [Fact]
    public void ProducerRequiredConsumerOptional_IsNonBreaking()
    {
        // Arrange
        var producer = new NormalizedContract
        {
            Name = "Data",
            Schemas = new()
            {
                new NormalizedSchema
                {
                    Name = "Data",
                    Type = "object",
                    Properties = new()
                    {
                        new NormalizedProperty { Name = "field", Type = "string", Required = true, Nullable = false }
                    },
                    Required = new() { "field" }
                }
            }
        };

        var consumer = new NormalizedContract
        {
            Name = "Data",
            Schemas = new()
            {
                new NormalizedSchema
                {
                    Name = "Data",
                    Type = "object",
                    Properties = new()
                    {
                        new NormalizedProperty { Name = "field", Type = "string", Required = false, Nullable = false }
                    },
                    Required = new()
                }
            }
        };

        // Act
        var result = _comparer.Compare(
            producer, consumer,
            "Producer", "Consumer",
            "Data",
            "assembly://producer.dll", "assembly://consumer.dll");

        // Assert
        Assert.True(result.Compatible);
    }

    [Fact]
    public void NestedPropertyPath_IsPreservedInDifference()
    {
        // Arrange
        var producer = new NormalizedContract
        {
            Name = "Outer",
            Schemas = new()
            {
                new NormalizedSchema
                {
                    Name = "Inner",
                    Type = "object",
                    Properties = new()
                    {
                        new NormalizedProperty { Name = "nested", Type = "integer", Required = true, Nullable = false }
                    },
                    Required = new() { "nested" }
                }
            }
        };

        var consumer = new NormalizedContract
        {
            Name = "Outer",
            Schemas = new()
            {
                new NormalizedSchema
                {
                    Name = "Inner",
                    Type = "object",
                    Properties = new()
                    {
                        new NormalizedProperty { Name = "nested", Type = "string", Required = true, Nullable = false }
                    },
                    Required = new() { "nested" }
                }
            }
        };

        // Act
        var result = _comparer.Compare(
            producer, consumer,
            "Producer", "Consumer",
            "Outer",
            "assembly://producer.dll", "assembly://consumer.dll");

        // Assert
        Assert.False(result.Compatible);
        var diff = result.Differences.FirstOrDefault();
        Assert.NotNull(diff);
        Assert.Contains("Inner.nested", diff.Path);
    }

    [Fact]
    public void DeterministicOrdering_SameInputProducesSameOutput()
    {
        // Arrange
        var producer = new NormalizedContract
        {
            Name = "Event",
            Schemas = new()
            {
                new NormalizedSchema
                {
                    Name = "Event",
                    Type = "object",
                    Properties = new()
                    {
                        new NormalizedProperty { Name = "zField", Type = "string", Required = true },
                        new NormalizedProperty { Name = "aField", Type = "integer", Required = false }
                    },
                    Required = new() { "zField" }
                }
            }
        };

        var consumer = new NormalizedContract
        {
            Name = "Event",
            Schemas = new()
            {
                new NormalizedSchema
                {
                    Name = "Event",
                    Type = "object",
                    Properties = new()
                    {
                        new NormalizedProperty { Name = "aField", Type = "string", Required = false }
                    },
                    Required = new()
                }
            }
        };

        // Act
        var result1 = _comparer.Compare(producer, consumer, "P", "C", "E", null, null);
        var result2 = _comparer.Compare(producer, consumer, "P", "C", "E", null, null);

        // Assert
        Assert.Equal(result1.Differences.Count, result2.Differences.Count);
        for (int i = 0; i < result1.Differences.Count; i++)
        {
            Assert.Equal(result1.Differences[i].Path, result2.Differences[i].Path);
            Assert.Equal(result1.Differences[i].Property, result2.Differences[i].Property);
        }
    }
}
