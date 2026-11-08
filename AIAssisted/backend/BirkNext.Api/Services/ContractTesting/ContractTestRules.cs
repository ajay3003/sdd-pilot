using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BirkNext.Api.Services.ExecutionEvidence;
using BirkNext.ContractTesting;
using BirkNext.ExecutionEvidence;
using BirkNext.SourceDomains;
using BirkNext.TestData;

namespace BirkNext.Api.Services.ContractTesting;

/// <summary>
/// Pure rules of contract-driven testing: draft generation from a contract operation, Test Data materialization into a call, the impact of a
/// newer contract version on a test, and the dataset compatibility the picker shows. No I/O; nothing here sends a request.
/// </summary>
public static partial class ContractTestRules
{
    /// <summary>A Test Data value in a template: <c>{{dataset.name}}</c>.</summary>
    public static string Placeholder(string name) => "{{dataset." + name + "}}";
    private static readonly string[] Verbs = ["GET", "HEAD", "OPTIONS", "POST", "PUT", "PATCH", "DELETE"];
    [GeneratedRegex(@"^\{\{dataset\.([A-Za-z_][A-Za-z0-9_.\-]*)\}\}$")] private static partial Regex WholePlaceholder();
    [GeneratedRegex(@"\{\{dataset\.([A-Za-z_][A-Za-z0-9_.\-]*)\}\}")] private static partial Regex AnyPlaceholder();

    private static string P(string name) => Placeholder(name);

    /// <summary>"Child" from "Child", "[Child]" or "#/components/schemas/Child"; null for primitives.</summary>
    public static string? TypeName(string? schema) => schema is null ? null : schema.Replace("!", "").Trim('[', ']').Split('/').Last() is var n && n.Length > 0 && !n.Contains(':') && n is not ("string" or "integer" or "number" or "boolean" or "object" or "array") ? n : null;

    public static bool IsRest(ContractOperation op) => Verbs.Contains(op.Kind.ToUpperInvariant()) && op.Path is { Length: > 0 };
    public static bool IsGraphQl(ContractOperation op) => op.Kind is "query" or "mutation";

    /// <summary>Draft tests for one operation of either protocol (empty for subscriptions and other kinds).</summary>
    public static List<ContractTestDefinition> GenerateAny(SourceContract contract, string key, int version, ContractOperation op, DateTimeOffset now) =>
        IsRest(op) ? Generate(contract, key, version, op, now) : IsGraphQl(op) ? GenerateGraphQl(contract, key, version, op, now) : [];

    public const string GraphQlEndpoint = "/graphql";
    private static readonly string[] BuiltInScalars = ["String", "Int", "Float", "Boolean", "ID"];

