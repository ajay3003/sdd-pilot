using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BirkNext.ContractTesting;
using BirkNext.SourceDomains;
using BirkNext.TestData;

namespace BirkNext.Api.Services.ContractTesting;

/// <summary>
/// Typed synthetic input values for one contract test, derived from the exact stored contract version. With the document text the request
/// parameter and body schemas are read from the JSON Schema (via <see cref="OpenApiSchemaValidator"/>: <c>$ref</c>, <c>allOf</c>, <c>oneOf</c>/<c>anyOf</c>
/// (first alternative), <c>enum</c>, <c>const</c>, <c>default</c>, <c>type</c>, <c>format</c>, <c>minimum</c>/<c>maximum</c> (incl. exclusive),
/// <c>multipleOf</c>, <c>min/maxLength</c>, <c>pattern</c> (checked, never guessed), <c>min/maxItems</c>, <c>uniqueItems</c>, <c>required</c>,
/// <c>readOnly</c>, nested objects and arrays); without it (a source-backed version) the contract's typed model is used (types, required, enums).
/// GraphQL variables keep their GraphQL types (Int → number, Boolean → boolean, input objects → objects, lists → arrays).
/// <para>Never generated: person identities, credentials and protection classifications (they need approved Test Data or the environment);
/// examples are never copied (they may hold real-looking data). Identifiers of existing records (path parameters, <c>…Id</c> fields, GraphQL
/// <c>ID</c>) get a proposed value that a human must confirm. Deterministic: the same seed, contract and input give the same value.</para>
/// </summary>
public static partial class ContractTestDataRules
{
    public sealed record Generated(ContractGeneratedValue Meta, TestDataTypedEntry? Entry);

    private const int MaxDepth = 6;
    private const int MaxProperties = 40;
    private const int MaxItems = 20;

    private sealed record Gen(JsonNode? Value, bool HasValue, GeneratedValueOrigin Origin, GeneratedValueCertainty Certainty, string Rule, List<string> Reasons, string? Format = null)
    {
        public static Gen None(string reason) => new(null, false, GeneratedValueOrigin.SyntheticRule, GeneratedValueCertainty.Unresolved, "not generated", [reason]);
    }

    private sealed record Ctx(OpenApiSchemaValidator? Raw, SourceContract Doc, int Seed, bool GraphQl);

    /// <summary>One generated value per input of the test (the positive test's inputs are shared by its negative variants).</summary>
    public static List<Generated> Generate(SourceContract doc, OpenApiSchemaValidator? raw, ContractTestDefinition test, ContractOperation op, int seed)
    {
        var ctx = new Ctx(raw, doc, seed, test.Template.Protocol == BirkNext.ExecutionEvidence.ExecutionProtocol.GraphQl);
        var result = new List<Generated>();
        foreach (var input in test.Inputs)
        {
            var schema = SchemaOf(ctx, test, op, input);
            var g = schema is null ? Gen.None("The contract declares no type for this input.") : Value(ctx, schema, input.Name, input.Name, 0);
            // A path parameter always names an existing record of the target: a schema-valid value is still only a proposal.
            if (g.HasValue && g.Certainty == GeneratedValueCertainty.Certain && input.In == "path" && g.Origin == GeneratedValueOrigin.SyntheticRule)
                g = g with { Certainty = GeneratedValueCertainty.NeedsReview, Reasons = [.. g.Reasons, $"Path parameter: {input.Name} must identify an existing record in the target."] };
            var entry = g.HasValue ? Entry(input.Name, g) : null;
            result.Add(new(new ContractGeneratedValue(input.Name, input.In, input.Required, entry?.Type, entry is null ? null : Preview(entry), g.Origin, g.Certainty, g.Rule,
                g.Reasons.Count == 0 ? null : string.Join(" ", g.Reasons.Distinct(StringComparer.Ordinal).Take(6))), entry));
        }
        return result;
    }

    // ── Schemas ──────────────────────────────────────────────────────────────────────────────────────────────────────────────

