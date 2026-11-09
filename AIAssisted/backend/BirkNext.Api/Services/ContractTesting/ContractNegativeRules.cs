using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using BirkNext.ContractTesting;
using BirkNext.ExecutionEvidence;
using BirkNext.SourceDomains;

namespace BirkNext.Api.Services.ContractTesting;

/// <summary>
/// Negative test proposals derived from the contract, one changed input per proposal, made only when the tester asks and never run automatically.
/// The invalid value is a literal in the template (never Test Data). The expected outcome is set only from the contract: REST proposals expect the
/// documented client-error codes that fit the variant (400/422 validation, 401/403 authentication, 405 method, 415 media type); when the contract
/// documents none of them, the proposal has no assertion and says it needs configuration — business errors are never inferred. GraphQL validation
/// errors are defined by the GraphQL specification, so GraphQL proposals expect GraphQL errors (HTTP 200 or 400).
/// </summary>
public static class ContractNegativeRules
{
    public const int MaxProposals = 16;
    private static readonly string[] SafeMethods = ["GET", "HEAD"];

    private sealed record Constraint(string? Type, List<string> Enum, decimal? Minimum, decimal? Maximum, int? MinLength, int? MaxLength, bool Nullable);

    public static string Label(ContractTestKind kind) => kind switch
    {
        ContractTestKind.Positive => "Positive",
        ContractTestKind.MissingRequiredInput => "Negative: missing required value",
        ContractTestKind.InvalidType => "Negative: wrong type",
        ContractTestKind.InvalidEnum => "Negative: value outside the enum",
        ContractTestKind.NullNotAllowed => "Negative: null where not allowed",
        ContractTestKind.EmptyValue => "Negative: empty value",
        ContractTestKind.BoundaryViolation => "Negative: boundary violation",
        ContractTestKind.InvalidContentType => "Negative: wrong content type",
        ContractTestKind.InvalidShape => "Negative: wrong body shape",
        ContractTestKind.MissingAuthentication => "Negative: without authentication",
        ContractTestKind.UnsupportedMethod => "Negative: unsupported method",
        ContractTestKind.UnknownInputField => "Negative: unknown input field",
        ContractTestKind.InvalidListShape => "Negative: list expected",
        ContractTestKind.InvalidSelection => "Negative: invalid field selection",
        _ => kind.ToString(),
    };

    /// <summary>Proposals for one operation, from its positive draft (whose template holds the placeholders the variants replace).</summary>
    public static List<ContractTestDefinition> Propose(SourceContract doc, OpenApiSchemaValidator? raw, ContractTestDefinition positive, ContractOperation op, DateTimeOffset now) =>
        [.. (positive.Template.Protocol == ExecutionProtocol.GraphQl ? GraphQl(doc, positive, op, now) : Rest(doc, raw, positive, op, now)).Take(MaxProposals)];

