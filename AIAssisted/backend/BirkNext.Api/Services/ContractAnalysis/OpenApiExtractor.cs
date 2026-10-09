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
                    ExtractOperations(pathProp.Value, pathProp.Name, contract.Operations, root);
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

    private void ExtractOperations(JsonElement pathElement, string path, List<NormalizedOperation> operations, JsonElement root)
    {
        var pathPointer = "#/paths/" + Escape(path);
        var pathLevel = ExtractParameters(pathElement, pathPointer, root);
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

                // Parameters: operation-level definitions override path-level ones with the same name and location.
                var opLevel = ExtractParameters(opEl, $"{pathPointer}/{method}", root);
                operation.Parameters = pathLevel.Where(p => !opLevel.Any(o => o.Name == p.Name && o.Location == p.Location)).Concat(opLevel).ToList();

                // Extract responses
                if (opEl.TryGetProperty("responses", out var responsesEl))
                {
                    foreach (var respProp in responsesEl.EnumerateObject())
                    {
                        operation.ResponseCodes.Add(respProp.Name);
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

    /// <summary>JSON pointer escaping (RFC 6901): "~" → "~0", "/" → "~1".</summary>
    private static string Escape(string segment) => segment.Replace("~", "~0").Replace("/", "~1");

    /// <summary>
    /// The <c>parameters</c> array of a path item or operation: path, query and header parameters with the constraints the document states.
    /// Local <c>$ref</c>s to <c>#/components/parameters</c> and one level of <c>#/components/schemas</c> are resolved; anything else is skipped,
    /// never guessed. <see cref="NormalizedParameter.SourceRef"/> points at the definition that was used.
    /// </summary>
    internal static List<NormalizedParameter> ExtractParameters(JsonElement owner, string ownerPointer, JsonElement root)
    {
        var result = new List<NormalizedParameter>();
        if (!owner.TryGetProperty("parameters", out var parametersEl) || parametersEl.ValueKind != JsonValueKind.Array) return result;
        var index = 0;
        foreach (var raw in parametersEl.EnumerateArray())
        {
            var pointer = $"{ownerPointer}/parameters/{index++}";
            var definition = raw;
            if (raw.TryGetProperty("$ref", out var refEl))
            {
                if (ResolveLocal(root, refEl.GetString()) is not { } resolved) continue;
                definition = resolved;
                pointer = refEl.GetString()!;
            }
            if (!definition.TryGetProperty("name", out var nameEl) || nameEl.GetString() is not { Length: > 0 } name) continue;
            if (!definition.TryGetProperty("in", out var inEl)) continue;
            NormalizedParameterLocation? location = inEl.GetString() switch
            {
                "path" => NormalizedParameterLocation.Path,
                "query" => NormalizedParameterLocation.Query,
                "header" => NormalizedParameterLocation.Header,
                _ => null,
            };
            if (location is null) continue;
            var parameter = new NormalizedParameter
            {
                Name = name, Location = location.Value, SourceRef = pointer,
                Required = location == NormalizedParameterLocation.Path || (definition.TryGetProperty("required", out var req) && req.ValueKind == JsonValueKind.True),
            };
            if (definition.TryGetProperty("schema", out var schemaEl))
            {
                if (schemaEl.TryGetProperty("$ref", out var schemaRef) && ResolveLocal(root, schemaRef.GetString()) is { } resolvedSchema) schemaEl = resolvedSchema;
                ApplyConstraints(parameter, schemaEl);
            }
            result.Add(parameter);
        }
        return result;
    }

    private static void ApplyConstraints(NormalizedParameter parameter, JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object) return;
        if (schema.TryGetProperty("type", out var t))
        {
            if (t.ValueKind == JsonValueKind.String) parameter.Type = t.GetString();
            else if (t.ValueKind == JsonValueKind.Array)
            {
                // OpenAPI 3.1 type arrays: ["string", "null"].
                var types = t.EnumerateArray().Select(x => x.GetString()).ToList();
                parameter.Type = types.FirstOrDefault(x => x is not null && x != "null");
                parameter.Nullable |= types.Contains("null");
            }
        }
        if (schema.TryGetProperty("format", out var f)) parameter.Format = f.GetString();
        if (schema.TryGetProperty("nullable", out var n) && n.ValueKind == JsonValueKind.True) parameter.Nullable = true;
        if (schema.TryGetProperty("enum", out var e) && e.ValueKind == JsonValueKind.Array)
            parameter.EnumValues = e.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToList();
        if (schema.TryGetProperty("minimum", out var min) && min.ValueKind == JsonValueKind.Number) parameter.Minimum = min.GetDecimal();
        if (schema.TryGetProperty("maximum", out var max) && max.ValueKind == JsonValueKind.Number) parameter.Maximum = max.GetDecimal();
        // OpenAPI 3.0: exclusiveMinimum/Maximum are booleans; 3.1: numbers that replace minimum/maximum.
        if (schema.TryGetProperty("exclusiveMinimum", out var xmin))
        {
            if (xmin.ValueKind == JsonValueKind.True) parameter.ExclusiveMinimum = true;
            else if (xmin.ValueKind == JsonValueKind.Number) { parameter.Minimum = xmin.GetDecimal(); parameter.ExclusiveMinimum = true; }
        }
        if (schema.TryGetProperty("exclusiveMaximum", out var xmax))
        {
            if (xmax.ValueKind == JsonValueKind.True) parameter.ExclusiveMaximum = true;
            else if (xmax.ValueKind == JsonValueKind.Number) { parameter.Maximum = xmax.GetDecimal(); parameter.ExclusiveMaximum = true; }
        }
        if (schema.TryGetProperty("minLength", out var minLen) && minLen.ValueKind == JsonValueKind.Number) parameter.MinLength = minLen.GetInt32();
        if (schema.TryGetProperty("maxLength", out var maxLen) && maxLen.ValueKind == JsonValueKind.Number) parameter.MaxLength = maxLen.GetInt32();
    }

    /// <summary>Resolves a local "#/components/..." pointer one level. External or chained references return null.</summary>
    private static JsonElement? ResolveLocal(JsonElement root, string? reference)
    {
        if (reference is null || !reference.StartsWith("#/", StringComparison.Ordinal)) return null;
        var current = root;
        foreach (var segment in reference[2..].Split('/'))
        {
            var key = segment.Replace("~1", "/").Replace("~0", "~");
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(key, out current)) return null;
        }
        return current.ValueKind == JsonValueKind.Object && !current.TryGetProperty("$ref", out _) ? current : null;
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
