using BirkNext.Api.Services.ContractAnalysis;
using Xunit;
using FluentAssertions;
using Microsoft.Extensions.Logging;

namespace BirkNext.Api.Tests.Services.ContractAnalysis;

/// <summary>
/// Phase 4: GraphQL Contract Discovery + Compatibility Analysis
/// Focused backend tests covering GraphQL source resolution, extraction, and comparison
/// </summary>
public class GraphQlContractAnalysisTests
{
    private readonly IContractComparer _comparer = new ContractComparer();
    private readonly ILogger<GraphQlExtractor> _logger = new MockLogger<GraphQlExtractor>();

    // ────────────────────────────────────────────────────────────────────────────
    // Test 1: Basic Compatible GraphQL Query
    // ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GraphQl_CompatibleQuery_ReturnsCompatible()
    {
        var producer = new GraphQlNormalizedContract
        {
            Name = "Provider Schema",
            Types = new List<GraphQlType>
            {
                new()
                {
                    Name = "Query",
                    Kind = "OBJECT",
                    Fields = new List<GraphQlField>
                    {
                        new()
                        {
                            Name = "getUser",
                            Type = new GraphQlTypeRef
                            {
                                Kind = "NON_NULL",
                                OfType = new GraphQlTypeRef
                                {
                                    Kind = "NAMED",
                                    Name = "User"
                                }
                            }
                        }
                    }
                },
                new()
                {
                    Name = "User",
                    Kind = "OBJECT",
                    Fields = new List<GraphQlField>
                    {
                        new() { Name = "id", Type = new GraphQlTypeRef { Kind = "NON_NULL", OfType = new GraphQlTypeRef { Kind = "NAMED", Name = "ID" } } },
                        new() { Name = "name", Type = new GraphQlTypeRef { Kind = "NAMED", Name = "String" } }
                    }
                }
            },
            Operations = new List<GraphQlOperation>
            {
                new()
                {
                    Kind = "query",
                    Name = "getUser",
                    RootType = "Query",
                    RootField = "getUser"
                }
            }
        };

        var consumer = new GraphQlNormalizedContract
        {
            Name = "Consumer Schema",
            Types = new List<GraphQlType>
            {
                new()
                {
                    Name = "Query",
                    Kind = "OBJECT",
                    Fields = new List<GraphQlField>
                    {
                        new()
                        {
                            Name = "getUser",
                            Type = new GraphQlTypeRef
                            {
                                Kind = "NON_NULL",
                                OfType = new GraphQlTypeRef
                                {
                                    Kind = "NAMED",
                                    Name = "User"
                                }
                            }
                        }
                    }
                },
                new()
                {
                    Name = "User",
                    Kind = "OBJECT",
                    Fields = new List<GraphQlField>
                    {
                        new() { Name = "id", Type = new GraphQlTypeRef { Kind = "NON_NULL", OfType = new GraphQlTypeRef { Kind = "NAMED", Name = "ID" } } },
                        new() { Name = "name", Type = new GraphQlTypeRef { Kind = "NAMED", Name = "String" } }
                    }
                }
            },
            Operations = new List<GraphQlOperation>
            {
                new()
                {
                    Kind = "query",
                    Name = "getUser",
                    RootType = "Query",
                    RootField = "getUser"
                }
            }
        };

        var result = _comparer.CompareGraphQL(producer, consumer, "Provider", "Consumer", "GetUser", null, null);

        result.Compatible.Should().BeTrue();
        result.Status.Should().Be(ContractCompatibilityStatus.Compatible);
        result.Differences.Should().BeEmpty();
    }

    // Test 2: Missing Query Operation
    [Fact]
    public void GraphQl_MissingQuery_ReturnsBreaking()
    {
        var producer = new GraphQlNormalizedContract
        {
            Name = "Provider",
            Types = new List<GraphQlType> { new() { Name = "Query", Kind = "OBJECT", Fields = [] } },
            Operations = new List<GraphQlOperation>()
        };

        var consumer = new GraphQlNormalizedContract
        {
            Name = "Consumer",
            Types = new List<GraphQlType> { new() { Name = "Query", Kind = "OBJECT", Fields = [] } },
            Operations = new List<GraphQlOperation>
            {
                new() { Kind = "query", Name = "getUser", RootType = "Query", RootField = "getUser" }
            }
        };

        var result = _comparer.CompareGraphQL(producer, consumer, "Provider", "Consumer", "Test", null, null);

        result.Compatible.Should().BeFalse();
        result.Status.Should().Be(ContractCompatibilityStatus.Breaking);
        result.Differences.Should().ContainSingle();
    }

