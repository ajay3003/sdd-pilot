using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BirkNext.Api.Services.SourceAnalysis.Evidence;
using BirkNext.ExecutionEvidence;

namespace BirkNext.Api.Services.ContractTesting;

public enum ValidationDirection { Request, Response }

/// <summary>A JSON number as decimal, whether the node was parsed (element-backed) or created from a CLR number.</summary>
internal static class JsonNumber
{
    public static decimal Of(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<decimal>(out var d) ? d : decimal.Parse(node!.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture);
}

/// <summary>
/// Validates JSON instances against the Schema Objects of one stored OpenAPI document (the exact version a call references). Implemented here, not
/// through a JSON Schema package: none is referenced by the solution and the cached one needs packages that are not available offline.
/// <para><b>Evaluated</b>: local <c>$ref</c> (any depth, cycle-guarded), <c>type</c> (3.0 string, 3.1 string or array incl. "null"), OpenAPI 3.0
/// <c>nullable</c>, <c>enum</c>, <c>const</c>, <c>required</c>, <c>properties</c>, <c>additionalProperties</c> (boolean or schema), <c>min/maxProperties</c>,
/// <c>items</c>, <c>min/maxItems</c>, <c>uniqueItems</c>, <c>min/maxLength</c>, <c>pattern</c> (bounded time), <c>minimum</c>/<c>maximum</c>,
/// <c>exclusiveMinimum</c>/<c>exclusiveMaximum</c> (3.0 boolean, 3.1 number), <c>multipleOf</c>, <c>allOf</c>, <c>anyOf</c>, <c>oneOf</c>, <c>not</c>,
/// <c>readOnly</c> (not allowed in requests), <c>writeOnly</c> (not allowed in responses), <c>format</c> for date-time, date, uuid, email, int32, int64.</para>
/// <para><b>Reported as unsupported</b> (met, not evaluated): <see cref="Unsupported"/>, external <c>$ref</c>s and other formats.</para>
/// </summary>
public sealed class OpenApiSchemaValidator
{
    public static readonly string[] Unsupported = ["patternProperties", "propertyNames", "if", "then", "else", "dependentRequired", "dependentSchemas", "unevaluatedProperties",
        "unevaluatedItems", "contains", "minContains", "maxContains", "prefixItems", "$dynamicRef", "$recursiveRef", "discriminator"];
    private static readonly string[] Formats = ["date-time", "date", "uuid", "email", "int32", "int64", "float", "double", "byte", "binary", "password"];
    private const int MaxDepth = 64;
    private readonly JsonNode _root;
    public bool IsOpenApi31 { get; }
    public bool IsSwagger2 { get; }

    public OpenApiSchemaValidator(JsonNode root)
    {
        _root = root;
        var version = root["openapi"]?.GetValueKind() == JsonValueKind.String ? root["openapi"]!.GetValue<string>() : null;
        IsOpenApi31 = version?.StartsWith("3.1", StringComparison.Ordinal) == true;
        IsSwagger2 = root["swagger"] is not null;
    }

    /// <summary>The stored document (JSON or YAML) as a JSON tree; YAML scalars become numbers, booleans or null where they read as such.</summary>
    public static JsonNode? Parse(string text)
    {
        if (text.TrimStart().StartsWith('{')) return JsonNode.Parse(text, null, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true, MaxDepth = 128 });
        return MiniYaml.Parse(text) is { } yaml ? FromYaml(yaml, 0) : null;
    }

    private static JsonNode? FromYaml(YamlNode node, int depth)
    {
        if (depth > 128) throw new FormatException("The YAML document is nested too deeply.");
        switch (node.Kind)
        {
            case YamlKind.Map:
                var o = new JsonObject();
                foreach (var (k, v) in node.Entries) o[k] = FromYaml(v, depth + 1);
                return o;
            case YamlKind.Seq:
                var a = new JsonArray();
                foreach (var item in node.Items) a.Add(FromYaml(item, depth + 1));
                return a;
            default:
                var s = node.Value;
                if (s is null or "~" or "null") return null;
                if (s is "true" or "false") return JsonValue.Create(s == "true");
                // Numbers through the JSON parser, so they behave exactly like numbers of a JSON document.
                if (Regex.IsMatch(s, @"^-?(0|[1-9][0-9]*)(\.[0-9]+)?([eE][+-]?[0-9]+)?$")) return JsonNode.Parse(s);
                return JsonValue.Create(s);
        }
    }

