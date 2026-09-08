using System.Text.Json.Serialization;
using BirkNext.Api.Services.IntegrationQuality;

namespace BirkNext.Api.Services.ContractAnalysis;

/// <summary>
/// Normalized contract model independent of OpenAPI/GraphQL syntax.
/// Used for deterministic comparison of producer/consumer contracts.
/// </summary>
public sealed class NormalizedContract
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("source")]
    public ContractSource Source { get; set; } = new();

    [JsonPropertyName("operations")]
    public List<NormalizedOperation> Operations { get; set; } = [];

    [JsonPropertyName("schemas")]
    public List<NormalizedSchema> Schemas { get; set; } = [];
}

public sealed class ContractSource
{
    [JsonPropertyName("type")]
    public ContractSourceType Type { get; set; }

    [JsonPropertyName("location")]
    public string? Location { get; set; }

    [JsonPropertyName("fetched_at")]
    public DateTime FetchedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Represents REST operation or message contract.
/// </summary>
public sealed class NormalizedOperation
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("method")]
    public string Method { get; set; } = "GET";

    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    [JsonPropertyName("request_schema")]
    public NormalizedSchema? RequestSchema { get; set; }

    [JsonPropertyName("response_schemas")]
    public Dictionary<string, NormalizedSchema> ResponseSchemas { get; set; } = [];
}

/// <summary>
/// Normalized schema independent of OpenAPI/JSON Schema syntax.
/// </summary>
public sealed class NormalizedSchema
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "object";

    [JsonPropertyName("properties")]
    public List<NormalizedProperty> Properties { get; set; } = [];

    [JsonPropertyName("required")]
    public List<string> Required { get; set; } = [];

    [JsonPropertyName("nullable")]
    public bool Nullable { get; set; }

    [JsonPropertyName("enum_values")]
    public List<string>? EnumValues { get; set; }

    [JsonPropertyName("array_item_type")]
    public string? ArrayItemType { get; set; }

    [JsonPropertyName("allows_additional")]
    public bool? AllowsAdditionalProperties { get; set; }
}

/// <summary>
/// Normalized property definition.
/// </summary>
public sealed class NormalizedProperty
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("required")]
    public bool Required { get; set; }

    [JsonPropertyName("nullable")]
    public bool Nullable { get; set; }

    [JsonPropertyName("format")]
    public string? Format { get; set; }

    [JsonPropertyName("enum_values")]
    public List<string>? EnumValues { get; set; }

    [JsonPropertyName("array_item_type")]
    public string? ArrayItemType { get; set; }
}

/// <summary>
/// Result of contract compatibility analysis.
/// Producer → Consumer directional semantics.
/// </summary>
public sealed class ContractCompatibilityResult
{
    [JsonPropertyName("compatible")]
    public bool Compatible { get; set; }

    [JsonPropertyName("status")]
    public ContractCompatibilityStatus Status { get; set; } = ContractCompatibilityStatus.Compatible;

    [JsonPropertyName("producer")]
    public string Producer { get; set; } = "";

    [JsonPropertyName("consumer")]
    public string Consumer { get; set; } = "";

    [JsonPropertyName("contract")]
    public string Contract { get; set; } = "";

    [JsonPropertyName("producer_source")]
    public string? ProducerSource { get; set; }

    [JsonPropertyName("consumer_source")]
    public string? ConsumerSource { get; set; }

    [JsonPropertyName("differences")]
    public List<ContractDifference> Differences { get; set; } = [];

    [JsonPropertyName("analysis_readiness")]
    public ContractAnalysisReadiness AnalysisReadiness { get; set; } = ContractAnalysisReadiness.Ready;

    [JsonPropertyName("ready_reason")]
    public string? ReadyReason { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";
}

public enum ContractCompatibilityStatus
{
    Compatible = 0,
    Warning = 1,
    Breaking = 2,
    Unsupported = 3,
    NotReady = 4,
    Error = 5
}

public enum ContractAnalysisReadiness
{
    Ready = 0,
    NotReady = 1,
    Unsupported = 2,
    Error = 3
}

/// <summary>
/// Typed difference between producer and consumer contracts.
/// </summary>
public sealed class ContractDifference
{
    [JsonPropertyName("type")]
    public ContractDifferenceType Type { get; set; }

    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    [JsonPropertyName("operation")]
    public string? Operation { get; set; }

    [JsonPropertyName("property")]
    public string? Property { get; set; }

    [JsonPropertyName("producer_value")]
    public string? ProducerValue { get; set; }

