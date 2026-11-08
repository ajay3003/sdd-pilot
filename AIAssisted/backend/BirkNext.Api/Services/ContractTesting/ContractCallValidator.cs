using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BirkNext.Api.Services.ExecutionEvidence;
using BirkNext.ContractTesting;
using BirkNext.ExecutionEvidence;
using BirkNext.SourceDomains;

namespace BirkNext.Api.Services.ContractTesting;

/// <summary>
/// Contract checks of one execution call against the exact contract version in its <see cref="ExecutionCallDefinition.OperationRef"/>
/// ("key@vN#operation"). REST: query/path/header parameters and the JSON request body before sending; status documentation, media type and the
/// JSON response body after. GraphQL: document + variables before sending; data shape after. Findings keep their tier (Syntax, Type, Schema,
/// Operation); keywords that are not evaluated are listed, and a report with unevaluated keywords never counts as a full pass.
/// </summary>
public sealed partial class ContractCallValidator(ContractRegistryService registry) : IExecutionContractValidator
{
    [GeneratedRegex(@"\{([^}/]+)\}")] private static partial Regex TemplateParameter();

    public async Task<ContractValidationReport?> RequestAsync(ExecutionCallDefinition d, CancellationToken ct)
    {
        if (await LoadAsync(d, ct) is not { } loaded) return null;
        var (doc, version, operation, contractRef, validator) = loaded;
        if (d.Protocol == ExecutionProtocol.GraphQl)
        {
            var (gqlFindings, gqlUnsupported) = GraphQlContractValidator.ValidateRequest(doc, d.GraphQlQuery, d.GraphQlVariablesJson);
            return Report("Request", contractRef, $"{contractRef}#{operation}", gqlFindings, gqlUnsupported, "document and variables");
        }
        if (validator is null) return Structural("Request", doc, contractRef, doc.Operations.FirstOrDefault(o => o.Name == operation)?.RequestSchema, d.Body, d.Body is null);
        var findings = new List<ContractValidationFinding>();
        var unsupported = new HashSet<string>(StringComparer.Ordinal);
        var (method, template) = Split(operation);
        // Parameters (query, header, path) against their schemas; values arrive as text, so numbers and booleans are read before checking.
        var op = doc.Operations.FirstOrDefault(o => o.Name == operation);
        var pathValues = PathValues(template, d.Path);
        foreach (var p in op?.ParameterDetails ?? [])
        {
            string? raw = p.In switch
            {
                "query" => d.Query.TryGetValue(p.Name, out var q) ? q : null,
                "header" => d.Headers.FirstOrDefault(h => h.Key.Equals(p.Name, StringComparison.OrdinalIgnoreCase)).Value,
                "path" => pathValues.TryGetValue(p.Name, out var v) ? Uri.UnescapeDataString(v) : null,
                _ => null,
            };
            if (raw is null)
            {
                if (p.Required) findings.Add(new("Operation", $"$.{p.In}.{p.Name}", "", "required", $"Required {p.In} parameter {p.Name} is missing."));
                continue;
            }
            if (ParameterSchema(validator, method, template, p.Name, p.In) is { } ps)
                findings.AddRange(validator.Validate(Typed(raw, ps.Schema), ps.Schema, ps.Pointer, ValidationDirection.Request, unsupported)
                    .Select(f => f with { InstancePath = f.InstancePath.Replace("$", $"$.{p.In}.{p.Name}") }));
        }
        var lookup = validator.RequestSchema(method, template, d.ContentType);
        if (!lookup.OperationFound) findings.Add(new("Operation", "$", "", "operation", lookup.Note!));
        else if (d.Body is null)
        {
            if (op?.RequestBodyRequired == true) findings.Add(new("Operation", "$.body", lookup.Pointer ?? "", "required", "The operation requires a request body."));
        }
        else if (lookup.Schema is not null)
        {
            if (!(d.ContentType ?? "").Contains("json", StringComparison.OrdinalIgnoreCase)) unsupported.Add($"request media type {d.ContentType}");
            else
            {
                JsonNode? body = null;
                try { body = JsonNode.Parse(d.Body); } catch (JsonException ex) { findings.Add(new("Syntax", "$.body", "", "json", $"The request body is not JSON ({ex.Message.Split('.')[0]}).")); }
                if (findings.All(f => f.Tier != "Syntax"))
                    findings.AddRange(validator.Validate(body, lookup.Schema, lookup.Pointer!, ValidationDirection.Request, unsupported).Select(f => f with { InstancePath = "$.body" + f.InstancePath[1..] }));
            }
        }
        else if (op?.RequestContentTypes.Count > 0 && d.ContentType is { } media && !op.RequestContentTypes.Any(t => t.Split(';')[0].Trim().Equals(media.Split(';')[0].Trim(), StringComparison.OrdinalIgnoreCase)))
            findings.Add(new("Operation", "$.contentType", "", "content", $"Media type {media} is not accepted ({string.Join(", ", op.RequestContentTypes)})."));
        return Report("Request", contractRef, lookup.Pointer, findings, unsupported, "parameters and body");
    }