    private static IEnumerable<ContractTestDefinition> Rest(SourceContract doc, OpenApiSchemaValidator? raw, ContractTestDefinition positive, ContractOperation op, DateTimeOffset now)
    {
        var (method, template) = (op.Kind.ToUpperInvariant(), op.Path ?? "");
        var bodySchema = raw?.Resolve(raw.RequestSchema(method, template, positive.Template.ContentType).Schema);
        Constraint Of(ContractTestInput input)
        {
            JsonObject? schema = input.In == "body" ? raw?.Resolve(bodySchema?["properties"]?[input.Name]) : raw?.Resolve(raw.ParameterSchema(method, template, input.Name, input.In)?.Schema);
            var p = op.ParameterDetails.FirstOrDefault(x => x.Name == input.Name && x.In == input.In);
            var type = schema?["type"] is JsonValue t && t.GetValueKind() == JsonValueKind.String ? t.GetValue<string>() : (input.Type ?? p?.Type)?.Split(':')[0];
            return new(type, schema?["enum"] is JsonArray e ? [.. e.Select(x => x?.ToString() ?? "")] : p?.Enum ?? [],
                Num(schema?["minimum"]), Num(schema?["maximum"]), (int?)Num(schema?["minLength"]), (int?)Num(schema?["maxLength"]),
                schema?["nullable"]?.GetValueKind() == JsonValueKind.True || schema?["type"] is JsonArray ta && ta.Any(x => x?.ToString() == "null"));
        }
        foreach (var input in positive.Inputs)
        {
            var c = Of(input);
            if (c.Type is "integer" or "number" or "boolean")
                yield return Variant(positive, ContractTestKind.InvalidType, input.Name, Set(positive.Template, input, JsonValue.Create(c.Type == "boolean" ? "not-a-boolean" : "not-a-number")), input,
                    Validation(op), $"{input.Name} is declared as {c.Type}; the request sends text instead.", now);
            else if (c.Type == "string" && input.In == "body")
                yield return Variant(positive, ContractTestKind.InvalidType, input.Name, Set(positive.Template, input, JsonValue.Create(12345)), input, Validation(op),
                    $"{input.Name} is declared as string; the request sends a number.", now);
            if (c.Enum.Count > 0)
                yield return Variant(positive, ContractTestKind.InvalidEnum, input.Name, Set(positive.Template, input, JsonValue.Create("NOT_A_VALID_VALUE")), input, Validation(op),
                    $"{input.Name} allows {string.Join(", ", c.Enum)}; the request sends NOT_A_VALID_VALUE.", now);
            if (input.In == "body" && !c.Nullable)
                yield return Variant(positive, ContractTestKind.NullNotAllowed, input.Name, Set(positive.Template, input, null), input, Validation(op),
                    $"{input.Name} is not nullable; the request sends null.", now);
            if (input.Required && c.Type == "string" && c.Enum.Count == 0)
                yield return Variant(positive, ContractTestKind.EmptyValue, input.Name, Set(positive.Template, input, JsonValue.Create("")), input, Validation(op),
                    $"{input.Name} is required; the request sends an empty string.", now);
            JsonNode? boundary = c.Maximum is { } max ? JsonNode.Parse((max + 1).ToString(CultureInfo.InvariantCulture)) : c.Minimum is { } min ? JsonNode.Parse((min - 1).ToString(CultureInfo.InvariantCulture))
                : c.MaxLength is { } maxLength and < 4096 ? JsonValue.Create(new string('x', maxLength + 1)) : c.MinLength is > 1 ? JsonValue.Create("x") : null;
            if (boundary is not null)
                yield return Variant(positive, ContractTestKind.BoundaryViolation, input.Name, Set(positive.Template, input, boundary), input, Validation(op),
                    $"{input.Name} is bounded ({Bounds(c)}); the request sends {boundary.ToJsonString()}.", now);
        }
        if (positive.Template.Body is { Length: > 0 } && (positive.Template.ContentType ?? "").Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            yield return Variant(positive, ContractTestKind.InvalidContentType, "", positive.Template with { ContentType = "text/plain" }, null, Expect(op, ["415", "400"]),
                "The operation accepts JSON; the request declares text/plain.", now);
            yield return Variant(positive, ContractTestKind.InvalidShape, "", positive.Template with { Body = "[]" }, null, Validation(op),
                "The operation expects a JSON object; the request sends an array.", now);
        }
        if (op.Security.Count > 0)
            yield return Variant(positive, ContractTestKind.MissingAuthentication, "", positive.Template, null, Expect(op, ["401", "403"]),
                $"The operation requires {string.Join(", ", op.Security)}; the request is sent without any credential.", now) with { WithoutAuthentication = true };
        var declared = doc.Operations.Where(o => o.Path == op.Path).Select(o => o.Kind.ToUpperInvariant()).ToHashSet();
        if (SafeMethods.FirstOrDefault(m => !declared.Contains(m)) is { } unsupported)
            yield return Variant(positive, ContractTestKind.UnsupportedMethod, unsupported, positive.Template with { Method = unsupported, Body = null, ContentType = null }, null, Expect(op, ["405"]),
                $"{op.Path} does not declare {unsupported}; the request uses it (a read-only method).", now);
    }