    public sealed record SchemaLookup(bool OperationFound, bool StatusDocumented, string? StatusKey, JsonNode? Schema, string? Pointer, string? MediaType, string? Note);

    /// <summary>The request body schema of an operation for a media type (OpenAPI 3 requestBody.content, Swagger 2 body parameter).</summary>
    public SchemaLookup RequestSchema(string method, string pathTemplate, string? mediaType)
    {
        var (op, pointer) = Operation(method, pathTemplate);
        if (op is null) return new(false, false, null, null, null, null, $"{method.ToUpperInvariant()} {pathTemplate} is not in the contract.");
        if (IsSwagger2)
        {
            var body = (op["parameters"] as JsonArray ?? []).Select((p, i) => (Node: Deref(p, out _), Index: i)).FirstOrDefault(p => p.Node?["in"]?.GetValue<string>() == "body");
            return body.Node?["schema"] is { } swaggerSchema ? new(true, true, null, swaggerSchema, $"{pointer}/parameters/{body.Index}/schema", "application/json", null)
                : new(true, true, null, null, null, null, "The operation declares no request body.");
        }
        if (op["requestBody"] is not { } rb) return new(true, true, null, null, null, null, "The operation declares no request body.");
        var requestBody = Deref(rb, out var rbPointer) ?? rb;
        var basePointer = rbPointer ?? $"{pointer}/requestBody";
        var (type, schema) = Content(requestBody["content"] as JsonObject, mediaType);
        return schema is null ? new(true, true, null, null, null, type, type is null ? "No request media type matches." : "The request media type declares no schema.")
            : new(true, true, null, schema, $"{basePointer}/content/{Escape(type!)}/schema", type, null);
    }

    /// <summary>The schema of the documented response for a status (exact code, then 2XX-style range, then default).</summary>
    public SchemaLookup ResponseSchema(string method, string pathTemplate, int status, string? mediaType)
    {
        var (op, pointer) = Operation(method, pathTemplate);
        if (op is null) return new(false, false, null, null, null, null, $"{method.ToUpperInvariant()} {pathTemplate} is not in the contract.");
        if (op["responses"] is not JsonObject responses) return new(true, false, null, null, null, null, "The operation documents no responses.");
        var code = status.ToString(CultureInfo.InvariantCulture);
        var key = responses.ContainsKey(code) ? code : responses.ContainsKey($"{code[0]}XX") ? $"{code[0]}XX" : responses.ContainsKey($"{code[0]}xx") ? $"{code[0]}xx" : responses.ContainsKey("default") ? "default" : null;
        if (key is null) return new(true, false, null, null, null, null, $"HTTP {status} is not a documented response ({string.Join(", ", responses.Select(r => r.Key))}).");
        var response = Deref(responses[key], out var responsePointer) ?? responses[key];
        var basePointer = responsePointer ?? $"{pointer}/responses/{Escape(key)}";
        if (IsSwagger2) return response?["schema"] is { } s2 ? new(true, true, key, s2, $"{basePointer}/schema", "application/json", null) : new(true, true, key, null, null, null, $"Response {key} declares no schema.");
        var (type, schema) = Content(response?["content"] as JsonObject, mediaType);
        return schema is null ? new(true, true, key, null, null, type, $"Response {key} declares no schema for this media type.")
            : new(true, true, key, schema, $"{basePointer}/content/{Escape(type!)}/schema", type, null);
    }