    private static JsonObject? SchemaOf(Ctx ctx, ContractTestDefinition test, ContractOperation op, ContractTestInput input)
    {
        if (ctx.GraphQl)
        {
            if (input.In.StartsWith("input ", StringComparison.Ordinal))
            {
                var argument = op.ParameterDetails.FirstOrDefault(a => a.Name == input.In["input ".Length..]);
                var shape = ContractTestRules.TypeName(argument?.Type) is { } n ? ctx.Doc.Types.FirstOrDefault(t => t.Name == n) : null;
                var field = shape?.Fields.FirstOrDefault(f => f.Name == input.Name);
                return field is null ? Shape(ctx, input.Type, 0) : Shape(ctx, field.Type, 0);
            }
            return Shape(ctx, input.Type, 0);
        }
        var method = op.Kind.ToUpperInvariant();
        var template = op.Path ?? "";
        if (ctx.Raw is { } raw)
        {
            if (input.In is "path" or "query" or "header" && raw.ParameterSchema(method, template, input.Name, input.In) is { } ps) return raw.Resolve(ps.Schema);
            if (input.In == "body" && raw.RequestSchema(method, template, test.Template.ContentType).Schema is { } body)
            {
                var resolved = Merge(ctx, raw.Resolve(body), 0);
                if (input.Name == "body" && test.Template.Body is { } b && ContractTestRules.IsWholePlaceholder(b)) return resolved;
                if (resolved?["properties"]?[input.Name] is { } property) return raw.Resolve(property) ?? property as JsonObject;
            }
        }
        // Source-backed version: the contract's typed model.
        if (input.In == "body")
        {
            if (input.Name == "body") return Shape(ctx, op.RequestSchema ?? input.Type, 0);
            var type = ContractTestRules.TypeName(op.RequestSchema) is { } rn ? ctx.Doc.Types.FirstOrDefault(t => t.Name == rn) : null;
            return Shape(ctx, type?.Fields.FirstOrDefault(f => f.Name == input.Name)?.Type ?? input.Type, 0);
        }
        var p = op.ParameterDetails.FirstOrDefault(x => x.Name == input.Name && x.In == input.In);
        var schema = Shape(ctx, p?.Type ?? input.Type, 0);
        if (schema is not null && p?.Enum is { Count: > 0 } e) schema["enum"] = new JsonArray([.. e.Select(x => (JsonNode?)JsonValue.Create(x))]);
        if (schema is not null && p?.Default is { Length: > 0 } d) schema["default"] = JsonValue.Create(d);
        return schema;
    }

    private static readonly Dictionary<string, (string Type, string? Format)> GraphQlScalars = new(StringComparer.Ordinal)
    {
        ["String"] = ("string", null), ["Int"] = ("integer", "int32"), ["Float"] = ("number", null), ["Boolean"] = ("boolean", null), ["ID"] = ("string", "x-id"),
        ["UUID"] = ("string", "uuid"), ["Uuid"] = ("string", "uuid"), ["Guid"] = ("string", "uuid"), ["Date"] = ("string", "date"), ["LocalDate"] = ("string", "date"),
        ["DateTime"] = ("string", "date-time"), ["DateTimeOffset"] = ("string", "date-time"), ["Instant"] = ("string", "date-time"), ["Long"] = ("integer", "int64"),
        ["BigInt"] = ("integer", "int64"), ["Decimal"] = ("number", null), ["Short"] = ("integer", "int32"), ["Byte"] = ("integer", "int32"),
    };