    private static IEnumerable<ContractTestDefinition> GraphQl(SourceContract doc, ContractTestDefinition positive, ContractOperation op, DateTimeOffset now)
    {
        var errors = new List<AssertionDefinition> { new("status", AssertionKind.StatusCode, AssertionOperator.In, null, "200,400"), new("errors", AssertionKind.GraphQlErrorCount, AssertionOperator.GreaterThan, null, "0") };
        const string expected = "GraphQL errors (HTTP 200 or 400) — defined by the GraphQL specification";
        foreach (var input in positive.Inputs)
        {
            var type = (input.Type ?? "").Trim();
            var named = type.Replace("!", "").Replace("[", "").Replace("]", "");
            var shape = doc.Types.FirstOrDefault(t => t.Name == named);
            if (type.EndsWith('!'))
                yield return GqlVariant(positive, ContractTestKind.NullNotAllowed, input, null, errors, expected, $"{input.Name} is non-null ({type}); the variables send null.", now);
            if (shape?.Kind == "enum")
                yield return GqlVariant(positive, ContractTestKind.InvalidEnum, input, JsonValue.Create("NOT_A_VALID_VALUE"), errors, expected, $"{input.Name} is the enum {named} ({string.Join(", ", shape.Values)}).", now);
            if (type.TrimEnd('!').StartsWith('['))
                yield return GqlVariant(positive, ContractTestKind.InvalidListShape, input, JsonValue.Create("not-a-list"), errors, expected, $"{input.Name} is a list ({type}); the variables send a single value.", now);
            else if (named is "Int" or "Float" or "Boolean" or "String")
                yield return GqlVariant(positive, ContractTestKind.InvalidType, input, named == "String" ? JsonValue.Create(12345) : JsonValue.Create(named == "Boolean" ? "not-a-boolean" : "not-a-number"),
                    errors, expected, $"{input.Name} is {type}; the variables send another type.", now);
        }
        foreach (var argument in op.ParameterDetails.Where(a => doc.Types.FirstOrDefault(t => t.Name == (a.Type ?? "").Replace("!", "").Replace("[", "").Replace("]", "")) is { Kind: "input_object" }))
        {
            var vars = JsonNode.Parse(positive.Template.GraphQlVariablesJson ?? "{}") as JsonObject ?? [];
            if (vars[argument.Name] is not JsonObject input) continue;
            input["birknextUnknownField"] = 1;
            yield return Make(positive, ContractTestKind.UnknownInputField, argument.Name, positive.Template with { GraphQlVariablesJson = vars.ToJsonString(), Assertions = errors, ExpectedOutcome = expected, ContractCheck = ContractCheckMode.Record },
                positive.Inputs, $"{argument.Type} has no field birknextUnknownField; the variables send one.", now);
        }
        var document = positive.Template.GraphQlQuery ?? "";
        var close = document.LastIndexOf('}');
        if (close > 0)
            yield return Make(positive, ContractTestKind.InvalidSelection, "", positive.Template with { GraphQlQuery = document[..close].TrimEnd() + " __birknextUnknownField }", Assertions = errors, ExpectedOutcome = expected, ContractCheck = ContractCheckMode.Record },
                positive.Inputs, "The document selects a field the schema does not have.", now);
    }

    private static ContractTestDefinition GqlVariant(ContractTestDefinition positive, ContractTestKind kind, ContractTestInput input, JsonNode? value, List<AssertionDefinition> assertions, string expected, string why, DateTimeOffset now)
    {
        var vars = JsonNode.Parse(positive.Template.GraphQlVariablesJson ?? "{}") as JsonObject ?? [];
        var owner = input.In.StartsWith("input ", StringComparison.Ordinal) ? vars[input.In["input ".Length..]] as JsonObject : vars;
        if (owner is not null) owner[input.Name] = value?.DeepClone();
        return Make(positive, kind, input.Name, positive.Template with { GraphQlVariablesJson = vars.ToJsonString(), Assertions = assertions, ExpectedOutcome = expected, ContractCheck = ContractCheckMode.Record },
            [.. positive.Inputs.Where(i => i.Name != input.Name)], why, now);
    }

