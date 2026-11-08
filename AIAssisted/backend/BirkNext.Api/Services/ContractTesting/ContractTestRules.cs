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
    public static string? TypeName(string? schema) => schema is null ? null : schema.Trim('[', ']').Split('/').Last() is var n && n.Length > 0 && !n.Contains(':') && n is not ("string" or "integer" or "number" or "boolean" or "object" or "array") ? n : null;

    public static bool IsRest(ContractOperation op) => Verbs.Contains(op.Kind.ToUpperInvariant()) && op.Path is { Length: > 0 };

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
        };
        var responseType = success.Select(c => op.ResponseSchemas.TryGetValue(c, out var s) ? s : null).FirstOrDefault(s => s is not null);
        if (responseType is { } rt && !rt.StartsWith('[') && TypeName(rt) is { } rn && contract.Types.FirstOrDefault(t => t.Name == rn) is { } shape)
            foreach (var f in shape.Fields.Where(f => f.Required).Take(10))
                assertions.Add(new($"body-{f.Name}", AssertionKind.JsonPath, AssertionOperator.Exists, "$." + f.Name));

        var template = new ExecutionCallDefinition
        {
            Name = $"{op.Name}", Protocol = ExecutionProtocol.Rest, Method = method, Path = path, Query = query, Headers = headers, ContentType = body is null ? null : contentType,
            Body = body, Assertions = assertions, TimeoutMs = 15000, ExpectedOutcome = success.Count > 0 ? $"HTTP {string.Join(" or ", success)}" : "A 2xx response",
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
            var expected = clientErrors.Count > 0 ? string.Join(',', clientErrors) : "400,422";
            result.Add(new ContractTestDefinition
            {
                Id = Guid.NewGuid(), ContractKey = key, ContractVersion = version, Operation = op.Name, Name = $"{op.Name} — rejects a request without {omit.Name}",
                Kind = ContractTestKind.MissingRequiredInput, CreatedAt = now, UpdatedAt = now,
                Inputs = [.. inputs.Where(i => i.Name != omit.Name)],
                Template = template with
                {
                    Query = negativeQuery, Body = negativeBody,
                    Assertions = [new("status", AssertionKind.StatusCode, AssertionOperator.In, null, expected)], ExpectedOutcome = $"HTTP {expected.Replace(",", " or ", StringComparison.Ordinal)}",
                },
                Rationale = $"The contract requires {omit.In} value {omit.Name}; the request leaves it out and expects a client error (HTTP {expected.Replace(",", " or ", StringComparison.Ordinal)})"
                    + (clientErrors.Count == 0 ? ". The contract documents neither 400 nor 422, so both are accepted." : "."),
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
            else if ((t.ContentType ?? "").Contains("json", StringComparison.OrdinalIgnoreCase) && JsonNode.Parse(body) is JsonObject o)
            {
                foreach (var (k, v) in o.ToList())
                {
                    if (v is not JsonValue jv || jv.GetValueKind() != JsonValueKind.String || WholePlaceholder().Match(jv.GetValue<string>()) is not { Success: true } m) continue;
                    var name = m.Groups[1].Value;
                    if (values.TryGetValue(name, out var e)) o[k] = TestDataResourceRules.ToJson(e);
                    else if (optional.Contains(name)) o.Remove(k);
                    else if (!missing.Contains(name)) missing.Add(name);
                }
                body = o.ToJsonString();
            }
            else body = Replace(body, false);
        }
        return missing.Count > 0 ? (null, missing) : (t with { Path = path, Query = query, Headers = headers, Body = body }, missing);
    }

    /// <summary>Which of a test's inputs a set of value names covers: missing required and missing optional names.</summary>
    public static (List<string> Required, List<string> Optional) Missing(ContractTestDefinition test, IReadOnlyCollection<string> provided) =>
        ([.. test.Inputs.Where(i => i.Required && !provided.Contains(i.Name)).Select(i => i.Name)], [.. test.Inputs.Where(i => !i.Required && !provided.Contains(i.Name)).Select(i => i.Name)]);

    /// <summary>
    /// What the change from <paramref name="before"/> to <paramref name="after"/> means for one test. Removed operation → Broken. A required
    /// parameter added, a parameter or request body changed, or a required request field added/changed → NeedsUpdate. A changed success response
    /// or response type → Review (assertions may no longer hold). Only optional additions or unrelated changes → Unaffected.
    /// </summary>
    public static ContractTestImpact Impact(ContractTestDefinition test, SourceContract before, SourceContract after, int toVersion)
    {
        ContractTestImpact Result(ContractTestImpactLevel level, List<string> reasons) => new(test.Id, test.Name, test.Operation, test.ContractVersion, toVersion, level, reasons);
        if (test.ContractVersion == toVersion) return Result(ContractTestImpactLevel.Unaffected, [$"Generated from the latest version (v{toVersion})."]);
        var op = after.Operations.FirstOrDefault(o => o.Name == test.Operation);
        if (op is null) return Result(ContractTestImpactLevel.Broken, [$"{test.Operation} no longer exists in v{toVersion}."]);
        var old = before.Operations.FirstOrDefault(o => o.Name == test.Operation);
        var reasons = new List<string>();
        var level = ContractTestImpactLevel.Unaffected;
        void Raise(ContractTestImpactLevel l, string why) { reasons.Add(why); if (l > level) level = l; }
        foreach (var c in SourceEvidenceDiff.OperationDetailChanges(before, after).Where(c => c.Key.Split('|') is [_, var name, ..] && name == test.Operation))
        {
            var detail = c.Detail[(test.Operation.Length + 2)..];
            switch (c.ContractChange)
            {
                case ContractChangeClass.ParameterAddedOptional: Raise(ContractTestImpactLevel.Unaffected, $"Non-breaking: {detail}."); break;
                case ContractChangeClass.ResponseChanged: Raise(ContractTestImpactLevel.Review, $"{char.ToUpperInvariant(detail[0])}{detail[1..]} — check the expected outcome."); break;
                default: Raise(ContractTestImpactLevel.NeedsUpdate, $"{char.ToUpperInvariant(detail[0])}{detail[1..]}."); break;
            }
        }
        var requestTypes = new[] { TypeName(old?.RequestSchema), TypeName(op.RequestSchema) }.Where(t => t is not null).ToHashSet(StringComparer.Ordinal);
        var responseTypes = (old?.ResponseSchemas.Values.AsEnumerable() ?? []).Concat(op.ResponseSchemas.Values).Select(TypeName).Where(t => t is not null).ToHashSet(StringComparer.Ordinal);
        foreach (var c in SourceEvidenceDiff.ContractChanges(before, after).Where(c => c.Area is "Field" or "Enum value" or "Type"))
        {
            var typeName = c.Key.Split('|').Last().Split('.')[0];
            if (requestTypes.Contains(typeName))
                Raise(c.ContractChange is ContractChangeClass.FieldAddedOptional or ContractChangeClass.EnumValueAdded ? ContractTestImpactLevel.Unaffected : ContractTestImpactLevel.NeedsUpdate,
                    $"Request type {c.Detail} ({Label(c.ContractChange)}).");
            else if (responseTypes.Contains(typeName))
                Raise(c.ContractChange is ContractChangeClass.FieldAddedOptional or ContractChangeClass.EnumValueAdded or ContractChangeClass.FieldAddedRequired ? ContractTestImpactLevel.Unaffected : ContractTestImpactLevel.Review,
                    $"Response type {c.Detail} ({Label(c.ContractChange)}).");
        }
        if (reasons.Count == 0) reasons.Add($"No change to {test.Operation} or its types between v{test.ContractVersion} and v{toVersion}.");
        return Result(level, reasons);
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