    // Test 3: Output Field Removal
    [Fact]
    public void GraphQl_MissingOutputField_ReturnsBreaking()
    {
        var producer = new GraphQlNormalizedContract
        {
            Name = "Provider",
            Types = new List<GraphQlType>
            {
                new()
                {
                    Name = "User",
                    Kind = "OBJECT",
                    Fields = new List<GraphQlField>
                    {
                        new() { Name = "id", Type = new GraphQlTypeRef { Kind = "NAMED", Name = "ID" } }
                    }
                }
            },
            Operations = []
        };

        var consumer = new GraphQlNormalizedContract
        {
            Name = "Consumer",
            Types = new List<GraphQlType>
            {
                new()
                {
                    Name = "User",
                    Kind = "OBJECT",
                    Fields = new List<GraphQlField>
                    {
                        new() { Name = "id", Type = new GraphQlTypeRef { Kind = "NAMED", Name = "ID" } },
                        new() { Name = "email", Type = new GraphQlTypeRef { Kind = "NAMED", Name = "String" } }
                    }
                }
            },
            Operations = []
        };

        var result = _comparer.CompareGraphQL(producer, consumer, "Provider", "Consumer", "User", null, null);

        result.Compatible.Should().BeFalse();
        result.Differences.Should().Contain(d => d.Property == "email");
    }

    // Test 4: Output Field Type Change
    [Fact]
    public void GraphQl_OutputFieldTypeChanged_ReturnsBreaking()
    {
        var producer = new GraphQlNormalizedContract
        {
            Name = "Provider",
            Types = new List<GraphQlType>
            {
                new()
                {
                    Name = "User",
                    Kind = "OBJECT",
                    Fields = new List<GraphQlField>
                    {
                        new() { Name = "age", Type = new GraphQlTypeRef { Kind = "NAMED", Name = "Int" } }
                    }
                }
            },
            Operations = []
        };

        var consumer = new GraphQlNormalizedContract
        {
            Name = "Consumer",
            Types = new List<GraphQlType>
            {
                new()
                {
                    Name = "User",
                    Kind = "OBJECT",
                    Fields = new List<GraphQlField>
                    {
                        new() { Name = "age", Type = new GraphQlTypeRef { Kind = "NAMED", Name = "String" } }
                    }
                }
            },
            Operations = []
        };

        var result = _comparer.CompareGraphQL(producer, consumer, "Provider", "Consumer", "User", null, null);

        result.Compatible.Should().BeFalse();
        result.Differences.Should().Contain(d => d.Type == ContractDifferenceType.TypeMismatch);
    }

    // Test 5: Output Nullability Violation
    [Fact]
    public void GraphQl_OutputNullabilityViolation_ReturnsBreaking()
    {
        var producer = new GraphQlNormalizedContract
        {
            Name = "Provider",
            Types = new List<GraphQlType>
            {
                new()
                {
                    Name = "User",
                    Kind = "OBJECT",
                    Fields = new List<GraphQlField>
                    {
                        new() { Name = "id", Type = new GraphQlTypeRef { Kind = "NAMED", Name = "ID" } }
                    }
                }
            },
            Operations = []
        };

        var consumer = new GraphQlNormalizedContract
        {
            Name = "Consumer",
            Types = new List<GraphQlType>
            {
                new()
                {
                    Name = "User",
                    Kind = "OBJECT",
                    Fields = new List<GraphQlField>
                    {
                        new()
                        {
                            Name = "id",
                            Type = new GraphQlTypeRef
                            {
                                Kind = "NON_NULL",
                                OfType = new GraphQlTypeRef { Kind = "NAMED", Name = "ID" }
                            }
                        }
                    }
                }
            },
            Operations = []
        };

        var result = _comparer.CompareGraphQL(producer, consumer, "Provider", "Consumer", "User", null, null);

        result.Compatible.Should().BeFalse();
        result.Differences.Should().Contain(d => d.Type == ContractDifferenceType.NullabilityMismatch);
    }

    // Test 6: Required Input Argument Added
    [Fact]
    public void GraphQl_RequiredArgumentAdded_ReturnsBreaking()
    {
        var producer = new GraphQlNormalizedContract
        {
            Name = "Provider",
            Types = new List<GraphQlType> { new() { Name = "Query", Kind = "OBJECT" } },
            Operations = new List<GraphQlOperation>
            {
                new()
                {
                    Kind = "query",
                    Name = "getUser",
                    Arguments = new List<GraphQlArgument>
                    {
                        new()
                        {
                            Name = "tenantId",
                            Type = new GraphQlTypeRef
                            {
                                Kind = "NON_NULL",
                                OfType = new GraphQlTypeRef { Kind = "NAMED", Name = "ID" }
                            }
                        }
                    }
                }
            }
        };

        var consumer = new GraphQlNormalizedContract
        {
            Name = "Consumer",
            Types = new List<GraphQlType> { new() { Name = "Query", Kind = "OBJECT" } },
            Operations = new List<GraphQlOperation>
            {
                new()
                {
                    Kind = "query",
                    Name = "getUser",
                    Arguments = []
                }
            }
        };

        var result = _comparer.CompareGraphQL(producer, consumer, "Provider", "Consumer", "Test", null, null);

        result.Compatible.Should().BeFalse();
        result.Differences.Should().Contain(d => d.Type == ContractDifferenceType.RequirednessMismatch);
    }