    /// <summary>
    /// Draft tests for one GraphQL root field. The document declares one variable per argument with its exact type (nullability and lists kept);
    /// an input-object argument becomes a nested variables object whose fields are the test's inputs; the selection set holds the result type's
    /// leaf fields (scalars and enums). Positive: HTTP 200, no GraphQL errors, data present for the field and its non-null leaf fields — a 200 with
    /// errors fails. Negative (when a required argument or input field exists): the value is left out and GraphQL errors are expected (HTTP 200,
    /// or 400 under GraphQL-over-HTTP). Mutations are data-changing and run only when confirmed.
    /// </summary>
    public static List<ContractTestDefinition> GenerateGraphQl(SourceContract contract, string key, int version, ContractOperation op, DateTimeOffset now)
    {
        if (!IsGraphQl(op)) return [];
        var field = op.OperationId ?? op.Name.Split(' ').Last();
        var operationName = char.ToUpperInvariant(field[0]) + field[1..];
        var inputs = new List<ContractTestInput>();
        var variables = new JsonObject();
        foreach (var a in op.ParameterDetails)
        {
            var shape = TypeName(a.Type) is { } named ? contract.Types.FirstOrDefault(t => t.Name == named) : null;
            if (shape is { Kind: "input_object" } && !(a.Type ?? "").TrimStart().StartsWith('['))
            {
                var o = new JsonObject();
                foreach (var f in shape.Fields.Take(50)) { o[f.Name] = P(f.Name); inputs.Add(new(f.Name, $"input {a.Name}", f.Required, f.Type)); }
                variables[a.Name] = o;
            }
            else { variables[a.Name] = P(a.Name); inputs.Add(new(a.Name, "variable", a.Required, a.Type)); }
        }
        inputs = [.. inputs.GroupBy(i => i.Name, StringComparer.Ordinal).Select(g => g.OrderByDescending(i => i.Required).First())];
        var resultShape = TypeName(op.ResultType) is { } rn ? contract.Types.FirstOrDefault(t => t.Name == rn && t.Kind is "object" or "interface") : null;
        bool Leaf(ContractField f) => TypeName(f.Type) is not { } n || BuiltInScalars.Contains(n) || contract.Types.FirstOrDefault(t => t.Name == n) is not { } ft || ft.Kind is "enum" or "scalar";
        var leaves = resultShape?.Fields.Where(Leaf).Take(20).ToList() ?? [];
        var selection = resultShape is null ? "" : leaves.Count > 0 ? $" {{ {string.Join(" ", leaves.Select(f => f.Name))} }}" : " { __typename }";
        var declarations = op.ParameterDetails.Count == 0 ? "" : $"({string.Join(", ", op.ParameterDetails.Select(a => $"${a.Name}: {a.Type}"))})";
        var arguments = op.ParameterDetails.Count == 0 ? "" : $"({string.Join(", ", op.ParameterDetails.Select(a => $"{a.Name}: ${a.Name}"))})";
        var document = $"{op.Kind} {operationName}{declarations} {{ {field}{arguments}{selection} }}";

        var assertions = new List<AssertionDefinition>
        {
            new("status", AssertionKind.StatusCode, AssertionOperator.Equals, null, "200"),
            new("no-errors", AssertionKind.GraphQlErrorCount, AssertionOperator.Equals, null, "0"),
            new("data", AssertionKind.GraphQlDataPath, AssertionOperator.Exists, field),
            new("contract", AssertionKind.ContractSchema, AssertionOperator.Equals, null, "valid"),
        };
        if (resultShape is not null && !(op.ResultType ?? "").TrimStart().StartsWith('['))
            foreach (var f in leaves.Where(f => f.Required).Take(10)) assertions.Add(new($"data-{f.Name}", AssertionKind.GraphQlDataPath, AssertionOperator.Exists, $"{field}.{f.Name}"));
        var template = new ExecutionCallDefinition
        {
            Name = op.Name, Protocol = ExecutionProtocol.GraphQl, Method = "POST", Path = GraphQlEndpoint, GraphQlQuery = document, GraphQlOperationName = operationName,
            GraphQlVariablesJson = variables.Count == 0 ? null : variables.ToJsonString(), Assertions = assertions, TimeoutMs = 15000,
            ExpectedOutcome = "HTTP 200 with data and no GraphQL errors", ContractCheck = ContractCheckMode.Enforce,
        };
        var positive = new ContractTestDefinition
        {
            Id = Guid.NewGuid(), ContractKey = key, ContractVersion = version, Operation = op.Name, Name = $"{op.Name} — returns data without GraphQL errors", Kind = ContractTestKind.Positive,
            Template = template, Inputs = inputs, CreatedAt = now, UpdatedAt = now,
            Rationale = $"Sends {op.Kind} {field} with every required variable from the bound Test Data and expects HTTP 200, no GraphQL errors and data for {field}"
                + (assertions.Count > 3 ? $" with its non-null fields ({string.Join(", ", leaves.Where(f => f.Required).Take(10).Select(f => f.Name))})." : ".")
                + " An HTTP 200 that carries GraphQL errors fails.",
        };
        var result = new List<ContractTestDefinition> { positive };
        var omit = inputs.FirstOrDefault(i => i.Required);
        if (omit is not null)
        {
            var negativeVariables = JsonNode.Parse(variables.ToJsonString())!.AsObject();
            if (omit.In == "variable") negativeVariables.Remove(omit.Name);
            else if (negativeVariables[omit.In["input ".Length..]] is JsonObject nested) nested.Remove(omit.Name);
            result.Add(new ContractTestDefinition
            {
                Id = Guid.NewGuid(), ContractKey = key, ContractVersion = version, Operation = op.Name, Name = $"{op.Name} — reports a GraphQL error without {omit.Name}",
                Kind = ContractTestKind.MissingRequiredInput, CreatedAt = now, UpdatedAt = now, Inputs = [.. inputs.Where(i => i.Name != omit.Name)],
                Template = template with
                {
                    GraphQlVariablesJson = negativeVariables.ToJsonString(), ContractCheck = ContractCheckMode.Record,
                    Assertions = [new("status", AssertionKind.StatusCode, AssertionOperator.In, null, "200,400"), new("errors", AssertionKind.GraphQlErrorCount, AssertionOperator.GreaterThan, null, "0")],
                    ExpectedOutcome = "GraphQL errors (HTTP 200 or 400)",
                },
                Rationale = $"The schema makes {omit.Name} ({omit.Type}) non-null; the request leaves it out and expects GraphQL errors. GraphQL servers answer this with HTTP 200 and an errors array, or HTTP 400, so both are accepted — the errors are what is asserted.",
            });
        }
        return result;
    }