    /// <summary>A JSON Schema built from a type text of the contract model (OpenAPI "string:date", "[Child]", a named type; GraphQL "[Int!]!").</summary>
    private static JsonObject? Shape(Ctx ctx, string? type, int depth)
    {
        if (string.IsNullOrWhiteSpace(type) || depth > MaxDepth) return null;
        var t = type.Trim();
        if (ctx.GraphQl) t = t.TrimEnd('!');
        if (t.StartsWith('[') && t.EndsWith(']')) return Shape(ctx, t[1..^1], depth + 1) is { } item ? new JsonObject { ["type"] = "array", ["items"] = item } : null;
        if (t.EndsWith("[]", StringComparison.Ordinal)) return Shape(ctx, t[..^2], depth + 1) is { } item2 ? new JsonObject { ["type"] = "array", ["items"] = item2 } : null;
        if (ctx.GraphQl && GraphQlScalars.TryGetValue(t, out var scalar))
            return scalar.Format is { } f ? new JsonObject { ["type"] = scalar.Type, ["format"] = f } : new JsonObject { ["type"] = scalar.Type };
        var parts = t.Split(':', 2);
        if (parts.Length == 1 && t.EndsWith(')') && t.IndexOf('(') is > 0 and var open) parts = [t[..open], t[(open + 1)..^1]]; // "string(date)"
        if (!ctx.GraphQl && parts[0] is "string" or "integer" or "number" or "boolean" or "object" or "array" or "null")
        {
            var s = new JsonObject { ["type"] = parts[0] };
            if (parts.Length > 1 && parts[1].Length > 0) s["format"] = parts[1];
            return s;
        }
        var named = t.Split('/').Last();
        if (ctx.Doc.Types.FirstOrDefault(x => x.Name == named) is not { } shape) return new JsonObject { ["x-unknown-type"] = named };
        switch (shape.Kind)
        {
            case "enum":
                return new JsonObject { ["type"] = "string", ["enum"] = new JsonArray([.. shape.Values.Select(v => (JsonNode?)JsonValue.Create(v))]) };
            case "scalar":
                return new JsonObject { ["x-scalar"] = named };
            default:
                if (shape.Kind is "string" or "integer" or "number" or "boolean")
                    return shape.Values.Count > 0 ? new JsonObject { ["type"] = shape.Kind, ["enum"] = new JsonArray([.. shape.Values.Select(v => (JsonNode?)JsonValue.Create(v))]) } : new JsonObject { ["type"] = shape.Kind };
                var props = new JsonObject();
                var required = new JsonArray();
                foreach (var f in shape.Fields.Take(MaxProperties))
                {
                    if (f.Name.Contains('[') || f.Name.Contains('.')) continue; // flattened paths of nested fields are not properties
                    props[f.Name] = Shape(ctx, f.Type, depth + 1) ?? new JsonObject();
                    if (f.Required) required.Add(f.Name);
                }
                return new JsonObject { ["type"] = "object", ["properties"] = props, ["required"] = required };
        }
    }

    /// <summary>allOf merged into one object schema (properties and required united); other keywords of the parent kept.</summary>
    private static JsonObject? Merge(Ctx ctx, JsonObject? schema, int depth)
    {
        if (schema?["allOf"] is not JsonArray all || depth > MaxDepth) return schema;
        var merged = new JsonObject();
        foreach (var (k, v) in schema) if (k != "allOf") merged[k] = v?.DeepClone();
        var props = merged["properties"] as JsonObject ?? new JsonObject();
        var required = merged["required"] as JsonArray ?? new JsonArray();
        foreach (var part in all)
        {
            var p = Merge(ctx, ctx.Raw?.Resolve(part) ?? part as JsonObject, depth + 1);
            if (p is null) continue;
            if (p["properties"] is JsonObject pp) foreach (var (k, v) in pp) props[k] = v?.DeepClone();
            if (p["required"] is JsonArray pr) foreach (var r in pr) if (r is not null && !required.Any(x => x?.ToString() == r.ToString())) required.Add(r.ToString());
            if (p["type"] is { } pt && merged["type"] is null) merged["type"] = pt.DeepClone();
        }
        merged["properties"] = props;
        merged["required"] = required;
        if (merged["type"] is null) merged["type"] = "object";
        return merged;
    }

    // ── Values ───────────────────────────────────────────────────────────────────────────────────────────────────────────────