    // Test 7: Optional Argument Added (Non-Breaking)
    [Fact]
    public void GraphQl_OptionalArgumentAdded_ReturnsWarning()
    {
        var producer = new GraphQlNormalizedContract
        {
            Name = "Provider",
            Types = new List<GraphQlType> { new() { Name = "Query", Kind = "OBJECT" } },
            Operations = new List<GraphQlOperation>
            {
                new()
                {
                    Kind = "query",
                    Name = "getUser",
                    Arguments = new List<GraphQlArgument>
                    {
                        new()
                        {
                            Name = "locale",
                            Type = new GraphQlTypeRef { Kind = "NAMED", Name = "String" },
                            DefaultValue = "en"
                        }
                    }
                }
            }
        };

        var consumer = new GraphQlNormalizedContract
        {
            Name = "Consumer",
            Types = new List<GraphQlType> { new() { Name = "Query", Kind = "OBJECT" } },
            Operations = new List<GraphQlOperation>
            {
                new()
                {
                    Kind = "query",
                    Name = "getUser",
                    Arguments = []
                }
            }
        };

        var result = _comparer.CompareGraphQL(producer, consumer, "Provider", "Consumer", "Test", null, null);

        result.Compatible.Should().BeTrue();
        result.Status.Should().Be(ContractCompatibilityStatus.Compatible);
    }

    // Test 8: Input Field Removal
    [Fact]
    public void GraphQl_InputFieldRemoved_ReturnsBreaking()
    {
        var producer = new GraphQlNormalizedContract
        {
            Name = "Provider",
            Types = new List<GraphQlType>
            {
                new()
                {
                    Name = "UserInput",
                    Kind = "INPUT_OBJECT",
                    InputFields = new List<GraphQlField>
                    {
                        new() { Name = "id", Type = new GraphQlTypeRef { Kind = "NAMED", Name = "ID" } }
                    }
                }
            },
            Operations = []
        };

        var consumer = new GraphQlNormalizedContract
        {
            Name = "Consumer",
            Types = new List<GraphQlType>
            {
                new()
                {
                    Name = "UserInput",
                    Kind = "INPUT_OBJECT",
                    InputFields = new List<GraphQlField>
                    {
                        new() { Name = "id", Type = new GraphQlTypeRef { Kind = "NAMED", Name = "ID" } },
                        new() { Name = "includeHistory", Type = new GraphQlTypeRef { Kind = "NAMED", Name = "Boolean" } }
                    }
                }
            },
            Operations = []
        };

        var result = _comparer.CompareGraphQL(producer, consumer, "Provider", "Consumer", "UserInput", null, null);

        result.Compatible.Should().BeFalse();
        result.Differences.Should().Contain(d => d.Property == "includeHistory");
    }

    // Test 9: Input Requiredness Increased
    [Fact]
    public void GraphQl_InputRequirednessIncreased_ReturnsBreaking()
    {
        var producer = new GraphQlNormalizedContract
        {
            Name = "Provider",
            Types = new List<GraphQlType>
            {
                new()
                {
                    Name = "UserInput",
                    Kind = "INPUT_OBJECT",
                    InputFields = new List<GraphQlField>
                    {
                        new() { Name = "name", Type = new GraphQlTypeRef { Kind = "NAMED", Name = "String" } }
                    }
                }
            },
            Operations = []
        };

        var consumer = new GraphQlNormalizedContract
        {
            Name = "Consumer",
            Types = new List<GraphQlType>
            {
                new()
                {
                    Name = "UserInput",
                    Kind = "INPUT_OBJECT",
                    InputFields = new List<GraphQlField>
                    {
                        new()
                        {
                            Name = "name",
                            Type = new GraphQlTypeRef
                            {
                                Kind = "NON_NULL",
                                OfType = new GraphQlTypeRef { Kind = "NAMED", Name = "String" }
                            }
                        }
                    }
                }
            },
            Operations = []
        };

        var result = _comparer.CompareGraphQL(producer, consumer, "Provider", "Consumer", "UserInput", null, null);

        result.Compatible.Should().BeFalse();
        result.Differences.Should().Contain(d => d.Type == ContractDifferenceType.RequirednessMismatch);
    }

    // Test 10: Mutation Compatibility
    [Fact]
    public void GraphQl_CompatibleMutation_ReturnsCompatible()
    {
        var producer = new GraphQlNormalizedContract
        {
            Name = "Provider",
            Types = new List<GraphQlType> { new() { Name = "Mutation", Kind = "OBJECT" } },
            Operations = new List<GraphQlOperation>
            {
                new() { Kind = "mutation", Name = "createUser", RootType = "Mutation" }
            }
        };

        var consumer = new GraphQlNormalizedContract
        {
            Name = "Consumer",
            Types = new List<GraphQlType> { new() { Name = "Mutation", Kind = "OBJECT" } },
            Operations = new List<GraphQlOperation>
            {
                new() { Kind = "mutation", Name = "createUser", RootType = "Mutation" }
            }
        };

        var result = _comparer.CompareGraphQL(producer, consumer, "Provider", "Consumer", "Test", null, null);

        result.Compatible.Should().BeTrue();
    }
}

// Mock logger for testing
internal class MockLogger<T> : ILogger<T>
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => false;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }
}