    /// <summary>
    /// Draft tests for one REST operation: a positive test (every required value from Test Data; the documented success codes; the required
    /// properties of the documented success schema) and, when the contract requires a query parameter or body field, a negative test that omits
    /// it and expects a client error. Credential-like header parameters and cookies are never inputs (authentication is the environment's).
    /// </summary>
    public static List<ContractTestDefinition> Generate(SourceContract contract, string key, int version, ContractOperation op, DateTimeOffset now)
    {
        if (!IsRest(op)) return [];
        var method = op.Kind.ToUpperInvariant();
        var inputs = new List<ContractTestInput>();
        var path = op.Path!;
        foreach (var p in op.ParameterDetails.Where(p => p.In == "path"))
        {
            path = path.Replace("{" + p.Name + "}", P(p.Name), StringComparison.Ordinal);
            inputs.Add(new(p.Name, "path", true, p.Type));
        }
        var query = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in op.ParameterDetails.Where(p => p.In == "query")) { query[p.Name] = P(p.Name); inputs.Add(new(p.Name, "query", p.Required, p.Type)); }
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in op.ParameterDetails.Where(p => p.In == "header" && !ExecutionEvidenceRedaction.IsSensitiveHeader(p.Name)))
        { headers[p.Name] = P(p.Name); inputs.Add(new(p.Name, "header", p.Required, p.Type)); }

        string? body = null, contentType = null;
        var bodyFields = new List<ContractField>();
        if (op.RequestContentTypes.Count > 0)
        {
            contentType = op.RequestContentTypes.FirstOrDefault(t => t.Contains("json", StringComparison.OrdinalIgnoreCase)) ?? op.RequestContentTypes[0];
            var type = op.RequestSchema is { } rs && !rs.StartsWith('[') && TypeName(rs) is { } n ? contract.Types.FirstOrDefault(t => t.Name == n) : null;
            if (contentType.Contains("json", StringComparison.OrdinalIgnoreCase) && type is { Fields.Count: > 0 })
            {
                var o = new JsonObject();
                foreach (var f in type.Fields.Take(50)) { o[f.Name] = P(f.Name); bodyFields.Add(f); inputs.Add(new(f.Name, "body", f.Required, f.Type)); }
                body = o.ToJsonString();
            }
            else if (op.RequestBodyRequired || op.RequestSchema is not null)
            { body = P("body"); inputs.Add(new("body", "body", op.RequestBodyRequired, op.RequestSchema ?? contentType)); }
        }
        inputs = [.. inputs.GroupBy(i => i.Name, StringComparer.Ordinal).Select(g => g.OrderByDescending(i => i.Required).First())];

        var success = op.ResponseCodes.Where(c => c.Length == 3 && c[0] == '2').ToList();
        var assertions = new List<AssertionDefinition>
        {
            new("status", AssertionKind.StatusCode, AssertionOperator.In, null, success.Count > 0 ? string.Join(',', success) : "200,201,202,204"),
            new("contract", AssertionKind.ContractSchema, AssertionOperator.Equals, null, "valid"),
        };
        var responseType = success.Select(c => op.ResponseSchemas.TryGetValue(c, out var s) ? s : null).FirstOrDefault(s => s is not null);
        if (responseType is { } rt && !rt.StartsWith('[') && TypeName(rt) is { } rn && contract.Types.FirstOrDefault(t => t.Name == rn) is { } shape)
            foreach (var f in shape.Fields.Where(f => f.Required).Take(10))
                assertions.Add(new($"body-{f.Name}", AssertionKind.JsonPath, AssertionOperator.Exists, "$." + f.Name));

        var template = new ExecutionCallDefinition
        {
            Name = $"{op.Name}", Protocol = ExecutionProtocol.Rest, Method = method, Path = path, Query = query, Headers = headers, ContentType = body is null ? null : contentType,
            Body = body, Assertions = assertions, TimeoutMs = 15000, ExpectedOutcome = success.Count > 0 ? $"HTTP {string.Join(" or ", success)}" : "A 2xx response",
            ContractCheck = ContractCheckMode.Enforce,
        };
        var positive = new ContractTestDefinition
        {
            Id = Guid.NewGuid(), ContractKey = key, ContractVersion = version, Operation = op.Name, Name = $"{op.Name} — succeeds with valid data", Kind = ContractTestKind.Positive,
            Template = template, Inputs = inputs, CreatedAt = now, UpdatedAt = now,
            Rationale = $"Sends every required value from the bound Test Data and expects {template.ExpectedOutcome}"
                + (assertions.Count > 1 ? $" with the required properties of {TypeName(responseType)} present." : ".")
                + (success.Count == 0 ? " The contract documents no 2xx response, so any common success code is accepted." : ""),
        };
        var result = new List<ContractTestDefinition> { positive };

        // Negative: omit one required query parameter or body field. Path parameters are part of the route and are not omitted.
        var omit = inputs.FirstOrDefault(i => i.Required && i.In == "query") ?? inputs.FirstOrDefault(i => i.Required && i.In == "body" && bodyFields.Any(f => f.Name == i.Name));
        if (omit is not null)
        {
            var clientErrors = op.ResponseCodes.Where(c => c is "400" or "422").ToList();
            var negativeBody = body;
            if (omit.In == "body" && JsonNode.Parse(body!) is JsonObject bo) { bo.Remove(omit.Name); negativeBody = bo.ToJsonString(); }
            var negativeQuery = new Dictionary<string, string>(query, StringComparer.Ordinal);
            if (omit.In == "query") negativeQuery.Remove(omit.Name);
            // The expected codes come from the contract only; when it documents neither 400 nor 422 the test needs configuration (no inference).
            var expected = clientErrors.Count > 0 ? string.Join(',', clientErrors) : null;
            result.Add(new ContractTestDefinition
            {
                Id = Guid.NewGuid(), ContractKey = key, ContractVersion = version, Operation = op.Name, Name = $"{op.Name} — rejects a request without {omit.Name}",
                Kind = ContractTestKind.MissingRequiredInput, CreatedAt = now, UpdatedAt = now,
                Inputs = [.. inputs.Where(i => i.Name != omit.Name)],
                Template = template with
                {
                    Query = negativeQuery, Body = negativeBody, ContractCheck = ContractCheckMode.Record,
                    Assertions = expected is null ? [] : [new("status", AssertionKind.StatusCode, AssertionOperator.In, null, expected)],
                    ExpectedOutcome = expected is null ? "Needs configuration: the contract documents neither 400 nor 422 for this operation." : $"HTTP {expected.Replace(",", " or ", StringComparison.Ordinal)}",
                },
                Rationale = $"The contract requires {omit.In} value {omit.Name}; the request leaves it out"
                    + (expected is null ? ". The contract documents neither 400 nor 422, so the expected outcome needs configuration (it is not inferred)." : $" and expects a client error (HTTP {expected.Replace(",", " or ", StringComparison.Ordinal)})."),
            });
        }
        return result;
    }

    /// <summary>
    /// The call a test sends: placeholders replaced by the pinned Test Data values. A whole-placeholder JSON value takes the value's exact JSON
    /// type (numbers stay numbers). An optional input without a value is left out; a required one is reported, and the call is not built.
    /// </summary>
    public static (ExecutionCallDefinition? Call, List<string> Missing) Materialize(ContractTestDefinition test, IReadOnlyDictionary<string, TestDataTypedEntry> values)
    {
        var missing = new List<string>();
        var optional = test.Inputs.Where(i => !i.Required).Select(i => i.Name).ToHashSet(StringComparer.Ordinal);
        string? Text(string name) => values.TryGetValue(name, out var e) ? e.Type == TestDataPrimitive.Null ? null : e.Value : null;
        string Replace(string text, bool escape) => AnyPlaceholder().Replace(text, m =>
        {
            var name = m.Groups[1].Value;
            if (Text(name) is { } v) return escape ? Uri.EscapeDataString(v) : v;
            if (!optional.Contains(name) && !missing.Contains(name)) missing.Add(name);
            return m.Value;
        });
        var t = test.Template;
        var path = Replace(t.Path, escape: true);
        var query = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, v) in t.Query)
        {
            if (WholePlaceholder().Match(v) is { Success: true } m && optional.Contains(m.Groups[1].Value) && !values.ContainsKey(m.Groups[1].Value)) continue;
            query[k] = Replace(v, escape: false);
        }
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in t.Headers)
        {
            if (WholePlaceholder().Match(v) is { Success: true } m && optional.Contains(m.Groups[1].Value) && !values.ContainsKey(m.Groups[1].Value)) continue;
            headers[k] = Replace(v, escape: false);
        }
        string? body = t.Body;
        if (body is not null)
        {
            if (WholePlaceholder().Match(body) is { Success: true } whole)
            {
                var name = whole.Groups[1].Value;
                body = values.TryGetValue(name, out var e) ? (t.ContentType ?? "").Contains("json", StringComparison.OrdinalIgnoreCase) ? TestDataResourceRules.ToJson(e)?.ToJsonString() ?? "null" : e.Value
                    : optional.Contains(name) ? null : Replace(body, false);
            }
            else if ((t.ContentType ?? "").Contains("json", StringComparison.OrdinalIgnoreCase) && JsonNode.Parse(body) is JsonObject o) { Fill(o); body = o.ToJsonString(); }
            else body = Replace(body, false);
        }
        string? variables = t.GraphQlVariablesJson;
        if (variables is not null && JsonNode.Parse(variables) is JsonObject vars) { Fill(vars); variables = vars.ToJsonString(); }
        return missing.Count > 0 ? (null, missing) : (t with { Path = path, Query = query, Headers = headers, Body = body, GraphQlVariablesJson = variables }, missing);

        // Whole-placeholder JSON values take the Test Data value's exact JSON type, at any depth (GraphQL input objects are nested).
        void Fill(JsonObject o)
        {
            foreach (var (k, v) in o.ToList())
            {
                if (v is JsonObject nested) { Fill(nested); continue; }
                if (v is JsonArray list) { FillArray(list); continue; }
                if (v is not JsonValue jv || jv.GetValueKind() != JsonValueKind.String || WholePlaceholder().Match(jv.GetValue<string>()) is not { Success: true } m) continue;
                var name = m.Groups[1].Value;
                if (values.TryGetValue(name, out var e)) o[k] = TestDataResourceRules.ToJson(e);
                else if (optional.Contains(name)) o.Remove(k);
                else if (!missing.Contains(name)) missing.Add(name);
            }
        }

        // Lists: each element keeps its own type; an unresolved optional element is left out, never sent as "" or null.
        void FillArray(JsonArray a)
        {
            for (var i = a.Count - 1; i >= 0; i--)
            {
                switch (a[i])
                {
                    case JsonObject nested: Fill(nested); continue;
                    case JsonArray inner: FillArray(inner); continue;
                    case JsonValue jv when jv.GetValueKind() == JsonValueKind.String && WholePlaceholder().Match(jv.GetValue<string>()) is { Success: true } m:
                        var name = m.Groups[1].Value;
                        if (values.TryGetValue(name, out var e)) a[i] = TestDataResourceRules.ToJson(e);
                        else if (optional.Contains(name)) a.RemoveAt(i);
                        else if (!missing.Contains(name)) missing.Add(name);
                        continue;
                }
            }
        }
    }

    public static bool IsWholePlaceholder(string text) => WholePlaceholder().IsMatch(text.Trim());

    /// <summary>
    /// The values a call actually uses (every placeholder of its template that resolved), typed, with their source and a redacted preview of at
    /// most 120 characters. Evidence keeps this so it can be checked that the right value and type were sent.
    /// </summary>
    public static List<ResolvedValue> Resolved(ContractTestDefinition test, IReadOnlyDictionary<string, TestDataTypedEntry> values, Func<string, string> source)
    {
        var t = test.Template;
        var used = Placeholders(t.Path).Concat(t.Query.Values.SelectMany(Placeholders)).Concat(t.Headers.Values.SelectMany(Placeholders))
            .Concat(Placeholders(t.Body)).Concat(Placeholders(t.GraphQlVariablesJson)).Distinct(StringComparer.Ordinal);
        return [.. used.Where(values.ContainsKey).Select(name =>
        {
            var e = values[name];
            var text = e.Type == TestDataPrimitive.Null ? "null" : ExecutionEvidenceRedaction.Text(e.Value ?? "").Text;
            return new ResolvedValue(name, e.Type.ToString(), source(name), text.Length > 120 ? text[..120] + "…" : text);
        })];
    }

    /// <summary>Every <c>{{dataset.name}}</c> name used in a text, in order of first use.</summary>
    public static List<string> Placeholders(string? text) => [.. AnyPlaceholder().Matches(text ?? "").Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal)];

    /// <summary>Which of a test's inputs a set of value names covers: missing required and missing optional names.</summary>
    public static (List<string> Required, List<string> Optional) Missing(ContractTestDefinition test, IReadOnlyCollection<string> provided) =>
        ([.. test.Inputs.Where(i => i.Required && !provided.Contains(i.Name)).Select(i => i.Name)], [.. test.Inputs.Where(i => !i.Required && !provided.Contains(i.Name)).Select(i => i.Name)]);

    /// <summary>
    /// What the change from <paramref name="before"/> to <paramref name="after"/> means for one test, by evidence about what the test sends, asserts
    /// and binds (see <see cref="ContractTestImpactLevel"/>). Each reason names the affected input, assertion, selection or binding. A negative
    /// test stays valid while the value it omits or breaks is still constrained and its expected code is still documented. Nothing is changed.
    /// </summary>
    public static ContractTestImpact Impact(ContractTestDefinition test, SourceContract before, SourceContract after, int toVersion, IReadOnlyDictionary<string, string?>? boundValues = null)
    {
        ContractTestImpact Result(ContractTestImpactLevel level, List<string> reasons) => new(test.Id, test.Name, test.Operation, test.ContractVersion, toVersion, level, reasons);
        if (test.ContractVersion == toVersion) return Result(ContractTestImpactLevel.NoEvidence, [$"Generated from the latest version (v{toVersion})."]);
        var op = after.Operations.FirstOrDefault(o => o.Name == test.Operation);
        if (op is null) return Result(ContractTestImpactLevel.Definite, [$"Operation: {test.Operation} no longer exists in v{toVersion}."]);
        var old = before.Operations.FirstOrDefault(o => o.Name == test.Operation);
        var negative = test.Kind != ContractTestKind.Positive;
        var reasons = new List<string>();
        var level = ContractTestImpactLevel.NoEvidence;
        void Raise(ContractTestImpactLevel l, string why) { reasons.Add($"{Name(l)}: {why}"); if (l > level) level = l; }
        var t = test.Template;
        var sent = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in Placeholders(string.Join('\n', new[] { t.Path, t.Body, t.GraphQlVariablesJson }.Concat(t.Query.Values).Concat(t.Headers.Values)))) sent.Add(name);
        foreach (var k in t.Query.Keys.Concat(t.Headers.Keys)) sent.Add(k);
        if (t.Body is { } body && JsonNode.Parse(IsWholePlaceholder(body) ? "{}" : body) is JsonObject bodyObject) foreach (var (k, _) in bodyObject) sent.Add(k);

        // Expected outcome of a negative test: its documented code must still be documented, and the value it breaks still constrained.
        if (negative && t.Assertions.FirstOrDefault(a => a.Kind == AssertionKind.StatusCode) is { Expected: { } codes } && t.Protocol == ExecutionProtocol.Rest)
            foreach (var code in codes.Split(',', StringSplitOptions.TrimEntries).Where(c => old?.ResponseCodes.Contains(c) == true && !op.ResponseCodes.Contains(c)))
                Raise(ContractTestImpactLevel.Definite, $"assertion status expects HTTP {code}, which v{toVersion} no longer documents.");
        if (test.Kind == ContractTestKind.MissingRequiredInput && test.Variant.Length == 0 && old is not null)
        {
            var omitted = old.ParameterDetails.Where(p => p.Required && !sent.Contains(p.Name)).Select(p => p.Name).FirstOrDefault();
            if (omitted is not null && op.ParameterDetails.FirstOrDefault(p => p.Name == omitted) is { Required: false })
                Raise(ContractTestImpactLevel.Definite, $"input {omitted} is no longer required, so omitting it is no longer invalid.");
        }

        foreach (var c in SourceEvidenceDiff.OperationDetailChanges(before, after).Where(c => c.Key.Split('|') is [_, var name, ..] && name == test.Operation))
        {
            var detail = c.Detail[(test.Operation.Length + 2)..];
            var subject = c.Key.Split('|').Last();
            var parameter = subject.StartsWith("param:", StringComparison.Ordinal) ? subject[6..].Split(':')[0].Split('.').Last() : null;
            switch (c.ContractChange)
            {
                case ContractChangeClass.ParameterAddedOptional:
                    Raise(ContractTestImpactLevel.NoEvidence, $"{detail} (optional; not needed)."); break;
                case ContractChangeClass.ParameterAddedRequired when negative:
                    Raise(ContractTestImpactLevel.NoEvidence, $"{detail}; the request was invalid on purpose and stays invalid."); break;
                case ContractChangeClass.ParameterAddedRequired when parameter is not null && sent.Contains(parameter):
                    Raise(ContractTestImpactLevel.NoEvidence, $"{detail}; the test already sends {parameter}."); break;
                case ContractChangeClass.ParameterAddedRequired:
                    Raise(ContractTestImpactLevel.Definite, $"{detail}; input {parameter} is not sent by the test{(boundValues?.ContainsKey(parameter ?? "") == true ? " (the bound Test Data has a value for it)" : " and the bound Test Data has no value for it")}."); break;
                case ContractChangeClass.ParameterRemoved when parameter is not null && sent.Contains(parameter):
                    Raise(ContractTestImpactLevel.Potential, $"{detail}; the test still sends {parameter}."); break;
                case ContractChangeClass.ParameterRemoved:
                    Raise(ContractTestImpactLevel.NoEvidence, $"{detail}; the test does not send it."); break;
                case ContractChangeClass.ParameterChanged when negative && test.Variant == parameter:
                    Raise(ContractTestImpactLevel.NeedsReview, $"{detail}; this negative test targets {parameter} — check that its invalid value is still invalid."); break;
                case ContractChangeClass.ParameterChanged when parameter is not null && sent.Contains(parameter):
                    Raise(ContractTestImpactLevel.Potential, $"{detail}; the test sends {parameter}{(boundValues?.TryGetValue(parameter, out var bv) == true && bv is not null ? $" (bound value {bv})" : "")}."); break;
                case ContractChangeClass.ParameterChanged:
                    Raise(ContractTestImpactLevel.NoEvidence, $"{detail}; the test does not send it."); break;
                case ContractChangeClass.RequestBodyChanged:
                    Raise(negative ? ContractTestImpactLevel.NeedsReview : ContractTestImpactLevel.Potential, $"{detail}."); break;
                case ContractChangeClass.ResponseChanged when !negative && subject.StartsWith("response:", StringComparison.Ordinal) && c.Kind == SourceEvidenceChangeKind.Removed
                    && t.Assertions.Any(a => a.Kind == AssertionKind.StatusCode && (a.Expected ?? "").Split(',', StringSplitOptions.TrimEntries).All(x => !op.ResponseCodes.Contains(x))):
                    Raise(ContractTestImpactLevel.Definite, $"{detail}; assertion status expects only codes v{toVersion} no longer documents."); break;
                case ContractChangeClass.ResponseChanged:
                    Raise(negative ? ContractTestImpactLevel.NoEvidence : ContractTestImpactLevel.Potential, $"{detail}{(negative ? " (not what this negative test checks)" : " — the response the test checks may differ")}."); break;
                case ContractChangeClass.SecurityChanged:
                    Raise(ContractTestImpactLevel.NeedsReview, $"{detail} — check the authentication binding of this contract."); break;
                case ContractChangeClass.Deprecated:
                    Raise(ContractTestImpactLevel.NeedsReview, $"{test.Operation} is deprecated in v{toVersion}."); break;
                default:
                    Raise(ContractTestImpactLevel.NeedsReview, $"{detail}."); break;
            }
        }
        if (!before.Servers.SequenceEqual(after.Servers, StringComparer.Ordinal) && (before.Servers.Count > 0 || after.Servers.Count > 0))
            Raise(ContractTestImpactLevel.NeedsReview, "the declared servers changed — check the endpoint binding (base path) of this contract.");

        // Types: request types (REST body schema, GraphQL argument types) and response types (success schemas, GraphQL result type).
        var requestTypes = new[] { TypeName(old?.RequestSchema), TypeName(op.RequestSchema) }
            .Concat((old?.ParameterDetails ?? []).Concat(op.ParameterDetails).Where(p => p.In == "argument").Select(p => TypeName(p.Type)))
            .Where(x => x is not null).ToHashSet(StringComparer.Ordinal);
        var responseTypes = (old?.ResponseSchemas.Values.AsEnumerable() ?? []).Concat(op.ResponseSchemas.Values).Append(old?.ResultType).Append(op.ResultType)
            .Select(TypeName).Where(x => x is not null).ToHashSet(StringComparer.Ordinal);
        var selected = t.Protocol == ExecutionProtocol.GraphQl ? Selections(t.GraphQlQuery) : [];
        foreach (var c in SourceEvidenceDiff.ContractChanges(before, after).Where(c => c.Area is "Field" or "Enum value"))
        {
            var key = c.Key.Split('|').Last();
            var typeName = key.Split('.')[0];
            var member = key.Contains('.') ? key[(key.IndexOf('.') + 1)..] : "";
            var wasRequired = before.Types.FirstOrDefault(x => x.Name == typeName)?.Fields.FirstOrDefault(f => f.Name == member)?.Required == true;
            if (requestTypes.Contains(typeName))
            {
                switch (c.ContractChange)
                {
                    case ContractChangeClass.FieldAddedOptional or ContractChangeClass.EnumValueAdded: Raise(ContractTestImpactLevel.NoEvidence, $"request type {c.Detail} ({Label(c.ContractChange)})."); break;
                    case ContractChangeClass.FieldAddedRequired when negative: Raise(ContractTestImpactLevel.NoEvidence, $"request type {c.Detail} is new and required; the request was invalid on purpose and stays invalid."); break;
                    case ContractChangeClass.FieldAddedRequired when sent.Contains(member): Raise(ContractTestImpactLevel.NoEvidence, $"request type {c.Detail} is new and required; the test already sends {member}."); break;
                    case ContractChangeClass.FieldAddedRequired: Raise(ContractTestImpactLevel.Definite, $"request type {c.Detail} is new and required; the test does not send {member}."); break;
                    case ContractChangeClass.EnumValueRemoved:
                        var removedValue = member;
                        var hit = boundValues?.Where(v => v.Value == removedValue).Select(v => v.Key).FirstOrDefault();
                        Raise(hit is not null ? ContractTestImpactLevel.Definite : ContractTestImpactLevel.NoEvidence,
                            hit is not null ? $"enum value {c.Detail} was removed and the bound Test Data sends it as {hit}." : $"enum value {c.Detail} was removed; the bound Test Data does not use it.");
                        break;
                    default:
                        Raise(sent.Contains(member) ? (negative ? ContractTestImpactLevel.NeedsReview : ContractTestImpactLevel.Potential) : ContractTestImpactLevel.NoEvidence,
                            $"request type {c.Detail} ({Label(c.ContractChange)}){(sent.Contains(member) ? $"; the test sends {member}" : "; the test does not send it")}.");
                        break;
                }
            }
            else if (responseTypes.Contains(typeName) && !negative)
            {
                var asserted = t.Assertions.Where(a => a.Target is { } target && (target.EndsWith($".{member}", StringComparison.Ordinal) || target.Contains($".{member}.", StringComparison.Ordinal))).Select(a => a.Id).ToList();
                var isSelected = selected.Contains(member);
                if (c.ContractChange == ContractChangeClass.FieldRemoved && (asserted.Count > 0 || isSelected))
                    Raise(ContractTestImpactLevel.Definite, $"response type {c.Detail} was removed; {(isSelected ? "the query document selects it" : $"assertion {string.Join(", ", asserted)} checks it")}.");
                else if (c.ContractChange is ContractChangeClass.FieldRemoved or ContractChangeClass.TypeChanged or ContractChangeClass.RequirednessChanged or ContractChangeClass.EnumValueRemoved)
                    Raise(asserted.Count > 0 || isSelected || wasRequired ? ContractTestImpactLevel.Potential : ContractTestImpactLevel.NoEvidence,
                        $"response type {c.Detail} ({Label(c.ContractChange)}){(asserted.Count > 0 ? $"; assertion {string.Join(", ", asserted)} checks it" : wasRequired ? "; it was required" : "; not asserted and optional")}.");
                else Raise(ContractTestImpactLevel.NoEvidence, $"response type {c.Detail} ({Label(c.ContractChange)}).");
            }
        }
        if (reasons.Count == 0) reasons.Add($"{Name(ContractTestImpactLevel.NoEvidence)}: no change to {test.Operation} or its types between v{test.ContractVersion} and v{toVersion}.");
        return Result(level, reasons);
    }

    public static string Name(ContractTestImpactLevel level) => level switch
    {
        ContractTestImpactLevel.Definite => "Definite impact",
        ContractTestImpactLevel.Potential => "Potential impact",
        ContractTestImpactLevel.NeedsReview => "Needs human review",
        _ => "No evidence of impact",
    };

    /// <summary>Field names selected anywhere in a GraphQL document (best effort; empty when it does not parse).</summary>
    private static HashSet<string> Selections(string? query)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            void Walk(HotChocolate.Language.SelectionSetNode? set)
            {
                foreach (var f in set?.Selections.OfType<HotChocolate.Language.FieldNode>() ?? []) { names.Add(f.Name.Value); Walk(f.SelectionSet); }
                foreach (var i in set?.Selections.OfType<HotChocolate.Language.InlineFragmentNode>() ?? []) Walk(i.SelectionSet);
            }
            foreach (var op in HotChocolate.Language.Utf8GraphQLParser.Parse(query ?? "").Definitions.OfType<HotChocolate.Language.OperationDefinitionNode>()) Walk(op.SelectionSet);
        }
        catch (HotChocolate.Language.SyntaxException) { }
        return names;
    }

    public static string Label(ContractChangeClass c) => c switch
    {
        ContractChangeClass.FieldAddedRequired => "required field added",
        ContractChangeClass.FieldAddedOptional => "optional field added",
        ContractChangeClass.FieldRemoved => "field removed",
        ContractChangeClass.TypeChanged => "type changed",
        ContractChangeClass.RequirednessChanged => "requiredness changed",
        ContractChangeClass.EnumValueAdded => "enum value added",
        ContractChangeClass.EnumValueRemoved => "enum value removed",
        ContractChangeClass.TypeAdded => "type added",
        ContractChangeClass.TypeRemoved => "type removed",
        ContractChangeClass.OperationAdded => "operation added",
        ContractChangeClass.OperationRemoved => "operation removed",
        ContractChangeClass.ParameterAddedRequired => "required parameter added",
        ContractChangeClass.ParameterAddedOptional => "optional parameter added",
        ContractChangeClass.ParameterRemoved => "parameter removed",
        ContractChangeClass.ParameterChanged => "parameter changed",
        ContractChangeClass.RequestBodyChanged => "request body changed",
        ContractChangeClass.ResponseChanged => "response changed",
        _ => c.ToString(),
    };

    /// <summary>Breaking-ness of one contract change for consumers, for the change list (independent of any test).</summary>
    public static string Severity(ContractChangeClass c) => c switch
    {
        ContractChangeClass.OperationRemoved or ContractChangeClass.FieldRemoved or ContractChangeClass.TypeChanged or ContractChangeClass.TypeRemoved or ContractChangeClass.EnumValueRemoved
            or ContractChangeClass.ParameterAddedRequired or ContractChangeClass.ParameterRemoved or ContractChangeClass.ParameterChanged or ContractChangeClass.FieldAddedRequired
            or ContractChangeClass.RequirednessChanged or ContractChangeClass.RequestBodyChanged or ContractChangeClass.ResponseChanged => "Potentially breaking",
        _ => "Compatible",
    };
}