    private static Gen Value(Ctx ctx, JsonObject schemaIn, string name, string path, int depth)
    {
        if (depth > MaxDepth) return Gen.None($"{path}: nested deeper than {MaxDepth} levels (recursive schema) — not generated.");
        var schema = Merge(ctx, ctx.Raw?.Resolve(schemaIn) ?? schemaIn, depth) ?? schemaIn;
        if (IsCredential(name)) return Gen.None($"{path}: credential — supplied by the Target Environment's authentication, never by Test Data.");
        if (IsIdentity(name)) return Gen.None($"{path}: person identity — needs an approved synthetic identity from Test Data (never generated).");
        if (IsClassification(name)) return Gen.None($"{path}: protection classification (Kode 6/7) — use an approved Kode 6/7 Test Data asset; never invented.");
        if (schema["x-scalar"]?.ToString() is { } scalar) return Gen.None($"{path}: custom scalar {scalar} has no generator mapping — map it or provide a value.");
        if (schema["x-unknown-type"]?.ToString() is { } unknown) return Gen.None($"{path}: type {unknown} is not described in this contract version.");
        if (schema.ContainsKey("const")) return Done(schema["const"]?.DeepClone(), GeneratedValueOrigin.ContractConstraint, "declared const");
        if (schema["enum"] is JsonArray en && en.FirstOrDefault(x => x is not null) is { } first)
            return Done(first.DeepClone(), GeneratedValueOrigin.ContractConstraint, $"first declared enum value ({en.Count} allowed)");
        if (schema["default"] is { } def && def.GetValueKind() is not JsonValueKind.Null) return Done(def.DeepClone(), GeneratedValueOrigin.SchemaDefault, "declared default");
        foreach (var composition in new[] { "oneOf", "anyOf" })
            if (schema[composition] is JsonArray alternatives && alternatives.Select(a => ctx.Raw?.Resolve(a) ?? a as JsonObject).FirstOrDefault(a => a is not null && a["type"]?.ToString() != "null") is { } alt)
            {
                var g = Value(ctx, alt, name, path, depth + 1);
                return g with { Rule = $"first {composition} alternative: {g.Rule}" };
            }

        var type = TypeOf(schema);
        var format = schema["format"]?.ToString();
        var r = Seeded(ctx.Seed, path);
        Gen result;
        switch (type)
        {
            case "string":
                result = Text(schema, name, path, format, r);
                break;
            case "integer":
            case "number":
                result = Number(schema, type, r);
                break;
            case "boolean":
                result = Done(JsonValue.Create(true), GeneratedValueOrigin.SyntheticRule, "boolean true");
                break;
            case "null":
                result = Done(null, GeneratedValueOrigin.ContractConstraint, "type null");
                break;
            case "array":
                result = Array(ctx, schema, name, path, depth);
                break;
            case "object":
                result = Object(ctx, schema, path, depth);
                break;
            default:
                return Gen.None($"{path}: the schema declares no type, so no safe value can be derived.");
        }
        // References to existing records: a schema-valid value is still only a proposal.
        if (result.HasValue && result.Certainty == GeneratedValueCertainty.Certain && result.Origin == GeneratedValueOrigin.SyntheticRule && type is "string" or "integer"
            && (IsReference(name) || format == "x-id"))
            result = result with { Certainty = GeneratedValueCertainty.NeedsReview, Reasons = [.. result.Reasons, $"{path} looks like the identifier of an existing record: confirm a value that exists in the target."] };
        return result;
    }

    private static Gen Done(JsonNode? value, GeneratedValueOrigin origin, string rule, string? format = null) =>
        new(value, true, origin, GeneratedValueCertainty.Certain, rule, [], format);

    private static string? TypeOf(JsonObject schema)
    {
        switch (schema["type"])
        {
            case JsonValue v when v.GetValueKind() == JsonValueKind.String: return v.GetValue<string>();
            case JsonArray a: return a.Select(x => x?.ToString()).FirstOrDefault(x => x is not null and not "null") ?? "null";
        }
        if (schema["properties"] is not null) return "object";
        if (schema["items"] is not null) return "array";
        return null;
    }