    [JsonPropertyName("consumer_value")]
    public string? ConsumerValue { get; set; }

    [JsonPropertyName("severity")]
    public ContractDifferenceSeverity Severity { get; set; }

    [JsonPropertyName("explanation")]
    public string Explanation { get; set; } = "";
}

public enum ContractDifferenceType
{
    MissingRequiredProperty = 0,
    TypeMismatch = 1,
    RequirednessMismatch = 2,
    NullabilityMismatch = 3,
    EnumValueMismatch = 4,
    ArrayItemTypeMismatch = 5,
    MissingOperation = 6,
    ResponseContractMismatch = 7,
    AdditionalProducerProperty = 8,
    AdditionalConsumerOptionalProperty = 9,
    UnsupportedSchema = 10,
    FormatMismatch = 11
}

public enum ContractDifferenceSeverity
{
    Info = 0,
    Warning = 1,
    Breaking = 2
}

/// <summary>
/// GraphQL-specific normalized types for Phase 4.
/// </summary>
public sealed class GraphQlOperation
{
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "query"; // query, mutation, subscription

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("root_type")]
    public string RootType { get; set; } = "Query"; // Query, Mutation, Subscription

    [JsonPropertyName("root_field")]
    public string RootField { get; set; } = "";

    [JsonPropertyName("arguments")]
    public List<GraphQlArgument> Arguments { get; set; } = [];

    [JsonPropertyName("return_type")]
    public GraphQlTypeRef? ReturnType { get; set; }
}

public sealed class GraphQlType
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "OBJECT"; // SCALAR, OBJECT, INTERFACE, UNION, ENUM, INPUT_OBJECT, LIST, NON_NULL

    [JsonPropertyName("fields")]
    public List<GraphQlField> Fields { get; set; } = [];

    [JsonPropertyName("enum_values")]
    public List<GraphQlEnumValue>? EnumValues { get; set; }

    [JsonPropertyName("input_fields")]
    public List<GraphQlField>? InputFields { get; set; }

    [JsonPropertyName("interfaces")]
    public List<string> Interfaces { get; set; } = [];

    [JsonPropertyName("possible_types")]
    public List<string>? PossibleTypes { get; set; } // For interfaces/unions

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("deprecated")]
    public bool IsDeprecated { get; set; }

    [JsonPropertyName("deprecation_reason")]
    public string? DeprecationReason { get; set; }
}

public sealed class GraphQlField
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("type")]
    public GraphQlTypeRef? Type { get; set; }

    [JsonPropertyName("arguments")]
    public List<GraphQlArgument> Arguments { get; set; } = [];

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("deprecated")]
    public bool IsDeprecated { get; set; }

    [JsonPropertyName("deprecation_reason")]
    public string? DeprecationReason { get; set; }
}

public sealed class GraphQlArgument
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("type")]
    public GraphQlTypeRef? Type { get; set; }

    [JsonPropertyName("default_value")]
    public string? DefaultValue { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }
}

public sealed class GraphQlTypeRef
{
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "NAMED"; // NAMED, LIST, NON_NULL

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("of_type")]
    public GraphQlTypeRef? OfType { get; set; }

    // Helper method for unwrapping type references
    public (string TypeName, bool IsNonNull, int ListDepth) Unwrap()
    {
        var isNonNull = false;
        var listDepth = 0;
        var current = this;

        while (current != null)
        {
            if (current.Kind == "NON_NULL")
            {
                isNonNull = true;
            }
            else if (current.Kind == "LIST")
            {
                listDepth++;
            }
            else if (current.Kind == "NAMED")
            {
                return (current.Name ?? "", isNonNull, listDepth);
            }

            current = current.OfType;
        }

        return ("", isNonNull, listDepth);
    }
}

public sealed class GraphQlEnumValue
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("deprecated")]
    public bool IsDeprecated { get; set; }

    [JsonPropertyName("deprecation_reason")]
    public string? DeprecationReason { get; set; }
}

// Extended to support GraphQL in addition to REST
public sealed class GraphQlNormalizedContract
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("source")]
    public ContractSource Source { get; set; } = new();

    [JsonPropertyName("graphql_operations")]
    public List<GraphQlOperation> Operations { get; set; } = [];

    [JsonPropertyName("graphql_types")]
    public List<GraphQlType> Types { get; set; } = [];
}

public enum GraphQlExtractionStatus
{
    Success = 0,
    InvalidJson = 1,
    MissingSchema = 2,
    InvalidSchema = 3,
    IntrospectionDisabled = 4,
    ComplexityLimitExceeded = 5,
    ParseError = 6
}