    /// <summary>The schema of one declared parameter (operation-level wins over path-level; Swagger 2 parameters carry their type inline).</summary>
    public (JsonNode Schema, string Pointer)? ParameterSchema(string method, string pathTemplate, string name, string location)
    {
        var (op, pointer) = Operation(method, pathTemplate);
        if (op is null) return null;
        var item = _root["paths"]?[pathTemplate];
        foreach (var (owner, ownerPointer) in new[] { (op as JsonNode, pointer), (item, $"#/paths/{Escape(pathTemplate)}") })
        {
            if (owner?["parameters"] is not JsonArray parameters) continue;
            for (var i = 0; i < parameters.Count; i++)
            {
                var p = Deref(parameters[i], out var refPointer) ?? parameters[i];
                if (p?["name"]?.ToString() != name || p["in"]?.ToString() != location) continue;
                var at = refPointer ?? $"{ownerPointer}/parameters/{i}";
                return p["schema"] is { } schema ? (schema, $"{at}/schema") : IsSwagger2 ? (p, at) : null;
            }
        }
        return null;
    }

    /// <summary>Validates <paramref name="instance"/> against <paramref name="schema"/>. Findings carry the instance path and schema pointer.</summary>
    public List<ContractValidationFinding> Validate(JsonNode? instance, JsonNode schema, string pointer, ValidationDirection direction, ISet<string> unsupported)
    {
        var findings = new List<ContractValidationFinding>();
        Check(instance, schema, pointer, "$", direction, findings, unsupported, 0, []);
        return findings;
    }