    public async Task<ContractValidationReport?> ResponseAsync(ExecutionCallDefinition d, CapturedResponse response, byte[]? raw, CancellationToken ct)
    {
        if (await LoadAsync(d, ct) is not { } loaded) return null;
        var (doc, _, operation, contractRef, validator) = loaded;
        var text = raw is null ? "" : Encoding.UTF8.GetString(raw);
        JsonNode? json = null;
        var parsed = true;
        if (text.Trim().Length > 0) { try { json = JsonNode.Parse(text); } catch (JsonException) { parsed = false; } }
        if (d.Protocol == ExecutionProtocol.GraphQl)
        {
            if (!parsed) return Report("Response", contractRef, null, [new("Syntax", "$", "", "json", "The GraphQL response is not JSON.")], [], "data shape");
            // Depth-capped parts are "not evaluated", never violations.
            var gql = GraphQlContractValidator.ValidateResponse(doc, d.GraphQlQuery, json);
            return Report("Response", contractRef, $"{contractRef}#{operation}", [.. gql.Where(f => f.Tier != "Unsupported")],
                gql.Where(f => f.Tier == "Unsupported").Select(f => $"response nesting > {GraphQlContractValidator.MaxNesting * 2}"), "data shape");
        }
        if (validator is null)
        {
            var op = doc.Operations.FirstOrDefault(o => o.Name == operation);
            var code = response.StatusCode.ToString(CultureInfo.InvariantCulture);
            if (op is not null && op.ResponseCodes.Count > 0 && !op.ResponseCodes.Contains(code) && !op.ResponseCodes.Contains($"{code[0]}XX") && !op.ResponseCodes.Contains("default"))
                return Report("Response", contractRef, null, [new("Operation", "$.status", "", "responses", $"HTTP {code} is not a documented response ({string.Join(", ", op.ResponseCodes)}).")], [], "status");
            return Structural("Response", doc, contractRef, op?.ResponseSchemas.TryGetValue(code, out var schemaName) == true ? schemaName : null, text, text.Trim().Length == 0);
        }
        var (method, template) = Split(operation);
        var contentType = response.Headers.FirstOrDefault(h => h.Name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))?.Value;
        var lookup = validator.ResponseSchema(method, template, response.StatusCode, contentType);
        var findings = new List<ContractValidationFinding>();
        var unsupported = new HashSet<string>(StringComparer.Ordinal);
        if (!lookup.OperationFound) findings.Add(new("Operation", "$", "", "operation", lookup.Note!));
        else if (!lookup.StatusDocumented) findings.Add(new("Operation", "$.status", "", "responses", lookup.Note!));
        else if (lookup.Schema is null)
            return new("Response", ContractValidationState.NotAssessed, contractRef, null, $"HTTP {response.StatusCode} is documented ({lookup.StatusKey}); {lookup.Note}", [], []);
        else if (text.Trim().Length == 0) findings.Add(new("Schema", "$", lookup.Pointer!, "body", $"Response {lookup.StatusKey} documents a body, but the response is empty."));
        else if (!parsed) findings.Add(new("Syntax", "$", lookup.Pointer!, "json", "The response body is not valid JSON."));
        else findings.AddRange(validator.Validate(json, lookup.Schema, lookup.Pointer!, ValidationDirection.Response, unsupported));
        return Report("Response", contractRef, lookup.Pointer, findings, unsupported, lookup.StatusKey is null ? "status" : $"response {lookup.StatusKey}");
    }

    /// <summary>
    /// Structural validation for versions without a stored document (imported from a Source Analysis snapshot): required properties and the
    /// primitive / nested / list types recorded on the contract's types. Constraints, enums and composition are not available and are listed as
    /// not evaluated, so such a check never counts as a full pass.
    /// </summary>
    private static ContractValidationReport Structural(string direction, SourceContract doc, string contractRef, string? typeName, string? body, bool empty)
    {
        const string limitation = "constraints, enums and composition (source-backed version without the document text)";
        if (typeName is null) return new(direction, ContractValidationState.NotAssessed, contractRef, null, $"The contract records no {direction.ToLowerInvariant()} schema for this case.", [], [limitation]);
        if (empty) return Report(direction, contractRef, typeName, [new("Schema", "$", typeName, "body", $"A {typeName} body is documented, but the {direction.ToLowerInvariant()} has none.")], [limitation], "structure");
        JsonNode? json;
        try { json = JsonNode.Parse(body!); }
        catch (JsonException) { return Report(direction, contractRef, typeName, [new("Syntax", "$", typeName, "json", "The body is not valid JSON.")], [limitation], "structure"); }
        var findings = new List<ContractValidationFinding>();
        Shape(json, typeName, "$", doc, findings, 0);
        return Report(direction, contractRef, typeName, findings, [limitation], "structure");
    }

    private static void Shape(JsonNode? value, string type, string path, SourceContract doc, List<ContractValidationFinding> findings, int depth)
    {
        if (depth > 32 || value is null) return;
        var t = type.Trim();
        if (t.StartsWith('['))
        {
            if (value is not JsonArray list) { findings.Add(new("Type", path, type, "type", $"Expected a list of {t.Trim('[', ']')}.")); return; }
            for (var i = 0; i < list.Count; i++) Shape(list[i], t.Trim('[', ']'), $"{path}[{i}]", doc, findings, depth + 1);
            return;
        }
        var primitive = t.Split(':')[0];
        var kind = value.GetValueKind();
        var mismatch = primitive switch
        {
            "string" => kind != JsonValueKind.String,
            "integer" => kind != JsonValueKind.Number || JsonNumber.Of(value) % 1 != 0,
            "number" => kind != JsonValueKind.Number,
            "boolean" => kind is not (JsonValueKind.True or JsonValueKind.False),
            "array" => kind != JsonValueKind.Array,
            _ => false,
        };
        if (mismatch) { findings.Add(new("Type", path, type, "type", $"Expected {primitive}, got {kind.ToString().ToLowerInvariant()}.")); return; }
        if (doc.Types.FirstOrDefault(x => x.Name == t) is not { } shape) return;
        if (value is not JsonObject o) { findings.Add(new("Type", path, t, "type", $"Expected an object ({t}).")); return; }
        foreach (var field in shape.Fields)
        {
            if (!o.TryGetPropertyValue(field.Name, out var child)) { if (field.Required) findings.Add(new("Schema", $"{path}.{field.Name}", $"{t}.{field.Name}", "required", $"Required property {field.Name} is missing.")); continue; }
            Shape(child, field.Type, $"{path}.{field.Name}", doc, findings, depth + 1);
        }
    }

    private static ContractValidationReport Report(string direction, string contractRef, string? schemaRef, List<ContractValidationFinding> findings, IEnumerable<string> unsupported, string what)
    {
        var notEvaluated = unsupported.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var state = findings.Count > 0 ? ContractValidationState.Invalid : ContractValidationState.Valid;
        var summary = findings.Count > 0 ? $"{findings.Count} contract violation(s) in the {direction.ToLowerInvariant()} ({what})."
            : notEvaluated.Count > 0 ? $"No violation in the evaluated keywords of the {what}; not evaluated: {string.Join(", ", notEvaluated)}."
            : $"The {direction.ToLowerInvariant()} matches the contract ({what}).";
        return new(direction, state, contractRef, schemaRef, summary, [.. findings.Take(200)], notEvaluated);
    }

    private sealed record Loaded(SourceContract Document, int Version, string Operation, string ContractRef, OpenApiSchemaValidator? Validator);

    private async Task<Loaded?> LoadAsync(ExecutionCallDefinition d, CancellationToken ct)
    {
        if (d.OperationRef is not { } reference) return null;
        var hash = reference.IndexOf('#');
        if (hash < 0) return null;
        var contractRef = reference[..hash];
        var operation = reference[(hash + 1)..];
        var at = contractRef.LastIndexOf("@v", StringComparison.Ordinal);
        if (at < 0 || !int.TryParse(contractRef[(at + 2)..], NumberStyles.None, CultureInfo.InvariantCulture, out var version)) return null;
        if (await registry.RawAsync(contractRef[..at], version, ct) is not { } raw) return null;
        OpenApiSchemaValidator? validator = null;
        if (raw.Document.Type == SourceContractType.OpenApi && raw.Text is { Length: > 0 } text)
            try { validator = OpenApiSchemaValidator.Parse(text) is { } root ? new OpenApiSchemaValidator(root) : null; } catch (Exception ex) when (ex is JsonException or FormatException) { }
        return new(raw.Document, version, operation, contractRef, validator);
    }

    private static (string Method, string Template) Split(string operation) => operation.IndexOf(' ') is var i and > 0 ? (operation[..i], operation[(i + 1)..]) : (operation, "");

    private static Dictionary<string, string> PathValues(string template, string actual)
    {
        var names = TemplateParameter().Matches(template).Select(m => m.Groups[1].Value).ToList();
        var pattern = "(?:^|/)" + string.Concat(TemplateParameter().Split(template).Select((part, i) => i % 2 == 0 ? Regex.Escape(part) : "([^/]+)")).TrimStart('/') + "$";
        var match = Regex.Match(actual.Split('?')[0], pattern);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!match.Success) return values;
        for (var i = 0; i < names.Count && i + 1 < match.Groups.Count; i++) values[names[i]] = match.Groups[i + 1].Value;
        return values;
    }

    private static (JsonNode Schema, string Pointer)? ParameterSchema(OpenApiSchemaValidator validator, string method, string template, string name, string location) =>
        validator.ParameterSchema(method, template, name, location);

    /// <summary>A parameter's text read as the type its schema declares (number, integer, boolean), otherwise as a string.</summary>
    private static JsonNode? Typed(string raw, JsonNode schema)
    {
        var type = schema["type"] is JsonValue t && t.GetValueKind() == JsonValueKind.String ? t.GetValue<string>() : schema["type"] is JsonArray a ? a.FirstOrDefault(x => x?.ToString() != "null")?.ToString() : null;
        if (type is "integer" or "number" && Regex.IsMatch(raw, @"^-?(0|[1-9][0-9]*)(\.[0-9]+)?([eE][+-]?[0-9]+)?$")) return JsonNode.Parse(raw);
        if (type == "boolean" && raw is "true" or "false") return JsonNode.Parse(raw);
        return JsonValue.Create(raw);
    }
}
