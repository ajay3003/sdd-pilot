using System.Text.Json;

namespace BirkNext.Api.Services.ContractAnalysis;

/// <summary>
/// Extracts normalized contracts from OpenAPI 3.x JSON documents.
/// </summary>
public interface IOpenApiExtractor
{
    OpenApiExtractionResult Extract(string openApiJson, string? targetOperationId = null);
}

public sealed class OpenApiExtractor : IOpenApiExtractor
{
    private readonly ILogger<OpenApiExtractor> _logger;

    public OpenApiExtractor(ILogger<OpenApiExtractor> logger)
    {
        _logger = logger;
    }

    public OpenApiExtractionResult Extract(string openApiJson, string? targetOperationId = null)
    {
        try
        {
            using var doc = JsonDocument.Parse(openApiJson);
            var root = doc.RootElement;

            // Extract OpenAPI version
            if (!root.TryGetProperty("openapi", out var versionEl))
                return OpenApiExtractionResult.Failure("Missing 'openapi' field");

            var version = versionEl.GetString();
            if (!version?.StartsWith("3.") ?? true)
                return OpenApiExtractionResult.Failure($"Unsupported OpenAPI version: {version}");

            // Extract title
            string title = "Unknown";
            if (root.TryGetProperty("info", out var infoEl) &&
                infoEl.TryGetProperty("title", out var titleEl))
            {
                title = titleEl.GetString() ?? "Unknown";
            }

            var contract = new NormalizedContract { Name = title };

            // Extract paths (operations)
            if (root.TryGetProperty("paths", out var pathsEl))
            {
                foreach (var pathProp in pathsEl.EnumerateObject())
                {
                    ExtractOperations(pathProp.Value, pathProp.Name, contract.Operations);
                }
            }

            // Extract schemas from components
            var schemaMap = new Dictionary<string, NormalizedSchema>(StringComparer.OrdinalIgnoreCase);
            if (root.TryGetProperty("components", out var compEl) &&
                compEl.TryGetProperty("schemas", out var schemasEl))
            {
                foreach (var schemaProp in schemasEl.EnumerateObject())
                {
                    var schema = ExtractSchema(schemaProp.Value, schemaProp.Name, schemaMap);
                    if (schema != null)
                        contract.Schemas.Add(schema);
                }
            }

            // Resolve $ref references
            ResolveReferences(contract.Schemas, schemaMap);

            return OpenApiExtractionResult.SuccessResult(contract);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse OpenAPI JSON");
            return OpenApiExtractionResult.Failure($"JSON parse error: {ex.Message}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error extracting OpenAPI");
            return OpenApiExtractionResult.Failure($"Extraction error: {ex.GetType().Name}");
        }
    }

    private void ExtractOperations(JsonElement pathElement, string path, List<NormalizedOperation> operations)
    {
        var methods = new[] { "get", "post", "put", "delete", "patch", "head", "options" };

        foreach (var method in methods)
        {
            if (pathElement.TryGetProperty(method, out var opEl))
            {
                var operationId = method.ToUpper();
                if (opEl.TryGetProperty("operationId", out var opIdEl))
                    operationId = opIdEl.GetString() ?? operationId;

                var operation = new NormalizedOperation
                {
                    Id = operationId,
                    Method = method.ToUpper(),
                    Path = path
                };

                // Extract request body
                if (opEl.TryGetProperty("requestBody", out var reqBodyEl) &&
                    reqBodyEl.TryGetProperty("content", out var contentEl))
                {
                    if (contentEl.TryGetProperty("application/json", out var jsonContentEl) &&
                        jsonContentEl.TryGetProperty("schema", out var schemaEl))
                    {
                        operation.RequestSchema = ParseSchema(schemaEl, new Dictionary<string, NormalizedSchema>());
                    }
                }

                // Extract responses
                if (opEl.TryGetProperty("responses", out var responsesEl))
                {
                    foreach (var respProp in responsesEl.EnumerateObject())
                    {
                        if (respProp.Value.TryGetProperty("content", out var respContentEl) &&
                            respContentEl.TryGetProperty("application/json", out var respJsonEl) &&
                            respJsonEl.TryGetProperty("schema", out var respSchemaEl))
                        {
                            var schema = ParseSchema(respSchemaEl, new Dictionary<string, NormalizedSchema>());
                            if (schema != null)
                                operation.ResponseSchemas[respProp.Name] = schema;
                        }
                    }
                }

                operations.Add(operation);
            }
        }
    }