    private void Check(JsonNode? value, JsonNode? schemaNode, string pointer, string path, ValidationDirection direction, List<ContractValidationFinding> findings, ISet<string> unsupported,
        int depth, HashSet<string> refs)
    {
        if (schemaNode is null) return;
        if (depth > MaxDepth) { unsupported.Add("schema depth > 64"); return; }
        if (schemaNode.GetValueKind() == JsonValueKind.True) return;
        if (schemaNode.GetValueKind() == JsonValueKind.False) { Add("Schema", "false", "No value is allowed here."); return; }
        if (schemaNode is not JsonObject schema) return;
        void Add(string tier, string keyword, string message) => findings.Add(new(tier, path, $"{pointer}/{keyword}", keyword, message));

        if (schema["$ref"]?.GetValue<string>() is { } reference)
        {
            if (!reference.StartsWith("#/", StringComparison.Ordinal)) { unsupported.Add($"external $ref ({reference})"); return; }
            if (!refs.Add($"{reference}|{path}")) return;   // a cycle on the same instance location
            var target = Resolve(reference);
            if (target is null) { Add("Schema", "$ref", $"Unresolvable reference {reference}."); return; }
            Check(value, target, reference, path, direction, findings, unsupported, depth + 1, refs);
            if (!IsOpenApi31) return;   // 3.0: siblings of $ref are ignored
        }
        foreach (var keyword in Unsupported.Where(schema.ContainsKey)) unsupported.Add(keyword);

        var kind = value?.GetValueKind() ?? JsonValueKind.Null;
        var nullable = schema["nullable"]?.GetValueKind() == JsonValueKind.True;
        var types = schema["type"] switch
        {
            JsonValue v when v.GetValueKind() == JsonValueKind.String => [v.GetValue<string>()],
            JsonArray a => a.Select(t => t?.GetValue<string>() ?? "").ToList(),
            _ => new List<string>(),
        };
        if (kind == JsonValueKind.Null)
        {
            if (types.Count > 0 && !types.Contains("null") && !nullable && !(schema["enum"] is JsonArray en && en.Any(e => e is null)))
                Add("Type", "type", $"null is not allowed (expected {string.Join(" or ", types)}{(IsOpenApi31 ? "" : "; OpenAPI 3.0 needs nullable: true")}).");
            Composition();
            return;
        }
        if (types.Count > 0 && !types.Any(t => Matches(t, value!)))
        { Add("Type", "type", $"Expected {string.Join(" or ", types)}, got {Describe(value!)}."); return; }

        if (schema["enum"] is JsonArray allowed && !allowed.Any(a => JsonNode.DeepEquals(a, value)))
            Add("Schema", "enum", $"{Short(value)} is not one of {string.Join(", ", allowed.Select(Short))}.");
        if (schema.ContainsKey("const") && !JsonNode.DeepEquals(schema["const"], value)) Add("Schema", "const", $"Must be {Short(schema["const"])}.");

        switch (value)
        {
            case JsonObject o:
                if (schema["required"] is JsonArray required)
                    foreach (var name in required.Select(r => r?.GetValue<string>()).Where(n => n is not null && !o.ContainsKey(n)))
                    {
                        var property = schema["properties"]?[name!] is { } pd ? Deref(pd, out _) ?? pd : null;
                        // readOnly properties are not required in requests; writeOnly ones not in responses (OpenAPI semantics).
                        if (direction == ValidationDirection.Request && property?["readOnly"]?.GetValueKind() == JsonValueKind.True) continue;
                        if (direction == ValidationDirection.Response && property?["writeOnly"]?.GetValueKind() == JsonValueKind.True) continue;
                        findings.Add(new("Schema", $"{path}.{name}", $"{pointer}/required", "required", $"Required property {name} is missing."));
                    }
                var declared = schema["properties"] as JsonObject;
                foreach (var (name, child) in o)
                {
                    if (declared?[name] is { } propertySchema)
                    {
                        var resolved = Deref(propertySchema, out _) ?? propertySchema;
                        if (direction == ValidationDirection.Request && resolved["readOnly"]?.GetValueKind() == JsonValueKind.True)
                            findings.Add(new("Schema", $"{path}.{name}", $"{pointer}/properties/{Escape(name)}/readOnly", "readOnly", $"{name} is read-only and must not be sent in a request."));
                        if (direction == ValidationDirection.Response && resolved["writeOnly"]?.GetValueKind() == JsonValueKind.True)
                            findings.Add(new("Schema", $"{path}.{name}", $"{pointer}/properties/{Escape(name)}/writeOnly", "writeOnly", $"{name} is write-only and must not be returned."));
                        Check(child, propertySchema, $"{pointer}/properties/{Escape(name)}", $"{path}.{name}", direction, findings, unsupported, depth + 1, refs);
                    }
                    else if (schema["additionalProperties"] is { } extra)
                    {
                        if (extra.GetValueKind() == JsonValueKind.False) findings.Add(new("Schema", $"{path}.{name}", $"{pointer}/additionalProperties", "additionalProperties", $"{name} is not a declared property and additional properties are not allowed."));
                        else if (extra is JsonObject) Check(child, extra, $"{pointer}/additionalProperties", $"{path}.{name}", direction, findings, unsupported, depth + 1, refs);
                    }
                }
                if (Number(schema["minProperties"]) is { } minP && o.Count < minP) Add("Schema", "minProperties", $"At least {minP} properties.");
                if (Number(schema["maxProperties"]) is { } maxP && o.Count > maxP) Add("Schema", "maxProperties", $"At most {maxP} properties.");
                break;
            case JsonArray a:
                if (schema["items"] is { } items) for (var i = 0; i < a.Count; i++) Check(a[i], items, $"{pointer}/items", $"{path}[{i}]", direction, findings, unsupported, depth + 1, refs);
                if (Number(schema["minItems"]) is { } minI && a.Count < minI) Add("Schema", "minItems", $"At least {minI} items, got {a.Count}.");
                if (Number(schema["maxItems"]) is { } maxI && a.Count > maxI) Add("Schema", "maxItems", $"At most {maxI} items, got {a.Count}.");
                if (schema["uniqueItems"]?.GetValueKind() == JsonValueKind.True && a.Select(x => x?.ToJsonString()).Distinct().Count() != a.Count) Add("Schema", "uniqueItems", "Items must be unique.");
                break;
            case JsonValue v when v.GetValueKind() == JsonValueKind.String:
                var text = v.GetValue<string>();
                var length = new StringInfo(text).LengthInTextElements;
                if (Number(schema["minLength"]) is { } minL && length < minL) Add("Schema", "minLength", $"At least {minL} characters, got {length}.");
                if (Number(schema["maxLength"]) is { } maxL && length > maxL) Add("Schema", "maxLength", $"At most {maxL} characters, got {length}.");
                if (schema["pattern"]?.GetValue<string>() is { } pattern)
                {
                    try { if (!Regex.IsMatch(text, pattern, RegexOptions.None, TimeSpan.FromMilliseconds(100))) Add("Schema", "pattern", $"Does not match {pattern}."); }
                    catch (Exception ex) when (ex is ArgumentException or RegexMatchTimeoutException) { unsupported.Add($"pattern ({pattern})"); }
                }
                if (schema["format"]?.GetValue<string>() is { } format) Format(format, text);
                break;
            case JsonValue v when v.GetValueKind() == JsonValueKind.Number:
                var n = JsonNumber.Of(v);
                var exclusiveMin = schema["exclusiveMinimum"];
                var exclusiveMax = schema["exclusiveMaximum"];
                if (Number(schema["minimum"]) is { } min && (exclusiveMin?.GetValueKind() == JsonValueKind.True ? n <= min : n < min)) Add("Schema", "minimum", $"{n} is below the minimum {min}{(exclusiveMin?.GetValueKind() == JsonValueKind.True ? " (exclusive)" : "")}.");
                if (Number(schema["maximum"]) is { } max && (exclusiveMax?.GetValueKind() == JsonValueKind.True ? n >= max : n > max)) Add("Schema", "maximum", $"{n} is above the maximum {max}{(exclusiveMax?.GetValueKind() == JsonValueKind.True ? " (exclusive)" : "")}.");
                if (Number(exclusiveMin) is { } xmin && n <= xmin) Add("Schema", "exclusiveMinimum", $"{n} must be greater than {xmin}.");
                if (Number(exclusiveMax) is { } xmax && n >= xmax) Add("Schema", "exclusiveMaximum", $"{n} must be less than {xmax}.");
                if (Number(schema["multipleOf"]) is { } step && step > 0 && n % step != 0) Add("Schema", "multipleOf", $"{n} is not a multiple of {step}.");
                if (schema["format"]?.GetValue<string>() is "int32" && (n % 1 != 0 || n < int.MinValue || n > int.MaxValue)) Add("Type", "format", $"{n} is not a 32-bit integer.");
                if (schema["format"]?.GetValue<string>() is "int64" && (n % 1 != 0 || n < long.MinValue || n > long.MaxValue)) Add("Type", "format", $"{n} is not a 64-bit integer.");
                break;
        }
        Composition();

        void Composition()
        {
            if (schema["allOf"] is JsonArray all) for (var i = 0; i < all.Count; i++) Check(value, all[i], $"{pointer}/allOf/{i}", path, direction, findings, unsupported, depth + 1, refs);
            if (schema["anyOf"] is JsonArray any && !any.Select((s, i) => Probe(value, s, $"{pointer}/anyOf/{i}", path, direction, unsupported, depth, refs)).Any(f => f.Count == 0))
                Add("Schema", "anyOf", $"Matches none of the {any.Count} anyOf alternatives.");
            if (schema["oneOf"] is JsonArray one)
            {
                var matches = one.Select((s, i) => Probe(value, s, $"{pointer}/oneOf/{i}", path, direction, unsupported, depth, refs)).Count(f => f.Count == 0);
                if (matches != 1) Add("Schema", "oneOf", matches == 0 ? $"Matches none of the {one.Count} oneOf alternatives." : $"Matches {matches} oneOf alternatives; exactly one is allowed.");
            }
            if (schema["not"] is { } not && Probe(value, not, $"{pointer}/not", path, direction, unsupported, depth, refs).Count == 0) Add("Schema", "not", "Matches the schema under not.");
        }

        void Format(string format, string text)
        {
            var ok = format switch
            {
                "date-time" => DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _) && text.Contains('T'),
                "date" => DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
                "uuid" => Guid.TryParseExact(text, "D", out _),
                "email" => Regex.IsMatch(text, @"^[^@\s]+@[^@\s]+\.[^@\s]+$"),
                _ => true,
            };
            if (!Formats.Contains(format)) unsupported.Add($"format {format}");
            else if (!ok) Add("Type", "format", $"Not a valid {format}.");
        }
    }

    private List<ContractValidationFinding> Probe(JsonNode? value, JsonNode? schema, string pointer, string path, ValidationDirection direction, ISet<string> unsupported, int depth, HashSet<string> refs)
    {
        var findings = new List<ContractValidationFinding>();
        Check(value, schema, pointer, path, direction, findings, unsupported, depth + 1, [.. refs]);
        return findings;
    }

    private static bool Matches(string type, JsonNode value) => (type, value.GetValueKind()) switch
    {
        ("object", JsonValueKind.Object) or ("array", JsonValueKind.Array) or ("string", JsonValueKind.String) or ("number", JsonValueKind.Number) => true,
        ("boolean", JsonValueKind.True or JsonValueKind.False) => true,
        ("integer", JsonValueKind.Number) => JsonNumber.Of(value) % 1 == 0,
        _ => false,
    };

    private static string Describe(JsonNode value) => value.GetValueKind() switch
    {
        JsonValueKind.Number => JsonNumber.Of(value) % 1 == 0 ? "integer" : "number",
        JsonValueKind.True or JsonValueKind.False => "boolean",
        var k => k.ToString().ToLowerInvariant(),
    };

    private static string Short(JsonNode? n) { var t = n?.ToJsonString() ?? "null"; return t.Length > 60 ? t[..60] + "…" : t; }
    private static decimal? Number(JsonNode? n) => n?.GetValueKind() == JsonValueKind.Number ? JsonNumber.Of(n) : null;
    private static string Escape(string s) => s.Replace("~", "~0").Replace("/", "~1");

    private (JsonObject? Operation, string Pointer) Operation(string method, string pathTemplate)
    {
        var pointer = $"#/paths/{Escape(pathTemplate)}/{method.ToLowerInvariant()}";
        var item = _root["paths"]?[pathTemplate];
        if (item is JsonObject pathItem && pathItem["$ref"] is not null) item = Deref(pathItem, out _);
        return (item?[method.ToLowerInvariant()] as JsonObject, pointer);
    }

    private static (string? Type, JsonNode? Schema) Content(JsonObject? content, string? mediaType)
    {
        if (content is null || content.Count == 0) return (null, null);
        var wanted = (mediaType ?? "").Split(';')[0].Trim().ToLowerInvariant();
        var pick = content.FirstOrDefault(c => c.Key.Equals(wanted, StringComparison.OrdinalIgnoreCase));
        if (pick.Key is null && wanted.Length > 0)
            pick = content.FirstOrDefault(c => c.Key.EndsWith("/*", StringComparison.Ordinal) && wanted.StartsWith(c.Key[..^1], StringComparison.OrdinalIgnoreCase) || c.Key == "*/*"
                || (wanted.Contains("json", StringComparison.Ordinal) && c.Key.Contains("json", StringComparison.OrdinalIgnoreCase)));
        if (pick.Key is null && wanted.Length == 0) pick = content.FirstOrDefault(c => c.Key.Contains("json", StringComparison.OrdinalIgnoreCase));
        return pick.Key is null ? (null, null) : (pick.Key, pick.Value?["schema"]);
    }

    private JsonNode? Deref(JsonNode? node, out string? pointer)
    {
        pointer = null;
        for (var hops = 0; hops < 16 && node?["$ref"]?.GetValue<string>() is { } reference; hops++)
        {
            pointer = reference;
            node = reference.StartsWith("#/", StringComparison.Ordinal) ? Resolve(reference) : null;
        }
        return node;
    }

    private JsonNode? Resolve(string reference)
    {
        JsonNode? node = _root;
        foreach (var part in reference[2..].Split('/')) node = node is JsonObject o ? o[part.Replace("~1", "/").Replace("~0", "~")] : null;
        return node;
    }
}