    private static Gen Text(JsonObject schema, string name, string path, string? format, uint r)
    {
        var inv = CultureInfo.InvariantCulture;
        (string? Value, string Rule, string? Problem) proposal = format switch
        {
            "date" => (new DateOnly(2026, 1, 1).AddDays((int)(r % 365)).ToString("yyyy-MM-dd", inv), "seeded date in 2026", null),
            "date-time" => (new DateTimeOffset(2026, 1, 1, 8, 0, 0, TimeSpan.Zero).AddMinutes(r % 525_000).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", inv), "seeded date-time in 2026 (UTC)", null),
            "time" => ($"{8 + r % 8:00}:{r % 60:00}:00", "seeded time", null),
            "uuid" or "x-id" => (DeterministicGuid(r).ToString("D"), "seeded uuid", null),
            "email" => ($"synthetic.{r % 100000:00000}@example.test", "synthetic address on the reserved example.test domain", null),
            "uri" or "url" or "uri-reference" => ($"https://example.test/synthetic/{r % 100000:00000}", "synthetic URL on the reserved example.test domain", null),
            "hostname" => ("synthetic.example.test", "reserved example.test host", null),
            "ipv4" => ($"192.0.2.{1 + r % 250}", "TEST-NET-1 address (RFC 5737)", null),
            "byte" => (Convert.ToBase64String(Encoding.UTF8.GetBytes($"synthetic-{r % 1000}")), "base64 of a synthetic text", null),
            "binary" => (null, "", $"{path}: binary content is not generated."),
            "password" => (null, "", $"{path}: password — never generated."),
            null or "" or "string" => (null, "", null),
            _ => ($"synthetic-{Slug(name)}-{r % 100000:00000}", $"seeded synthetic text (format {format} is not generated)", $"{path}: format {format} is not generated; confirm the value."),
        };
        if (proposal.Rule.Length == 0 && proposal.Problem is { } refused) return Gen.None(refused);
        var value = proposal.Value ?? $"synthetic-{Slug(name)}-{r % 100000:00000}";
        var rule = proposal.Value is null ? "seeded synthetic text" : proposal.Rule;
        var reasons = new List<string>();
        if (proposal.Problem is { } p) reasons.Add(p);
        if (proposal.Value is null)
        {
            var min = (int?)Int(schema["minLength"]);
            var max = (int?)Int(schema["maxLength"]);
            if (max is { } mx && value.Length > mx) value = mx <= 0 ? "" : value[..mx];
            if (min is { } mn && value.Length < mn) value = value.PadRight(Math.Min(mn, 4096), 'x');
            if (min is not null || max is not null) rule += $" within length {min ?? 0}–{(max is { } m2 ? m2.ToString(inv) : "∞")}";
        }
        var certainty = reasons.Count > 0 ? GeneratedValueCertainty.NeedsReview : GeneratedValueCertainty.Certain;
        if (schema["pattern"]?.ToString() is { } pattern)
        {
            var (ok, generated) = SatisfyPattern(pattern, value, r);
            if (ok) { value = generated; rule += $" matching pattern {Short(pattern)}"; }
            else { certainty = GeneratedValueCertainty.NeedsReview; reasons.Add($"{path}: pattern {Short(pattern)} is not satisfied by a generated value; enter a matching value."); }
        }
        return new(JsonValue.Create(value), true, GeneratedValueOrigin.SyntheticRule, certainty, rule, reasons, format);
    }

    /// <summary>The value when it already matches; else a value for the simple anchored forms (^\d{n}$, ^[0-9]{n,m}$, ^[A-Z]{n}$, ^[a-z]{n}$); else not satisfied.</summary>
    private static (bool Ok, string Value) SatisfyPattern(string pattern, string value, uint r)
    {
        bool Matches(string v)
        {
            try { return Regex.IsMatch(v, pattern, RegexOptions.None, TimeSpan.FromMilliseconds(100)); }
            catch (Exception ex) when (ex is ArgumentException or RegexMatchTimeoutException) { return false; }
        }
        if (Matches(value)) return (true, value);
        var m = SimplePatternRe().Match(pattern);
        if (!m.Success) return (false, value);
        var count = int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture);
        if (count is < 1 or > 64) return (false, value);
        var cls = m.Groups["cls"].Value;
        var alphabet = cls is @"\d" or "[0-9]" ? "0123456789" : cls == "[A-Z]" ? "ABCDEFGHIJKLMNOPQRSTUVWXYZ" : cls == "[a-z]" ? "abcdefghijklmnopqrstuvwxyz" : cls == "[A-Za-z0-9]" ? "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789" : null;
        if (alphabet is null) return (false, value);
        var sb = new StringBuilder();
        var seed = r;
        for (var i = 0; i < count; i++) { sb.Append(alphabet[(int)(seed % (uint)alphabet.Length)]); seed = seed * 1103515245 + 12345; }
        var candidate = sb.ToString();
        return Matches(candidate) ? (true, candidate) : (false, value);
    }