    private sealed record Expectation(List<AssertionDefinition> Assertions, string Outcome);

    private static Expectation Validation(ContractOperation op) => Expect(op, ["400", "422"]);

    /// <summary>The documented subset of <paramref name="wanted"/>; none documented = needs configuration (no assertion, Not ready).</summary>
    private static Expectation Expect(ContractOperation op, string[] wanted)
    {
        var documented = wanted.Where(op.ResponseCodes.Contains).ToList();
        return documented.Count > 0
            ? new([new("status", AssertionKind.StatusCode, AssertionOperator.In, null, string.Join(',', documented))], $"HTTP {string.Join(" or ", documented)}")
            : new([], $"Needs configuration: the contract documents none of {string.Join(", ", wanted)} for this operation, so the expected outcome is not inferred.");
    }

    private static ContractTestDefinition Variant(ContractTestDefinition positive, ContractTestKind kind, string variant, ExecutionCallDefinition template, ContractTestInput? replaced,
        Expectation expectation, string why, DateTimeOffset now) =>
        Make(positive, kind, variant, template with { Assertions = expectation.Assertions, ExpectedOutcome = expectation.Outcome, ContractCheck = ContractCheckMode.Record },
            [.. positive.Inputs.Where(i => replaced is null || i.Name != replaced.Name)], $"{why} Expected: {expectation.Outcome}", now);

    private static ContractTestDefinition Make(ContractTestDefinition positive, ContractTestKind kind, string variant, ExecutionCallDefinition template, List<ContractTestInput> inputs, string why, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(), ContractKey = positive.ContractKey, ContractVersion = positive.ContractVersion, Operation = positive.Operation, Kind = kind, Variant = variant,
        Name = $"{positive.Operation} — {Label(kind)[10..].Trim()}{(variant.Length > 0 ? $" ({variant})" : "")}", Template = template, Inputs = inputs, Binding = positive.Binding,
        Rationale = why, CreatedAt = now, UpdatedAt = now,
    };

    /// <summary>The template with one input's placeholder replaced by a literal (query/header/path text, or a JSON body value).</summary>
    private static ExecutionCallDefinition Set(ExecutionCallDefinition t, ContractTestInput input, JsonNode? value)
    {
        var placeholder = ContractTestRules.Placeholder(input.Name);
        var text = value is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : value?.ToJsonString() ?? "";
        switch (input.In)
        {
            case "query": return t with { Query = t.Query.ToDictionary(q => q.Key, q => q.Value == placeholder ? text : q.Value, StringComparer.Ordinal) };
            case "header": return t with { Headers = t.Headers.ToDictionary(h => h.Key, h => h.Value == placeholder ? text : h.Value, StringComparer.OrdinalIgnoreCase) };
            case "path": return t with { Path = t.Path.Replace(placeholder, Uri.EscapeDataString(text), StringComparison.Ordinal) };
            default:
                // A body without an object schema is templated as one whole-body placeholder: the variant replaces the whole body.
                if (t.Body == placeholder) return t with { Body = value?.ToJsonString() ?? "null" };
                if (t.Body is null || !t.Body.TrimStart().StartsWith('{') || JsonNode.Parse(t.Body) is not JsonObject body) return t;
                body[input.Name] = value?.DeepClone();
                return t with { Body = body.ToJsonString() };
        }
    }

    private static decimal? Num(JsonNode? n) => n?.GetValueKind() == JsonValueKind.Number ? JsonNumber.Of(n) : null;
    private static string Bounds(Constraint c) => string.Join(", ", new[] { c.Minimum is { } a ? $"minimum {a}" : null, c.Maximum is { } b ? $"maximum {b}" : null,
        c.MinLength is { } d ? $"minLength {d}" : null, c.MaxLength is { } e ? $"maxLength {e}" : null }.Where(x => x is not null));
}