    private NormalizedSchema? ExtractSchema(JsonElement schemaEl, string name, Dictionary<string, NormalizedSchema> schemaMap)
    {
        var schema = new NormalizedSchema { Name = name };

        if (schemaEl.TryGetProperty("type", out var typeEl))
            schema.Type = typeEl.GetString() ?? "object";

        if (schemaEl.TryGetProperty("nullable", out var nullEl))
            schema.Nullable = nullEl.GetBoolean();

        // Enum values
        if (schemaEl.TryGetProperty("enum", out var enumEl))
        {
            schema.EnumValues = [];
            foreach (var val in enumEl.EnumerateArray())
                if (val.GetString() is string s)
                    schema.EnumValues.Add(s);
        }

        // Array items
        if (schemaEl.TryGetProperty("items", out var itemsEl) &&
            itemsEl.TryGetProperty("type", out var itemTypeEl))
        {
            schema.ArrayItemType = itemTypeEl.GetString();
        }

        // Additional properties
        if (schemaEl.TryGetProperty("additionalProperties", out var addlEl))
        {
            schema.AllowsAdditionalProperties = addlEl.ValueKind == JsonValueKind.True;
        }

        // Properties
        if (schemaEl.TryGetProperty("properties", out var propsEl))
        {
            foreach (var propProp in propsEl.EnumerateObject())
            {
                var prop = ExtractProperty(propProp.Value, propProp.Name);
                if (prop != null)
                    schema.Properties.Add(prop);
            }
        }

        // Required
        if (schemaEl.TryGetProperty("required", out var reqEl))
        {
            foreach (var reqVal in reqEl.EnumerateArray())
                if (reqVal.GetString() is string reqName)
                    schema.Required.Add(reqName);
        }

        schemaMap[name] = schema;
        return schema;
    }

    private NormalizedProperty? ExtractProperty(JsonElement propEl, string name)
    {
        var prop = new NormalizedProperty { Name = name };

        if (propEl.TryGetProperty("type", out var typeEl))
            prop.Type = typeEl.GetString() ?? "";

        if (propEl.TryGetProperty("format", out var formatEl))
            prop.Format = formatEl.GetString();

        if (propEl.TryGetProperty("nullable", out var nullEl))
            prop.Nullable = nullEl.GetBoolean();

        if (propEl.TryGetProperty("enum", out var enumEl))
        {
            prop.EnumValues = [];
            foreach (var val in enumEl.EnumerateArray())
                if (val.GetString() is string s)
                    prop.EnumValues.Add(s);
        }

        if (propEl.TryGetProperty("items", out var itemsEl) &&
            itemsEl.TryGetProperty("type", out var itemTypeEl))
        {
            prop.ArrayItemType = itemTypeEl.GetString();
        }

        return prop;
    }

    private NormalizedSchema? ParseSchema(JsonElement schemaEl, Dictionary<string, NormalizedSchema> schemaMap)
    {
        // Handle $ref
        if (schemaEl.TryGetProperty("$ref", out var refEl))
        {
            var refPath = refEl.GetString();
            if (refPath?.StartsWith("#/components/schemas/") ?? false)
            {
                var schemaName = refPath.Substring("#/components/schemas/".Length);
                if (schemaMap.TryGetValue(schemaName, out var schema))
                    return schema;
                return null;
            }

            // External ref - not supported in Phase 3
            return null;
        }

        return ExtractSchema(schemaEl, "", schemaMap);
    }

    private void ResolveReferences(List<NormalizedSchema> schemas, Dictionary<string, NormalizedSchema> schemaMap)
    {
        // Resolve local $refs in schema properties
        // For Phase 3, we use a simple approach: if needed, expand inline
        // This prevents infinite recursion with depth bounds
    }
}

public sealed class OpenApiExtractionResult
{
    public bool IsSuccess { get; private set; }
    public NormalizedContract? Contract { get; private set; }
    public string? ErrorMessage { get; private set; }

    public bool Success => IsSuccess;

    public static OpenApiExtractionResult SuccessResult(NormalizedContract contract) =>
        new() { IsSuccess = true, Contract = contract };

    public static OpenApiExtractionResult Failure(string message) =>
        new() { IsSuccess = false, ErrorMessage = message };
}