    private static Gen Number(JsonObject schema, string type, uint r)
    {
        var integer = type == "integer";
        var min = Int(schema["minimum"]);
        var max = Int(schema["maximum"]);
        // OpenAPI 3.0: boolean exclusive flags; 3.1: numeric exclusive bounds.
        if (schema["exclusiveMinimum"] is { } xmin) min = xmin.GetValueKind() == JsonValueKind.True ? min + (integer ? 1 : 0.01m) : xmin.GetValueKind() == JsonValueKind.Number ? Int(xmin) + (integer ? 1 : 0.01m) : min;
        if (schema["exclusiveMaximum"] is { } xmax) max = xmax.GetValueKind() == JsonValueKind.True ? max - (integer ? 1 : 0.01m) : xmax.GetValueKind() == JsonValueKind.Number ? Int(xmax) - (integer ? 1 : 0.01m) : max;
        decimal value = min ?? (max is { } m && m < 1 ? m : integer ? 1 + r % 99 : 1.5m);
        if (max is { } upper && value > upper) value = upper;
        var rule = min is not null ? "lowest valid value (declared minimum)" : max is not null && max < 1 ? "declared maximum" : integer ? "seeded integer 1–99" : "decimal 1.5";
        if (Int(schema["multipleOf"]) is { } step && step > 0)
        {
            value = Math.Ceiling(value / step) * step;
            rule += $", multiple of {step.ToString(CultureInfo.InvariantCulture)}";
        }
        if (integer) value = Math.Ceiling(value);
        if (min is { } lo && value < lo || max is { } hi && value > hi)
            return new(JsonValue.Create(value), true, GeneratedValueOrigin.SyntheticRule, GeneratedValueCertainty.NeedsReview, rule, ["No value satisfies the declared bounds together; enter one."]);
        JsonNode node = integer ? (value is >= int.MinValue and <= int.MaxValue ? JsonValue.Create((int)value) : JsonValue.Create((long)value)) : JsonValue.Create(value);
        return new(node, true, GeneratedValueOrigin.SyntheticRule, GeneratedValueCertainty.Certain, rule, [], schema["format"]?.ToString());
    }

