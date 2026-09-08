using System.Text.Json;
using BirkNext.Api.Services.IntegrationQuality;

namespace BirkNext.Api.Services.ContractAnalysis;

/// <summary>
/// Extracts normalized GraphQL schema from introspection JSON.
/// Complexity bounds: max 1000 types, 500 fields/type, 100 args/field.
/// </summary>
public interface IGraphQlExtractor
{
    GraphQlExtractionResult Extract(string introspectionJson);
}

public sealed class GraphQlExtractor : IGraphQlExtractor
{
    private const int MaxTypes = 1000;
    private const int MaxFieldsPerType = 500;
    private const int MaxArgsPerField = 100;
    private const int MaxEnumValuesPerType = 1000;

    private readonly ILogger<GraphQlExtractor> _logger;

    public GraphQlExtractor(ILogger<GraphQlExtractor> logger)
    {
        _logger = logger;
    }

    public GraphQlExtractionResult Extract(string introspectionJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(introspectionJson);
            var root = doc.RootElement;

            // Check for standard introspection structure
            if (!root.TryGetProperty("data", out var dataEl))
            {
                _logger.LogWarning("Missing 'data' field in introspection response");
                return GraphQlExtractionResult.Failure("Invalid introspection format: missing 'data' field");
            }

            if (!dataEl.TryGetProperty("__schema", out var schemaEl))
            {
                _logger.LogWarning("Missing '__schema' field in introspection data");
                return GraphQlExtractionResult.Failure("Invalid introspection format: missing '__schema'");
            }

            // Extract query/mutation/subscription type names
            string? queryTypeName = ExtractTypeName(schemaEl, "queryType");
            string? mutationTypeName = ExtractTypeName(schemaEl, "mutationType");
            string? subscriptionTypeName = ExtractTypeName(schemaEl, "subscriptionType");

            // Extract types
            var types = new List<GraphQlType>();
            if (schemaEl.TryGetProperty("types", out var typesEl) && typesEl.ValueKind == JsonValueKind.Array)
            {
                if (typesEl.GetArrayLength() > MaxTypes)
                {
                    return GraphQlExtractionResult.Failure($"Schema exceeds type limit: {typesEl.GetArrayLength()} > {MaxTypes}");
                }

                foreach (var typeEl in typesEl.EnumerateArray())
                {
                    if (ExtractType(typeEl) is { } type)
                    {
                        types.Add(type);
                    }
                }
            }

            // Build contract
            var contract = new GraphQlNormalizedContract
            {
                Name = ExtractSchemaName(schemaEl),
                Source = new()
                {
                    Type = ContractSourceType.GraphQlSchema,
                    FetchedAt = DateTime.UtcNow
                },
                Types = types,
                Operations = ExtractOperations(queryTypeName, mutationTypeName, subscriptionTypeName, types)
            };

            return GraphQlExtractionResult.SuccessResult(contract);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse introspection JSON");
            return GraphQlExtractionResult.Failure($"JSON parse error: {ex.Message}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during GraphQL extraction");
            return GraphQlExtractionResult.Failure($"Extraction error: {ex.Message}");
        }
    }

    private static string? ExtractTypeName(JsonElement schemaEl, string propertyName)
    {
        if (schemaEl.TryGetProperty(propertyName, out var typeEl) && typeEl.ValueKind == JsonValueKind.Object)
        {
            if (typeEl.TryGetProperty("name", out var nameEl))
            {
                return nameEl.GetString();
            }
        }
        return null;
    }

    private static string ExtractSchemaName(JsonElement schemaEl)
    {
        if (schemaEl.TryGetProperty("types", out var typesEl) && typesEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var typeEl in typesEl.EnumerateArray())
            {
                if (typeEl.TryGetProperty("name", out var nameEl))
                {
                    var name = nameEl.GetString();
                    if (name == "Query")
                        return "GraphQL Schema";
                }
            }
        }
        return "Unknown GraphQL Schema";
    }

    private static GraphQlType? ExtractType(JsonElement typeEl)
    {
        if (!typeEl.TryGetProperty("name", out var nameEl))
            return null;

        var name = nameEl.GetString() ?? "Unknown";

        // Skip introspection types
        if (name.StartsWith("__"))
            return null;

        var kind = typeEl.TryGetProperty("kind", out var kindEl)
            ? kindEl.GetString() ?? "OBJECT"
            : "OBJECT";

        var type = new GraphQlType
        {
            Name = name,
            Kind = kind,
            Description = ExtractDescription(typeEl)
        };

        // Extract deprecation info
        if (typeEl.TryGetProperty("isDeprecated", out var deprecatedEl) && deprecatedEl.ValueKind == JsonValueKind.True)
        {
            type.IsDeprecated = true;
            if (typeEl.TryGetProperty("deprecationReason", out var deprecationReasonEl))
            {
                type.DeprecationReason = deprecationReasonEl.GetString();
            }
        }

        // Extract fields
        if (typeEl.TryGetProperty("fields", out var fieldsEl) && fieldsEl.ValueKind == JsonValueKind.Array)
        {
            if (fieldsEl.GetArrayLength() > MaxFieldsPerType)
            {
                // Log warning but continue
                return null;
            }

            foreach (var fieldEl in fieldsEl.EnumerateArray())
            {
                if (ExtractField(fieldEl) is { } field)
                    type.Fields.Add(field);
            }
        }

        // Extract enum values
        if (typeEl.TryGetProperty("enumValues", out var enumValuesEl) && enumValuesEl.ValueKind == JsonValueKind.Array)
        {
            if (enumValuesEl.GetArrayLength() > MaxEnumValuesPerType)
            {
                return null;
            }

            type.EnumValues ??= [];
            foreach (var enumValueEl in enumValuesEl.EnumerateArray())
            {
                if (ExtractEnumValue(enumValueEl) is { } enumValue)
                    type.EnumValues.Add(enumValue);
            }
        }

        // Extract input fields
        if (typeEl.TryGetProperty("inputFields", out var inputFieldsEl) && inputFieldsEl.ValueKind == JsonValueKind.Array)
        {
            if (inputFieldsEl.GetArrayLength() > MaxFieldsPerType)
            {
                return null;
            }

            type.InputFields ??= [];
            foreach (var inputFieldEl in inputFieldsEl.EnumerateArray())
            {
                if (ExtractField(inputFieldEl) is { } field)
                    type.InputFields.Add(field);
            }
        }

        // Extract interfaces
        if (typeEl.TryGetProperty("interfaces", out var interfacesEl) && interfacesEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var interfaceEl in interfacesEl.EnumerateArray())
            {
                if (interfaceEl.TryGetProperty("name", out var ifNameEl) && ifNameEl.GetString() is { } ifName)
                    type.Interfaces.Add(ifName);
            }
        }

        // Extract possible types (for interfaces/unions)
        if (typeEl.TryGetProperty("possibleTypes", out var possibleTypesEl) && possibleTypesEl.ValueKind == JsonValueKind.Array)
        {
            type.PossibleTypes ??= [];
            foreach (var possibleTypeEl in possibleTypesEl.EnumerateArray())
            {
                if (possibleTypeEl.TryGetProperty("name", out var ptNameEl) && ptNameEl.GetString() is { } ptName)
                    type.PossibleTypes.Add(ptName);
            }
        }

        return type;
    }

    private static GraphQlField? ExtractField(JsonElement fieldEl)
    {
        if (!fieldEl.TryGetProperty("name", out var nameEl))
            return null;

        var name = nameEl.GetString() ?? "unknown";

        var field = new GraphQlField
        {
            Name = name,
            Description = ExtractDescription(fieldEl),
            Type = ExtractTypeRef(fieldEl, "type")
        };

        // Extract deprecation
        if (fieldEl.TryGetProperty("isDeprecated", out var deprecatedEl) && deprecatedEl.ValueKind == JsonValueKind.True)
        {
            field.IsDeprecated = true;
            if (fieldEl.TryGetProperty("deprecationReason", out var deprecationReasonEl))
            {
                field.DeprecationReason = deprecationReasonEl.GetString();
            }
        }

        // Extract arguments
        if (fieldEl.TryGetProperty("args", out var argsEl) && argsEl.ValueKind == JsonValueKind.Array)
        {
            if (argsEl.GetArrayLength() > MaxArgsPerField)
            {
                return null;
            }

            foreach (var argEl in argsEl.EnumerateArray())
            {
                if (ExtractArgument(argEl) is { } arg)
                    field.Arguments.Add(arg);
            }
        }

        return field;
    }

    private static GraphQlArgument? ExtractArgument(JsonElement argEl)
    {
        if (!argEl.TryGetProperty("name", out var nameEl))
            return null;

        var name = nameEl.GetString() ?? "unknown";

        var arg = new GraphQlArgument
        {
            Name = name,
            Description = ExtractDescription(argEl),
            Type = ExtractTypeRef(argEl, "type"),
            DefaultValue = ExtractDefaultValue(argEl)
        };

        return arg;
    }

    private static GraphQlEnumValue? ExtractEnumValue(JsonElement enumValueEl)
    {
        if (!enumValueEl.TryGetProperty("name", out var nameEl))
            return null;

        var name = nameEl.GetString() ?? "unknown";

        var enumValue = new GraphQlEnumValue
        {
            Name = name,
            Description = ExtractDescription(enumValueEl)
        };

        // Extract deprecation
        if (enumValueEl.TryGetProperty("isDeprecated", out var deprecatedEl) && deprecatedEl.ValueKind == JsonValueKind.True)
        {
            enumValue.IsDeprecated = true;
            if (enumValueEl.TryGetProperty("deprecationReason", out var deprecationReasonEl))
            {
                enumValue.DeprecationReason = deprecationReasonEl.GetString();
            }
        }

        return enumValue;
    }

    private static GraphQlTypeRef? ExtractTypeRef(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var typeEl))
            return null;

        return ExtractTypeRefRecursive(typeEl);
    }

    private static GraphQlTypeRef? ExtractTypeRefRecursive(JsonElement typeEl)
    {
        if (typeEl.ValueKind != JsonValueKind.Object)
            return null;

        if (!typeEl.TryGetProperty("kind", out var kindEl))
            return null;

        var kind = kindEl.GetString() ?? "NAMED";

        var typeRef = new GraphQlTypeRef { Kind = kind };

        if (kind == "NAMED" && typeEl.TryGetProperty("name", out var nameEl))
        {
            typeRef.Name = nameEl.GetString();
        }

        if (typeEl.TryGetProperty("ofType", out var ofTypeEl) && ofTypeEl.ValueKind == JsonValueKind.Object)
        {
            typeRef.OfType = ExtractTypeRefRecursive(ofTypeEl);
        }

        return typeRef;
    }

    private static string? ExtractDescription(JsonElement element)
    {
        if (element.TryGetProperty("description", out var descEl) && descEl.ValueKind == JsonValueKind.String)
        {
            var desc = descEl.GetString();
            return string.IsNullOrEmpty(desc) ? null : desc;
        }
        return null;
    }

    private static string? ExtractDefaultValue(JsonElement element)
    {
        if (element.TryGetProperty("defaultValue", out var defaultEl) && defaultEl.ValueKind != JsonValueKind.Null)
        {
            return defaultEl.GetString();
        }
        return null;
    }

    private static List<GraphQlOperation> ExtractOperations(
        string? queryTypeName,
        string? mutationTypeName,
        string? subscriptionTypeName,
        List<GraphQlType> types)
    {
        var operations = new List<GraphQlOperation>();

        // Extract Query operations
        if (queryTypeName != null && types.FirstOrDefault(t => t.Name == queryTypeName) is { } queryType)
        {
            foreach (var field in queryType.Fields)
            {
                operations.Add(new GraphQlOperation
                {
                    Kind = "query",
                    Name = field.Name,
                    RootType = "Query",
                    RootField = field.Name,
                    Arguments = field.Arguments,
                    ReturnType = field.Type
                });
            }
        }

        // Extract Mutation operations
        if (mutationTypeName != null && types.FirstOrDefault(t => t.Name == mutationTypeName) is { } mutationType)
        {
            foreach (var field in mutationType.Fields)
            {
                operations.Add(new GraphQlOperation
                {
                    Kind = "mutation",
                    Name = field.Name,
                    RootType = "Mutation",
                    RootField = field.Name,
                    Arguments = field.Arguments,
                    ReturnType = field.Type
                });
            }
        }

        // Extract Subscription operations
        if (subscriptionTypeName != null && types.FirstOrDefault(t => t.Name == subscriptionTypeName) is { } subscriptionType)
        {
            foreach (var field in subscriptionType.Fields)
            {
                operations.Add(new GraphQlOperation
                {
                    Kind = "subscription",
                    Name = field.Name,
                    RootType = "Subscription",
                    RootField = field.Name,
                    Arguments = field.Arguments,
                    ReturnType = field.Type
                });
            }
        }

        return operations;
    }
}

public sealed class GraphQlExtractionResult
{
    public bool Success { get; init; }
    public GraphQlNormalizedContract? Contract { get; init; }
    public string? FailureMessage { get; init; }

    public static GraphQlExtractionResult SuccessResult(GraphQlNormalizedContract contract) =>
        new() { Success = true, Contract = contract };

    public static GraphQlExtractionResult Failure(string message) =>
        new() { Success = false, FailureMessage = message };
}