    private static Gen Array(Ctx ctx, JsonObject schema, string name, string path, int depth)
    {
        var min = (int?)Int(schema["minItems"]) ?? 0;
        var max = (int?)Int(schema["maxItems"]);
        var count = Math.Max(min, 1);
        if (max is { } mx) count = Math.Min(count, mx);
        if (count > MaxItems) return Gen.None($"{path}: minItems {min} is above the generator's limit of {MaxItems} items.");
        if (schema["items"] is not JsonObject itemsSchema) return count == 0 ? Done(new JsonArray(), GeneratedValueOrigin.SyntheticRule, "empty list") : Gen.None($"{path}: the list declares no item type.");
        var unique = schema["uniqueItems"]?.GetValueKind() == JsonValueKind.True;
        var list = new JsonArray();
        var reasons = new List<string>();
        var certainty = GeneratedValueCertainty.Certain;
        var rule = "";
        for (var i = 0; i < count; i++)
        {
            var g = Value(ctx, itemsSchema, name, $"{path}[{i}]", depth + 1);
            if (!g.HasValue) return Gen.None(string.Join(" ", g.Reasons));
            if (unique && list.Any(x => JsonNode.DeepEquals(x, g.Value)) && itemsSchema["enum"] is JsonArray en && en.Count > i) g = g with { Value = en[i]?.DeepClone() };
            list.Add(g.Value);
            reasons.AddRange(g.Reasons);
            certainty = Worst(certainty, g.Certainty);
            rule = g.Rule;
        }
        return new(list, true, GeneratedValueOrigin.SyntheticRule, certainty, $"{count} item(s): {rule}".TrimEnd(' ', ':'), reasons);
    }

    private static Gen Object(Ctx ctx, JsonObject schema, string path, int depth)
    {
        var o = new JsonObject();
        var required = (schema["required"] as JsonArray)?.Select(x => x?.ToString()).OfType<string>().ToHashSet(StringComparer.Ordinal) ?? [];
        var reasons = new List<string>();
        var certainty = GeneratedValueCertainty.Certain;
        var omitted = 0;
        foreach (var (key, node) in (schema["properties"] as JsonObject ?? []).Take(MaxProperties))
        {
            if (node is not JsonObject propertySchema) continue;
            var resolved = ctx.Raw?.Resolve(propertySchema) ?? propertySchema;
            if (resolved["readOnly"]?.GetValueKind() == JsonValueKind.True) continue; // never sent in a request
            var g = Value(ctx, propertySchema, key, $"{path}.{key}", depth + 1);
            if (required.Contains(key))
            {
                if (!g.HasValue) return Gen.None(string.Join(" ", g.Reasons));
                o[key] = g.Value;
                reasons.AddRange(g.Reasons);
                certainty = Worst(certainty, g.Certainty);
            }
            else if (g.HasValue && g.Certainty == GeneratedValueCertainty.Certain) o[key] = g.Value; // optional: only certain values
            else omitted++;
        }
        var rule = $"object with {o.Count} propert{(o.Count == 1 ? "y" : "ies")} ({required.Count} required)" + (omitted > 0 ? $"; {omitted} optional left out" : "");
        return new(o, true, GeneratedValueOrigin.SyntheticRule, certainty, rule, reasons);
    }

    private static GeneratedValueCertainty Worst(GeneratedValueCertainty a, GeneratedValueCertainty b) => (GeneratedValueCertainty)Math.Max((int)a, (int)b);

    // ── Typed entries ────────────────────────────────────────────────────────────────────────────────────────────────────────

    private static TestDataTypedEntry Entry(string name, Gen g)
    {
        var inv = CultureInfo.InvariantCulture;
        switch (g.Value)
        {
            case null: return new(name, TestDataPrimitive.Null, null);
            case JsonObject or JsonArray: return new(name, TestDataPrimitive.Json, g.Value.ToJsonString());
        }
        var v = (JsonValue)g.Value;
        switch (v.GetValueKind())
        {
            case JsonValueKind.True or JsonValueKind.False: return new(name, TestDataPrimitive.Boolean, v.GetValueKind() == JsonValueKind.True ? "true" : "false");
            case JsonValueKind.Number:
                var d = JsonNumber.Of(v);
                if (d % 1 == 0 && g.Format != "double" && g.Format != "float" && !v.ToJsonString().Contains('.'))
                    return d is >= int.MinValue and <= int.MaxValue && g.Format != "int64" ? new(name, TestDataPrimitive.Int32, ((int)d).ToString(inv)) : new(name, TestDataPrimitive.Int64, ((long)d).ToString(inv));
                return new(name, TestDataPrimitive.Decimal, d.ToString(inv));
            default:
                var text = v.ToString();
                return g.Format switch
                {
                    "date" when DateOnly.TryParseExact(text, "yyyy-MM-dd", inv, DateTimeStyles.None, out _) => new(name, TestDataPrimitive.Date, text),
                    "date-time" when DateTimeOffset.TryParse(text, inv, DateTimeStyles.None, out _) => new(name, TestDataPrimitive.DateTime, text),
                    "uuid" or "x-id" when Guid.TryParse(text, out _) => new(name, TestDataPrimitive.Uuid, text),
                    _ => new(name, TestDataPrimitive.String, text),
                };
        }
    }

    private static string Preview(TestDataTypedEntry e) => e.Value is null ? "null" : e.Value.Length > 160 ? e.Value[..160] + "…" : e.Value;

    // ── Field classes ────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Person identity (national id, D-number, DUF, BirkId, …): never generated.</summary>
    public static bool IsIdentity(string name) => IdentityRe().IsMatch(name);
    /// <summary>Credentials and secrets: the environment's, never Test Data.</summary>
    public static bool IsCredential(string name) => CredentialRe().IsMatch(name);
    /// <summary>Protection classification (Kode 6/7, address protection, grading): approved Test Data only, never invented.</summary>
    public static bool IsClassification(string name) => ClassificationRe().IsMatch(name);
    /// <summary>The identifier of an existing record (…Id, …_id, id, …nr, …nummer), except per-request technical ids (correlation, trace, …).</summary>
    public static bool IsReference(string name) => ReferenceRe().IsMatch(name) && !TechnicalIdRe().IsMatch(name);

    [GeneratedRegex(@"(fodsels|fødsels|personnummer|personnr|^fnr$|\bfnr\b|ssn|nationalid|national_id|duf|birkid|birk_id|dnummer|d-nummer|personident|personid$)", RegexOptions.IgnoreCase)]
    private static partial Regex IdentityRe();
    [GeneratedRegex(@"(password|passord|secret|token|api[-_]?key|credential|authorization|^cookie$|client[-_]?secret|private[-_]?key)", RegexOptions.IgnoreCase)]
    private static partial Regex CredentialRe();
    [GeneratedRegex(@"(adressebeskyttelse|addressprotection|address_protection|gradering|kode[-_ ]?[67]|strengt[-_ ]?fortrolig|confidentiality|classificationlevel|classification_level)", RegexOptions.IgnoreCase)]
    private static partial Regex ClassificationRe();
    [GeneratedRegex(@"(^id$|Id$|ID$|_id$|-id$|nr$|Nr$|nummer$|Nummer$)")]
    private static partial Regex ReferenceRe();
    [GeneratedRegex(@"(correlation|request|trace|span|message|idempotency|transaction|session)", RegexOptions.IgnoreCase)]
    private static partial Regex TechnicalIdRe();
    [GeneratedRegex(@"^\^(?<cls>\\d|\[0-9\]|\[A-Z\]|\[a-z\]|\[A-Za-z0-9\])\{(?<n>\d+)\}\$$")]
    private static partial Regex SimplePatternRe();
    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex SlugRe();

    private static string Slug(string s) => SlugRe().Replace(s.ToLowerInvariant(), "-").Trim('-');
    private static string Short(string s) => s.Length > 40 ? s[..40] + "…" : s;
    private static decimal? Int(JsonNode? n) => n?.GetValueKind() == JsonValueKind.Number ? JsonNumber.Of(n) : null;

    /// <summary>Stable across processes (never string.GetHashCode).</summary>
    private static uint Seeded(int seed, string path) => BitConverter.ToUInt32(SHA256.HashData(Encoding.UTF8.GetBytes($"{seed}|{path}")), 0);
    private static Guid DeterministicGuid(uint r) => new(SHA256.HashData(Encoding.UTF8.GetBytes($"guid|{r}")).AsSpan(0, 16));
}
