using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml;
using BirkNext.SourceDomains;

namespace BirkNext.TestData;

// Authored Test Data resources: synthetic values a tester adds in Test Data ("Add data"). One catalog: they are offered through the same
// Test Data catalog, approval, versioning and consumer matching as every other asset. These contracts and rules are shared so the editor
// validates exactly as the backend does before saving. Values are typed: numbers, booleans and null stay JSON numbers, booleans and null.

/// <summary>What a tester adds. XML is authored in the existing XML fixture editor, never in a second editor.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TestDataResourceKind { SingleValue, KeyValue, Json, GraphQlVariables, RestRequest, Dataset, Generated, FileReference }

/// <summary>Exact value types. Machine-readable text is invariant (ISO-8601 dates, '.' decimals); Null is an explicit marker, not "".</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TestDataPrimitive { String, Int32, Int64, Decimal, Double, Boolean, Null, Date, Time, DateTime, Duration, Uuid, Enum, Json }

/// <summary>One typed value. <see cref="Value"/> is the invariant text (null only for <see cref="TestDataPrimitive.Null"/>); a Json entry holds
/// an object or array as JSON text.</summary>
public sealed record TestDataTypedEntry(string Name, TestDataPrimitive Type, string? Value, bool Required = true)
{
    public List<string>? EnumValues { get; init; }
}

/// <summary>A REST parameter of a request template: path, query, header or cookie.</summary>
public sealed record TestDataRequestParameter(string Name, string In, TestDataPrimitive Type, string? Value, bool Required = true);

/// <summary>A discovered contract operation the resource is meant for (a Source Analysis snapshot's contract id + operation name).</summary>
public sealed record TestDataOperationRef(Guid? SnapshotId, string ContractId, string Operation);

/// <summary>A dataset member: a reference to another resource (alias, optionally pinned "alias@vN") or an inline typed value.</summary>
public sealed record TestDataDatasetMember(string Name, string? Reference, TestDataTypedEntry? Inline);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TestDataGeneratorKind { Uuid, Sequence, DateOffset, Timestamp, IntegerRange, DecimalRange, EnumChoice, FakeName, FakeEmail, FakeAddress, Pattern }

/// <summary>A generator definition: stored as defined; values are materialized per execution (deterministic for a fixed seed).</summary>
public sealed record TestDataGeneratorDefinition(TestDataGeneratorKind Kind, int? Seed = null)
{
    public long? Start { get; init; }
    public long? Step { get; init; }
    public decimal? Min { get; init; }
    public decimal? Max { get; init; }
    public int? Scale { get; init; }
    public int? OffsetDays { get; init; }
    public List<string>? Values { get; init; }
    /// <summary>Pattern generator: literal characters plus '#' (digit), 'A' (upper letter), 'a' (lower letter). Never a national id pattern.</summary>
    public string? Pattern { get; init; }
}

/// <summary>The typed content of an authored resource; only the part that belongs to <see cref="Kind"/> is used.</summary>
public sealed record TestDataResourceContent
{
    public TestDataResourceKind Kind { get; init; }
    public TestDataTypedEntry? Value { get; init; }
    public List<TestDataTypedEntry> Entries { get; init; } = [];
    /// <summary>JSON document text (Json) or the variables object (GraphQlVariables).</summary>
    public string? Json { get; init; }
    public TestDataOperationRef? Operation { get; init; }
    public string? Method { get; init; }
    public string? Path { get; init; }
    public List<TestDataRequestParameter> Parameters { get; init; } = [];
    public string? ContentType { get; init; }
    public string? Body { get; init; }
    /// <summary>A REST template never stores credentials: authentication comes from the Target Environment's configured authentication.</summary>
    public bool UseTargetAuthentication { get; init; }
    public List<TestDataDatasetMember> Members { get; init; } = [];
    public TestDataGeneratorDefinition? Generator { get; init; }
    public string? FilePath { get; init; }
    public string? MediaType { get; init; }
}

/// <summary>Save request. <see cref="ExpectedVersion"/> is the version the editor started from (null = create); a mismatch is a conflict.</summary>
public sealed record TestDataResourceDraft
{
    public string Alias { get; init; } = "";
    public string? Label { get; init; }
    public string? Description { get; init; }
    /// <summary>Environment id, or null for Shared.</summary>
    public string? EnvironmentId { get; init; }
    public List<string> AppliesTo { get; init; } = [];
    public TestDataResourceContent Content { get; init; } = new();
    public int? ExpectedVersion { get; init; }
    public string? ChangeNote { get; init; }
}

public sealed record TestDataResourceVersionInfo(int Version, DateTimeOffset CreatedAt, string Fingerprint, string? ChangeNote);

/// <summary>An authored resource as the editor reads it. Authored values are synthetic and non-sensitive by rule (credential-like input is refused).</summary>
public sealed record TestDataResourceView
{
    public string Alias { get; init; } = "";
    public string? AssetId { get; init; }
    public string? Label { get; init; }
    public string? Description { get; init; }
    public string? EnvironmentId { get; init; }
    public List<string> AppliesTo { get; init; } = [];
    public TestDataResourceContent Content { get; init; } = new();
    public int Version { get; init; }
    public List<TestDataResourceVersionInfo> Versions { get; init; } = [];
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed record TestDataResourceSaveResult(bool Saved, TestDataResourceView? Resource, TestDataResourceValidation Validation, string? Message, bool Conflict = false);

/// <summary>A discovered operation an authored REST template or GraphQL variables document can target (names and declared types only).</summary>
public sealed record TestDataOperationOption(Guid SnapshotId, string ContractId, string ContractName, string ContractType, ContractOperation Operation,
    List<ContractTypeShape> Types);

/// <summary>Readiness of one resource for one operation: validation tiers plus approval. Ready needs no failure and an approved current version.</summary>
public sealed record TestDataResourceReadiness(string Alias, int Version, bool Approved, bool Ready, TestDataResourceValidation Validation, List<string> Blockers);

/// <summary>Validation tiers are reported separately and never collapsed into one "valid".</summary>
public static class TestDataCheckTiers
{
    public const string Syntax = "Syntax", Type = "Type", Contract = "Contract", References = "References", Sensitive = "Sensitive data", Size = "Size";
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TestDataCheckState { Passed, Failed, Warning, NotAssessed }

public sealed record TestDataCheck(string Tier, TestDataCheckState State, string Message, string? Location = null);

public sealed record TestDataResourceValidation(List<TestDataCheck> Checks)
{
    public bool Blocking => Checks.Any(c => c.State == TestDataCheckState.Failed);
    public TestDataCheckState Tier(string tier) =>
        Checks.Where(c => c.Tier == tier).Select(c => c.State).DefaultIfEmpty(TestDataCheckState.NotAssessed)
            .Aggregate((a, b) => a == TestDataCheckState.Failed || b == TestDataCheckState.Failed ? TestDataCheckState.Failed
                : a == TestDataCheckState.Warning || b == TestDataCheckState.Warning ? TestDataCheckState.Warning
                : a == TestDataCheckState.NotAssessed && b == TestDataCheckState.NotAssessed ? TestDataCheckState.NotAssessed : TestDataCheckState.Passed);
}

/// <summary>What the validator may know besides the draft: other resources (for references) and the selected contract operation.</summary>
public sealed record TestDataValidationContext
{
    /// <summary>Aliases that exist in the Test Data catalog (any provider).</summary>
    public IReadOnlyCollection<string> KnownAliases { get; init; } = [];
    /// <summary>Authored datasets' member references (alias → referenced aliases), for cycle detection.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> DatasetReferences { get; init; } = new Dictionary<string, IReadOnlyList<string>>();
    /// <summary>The contract operation the content targets, when one is selected and was found.</summary>
    public ContractOperation? Operation { get; init; }
    /// <summary>Contract types (GraphQL input types and enums) of the operation's contract.</summary>
    public IReadOnlyList<ContractTypeShape> Types { get; init; } = [];
    /// <summary>The operation was selected but could not be found in the snapshot (contract missing / changed).</summary>
    public bool OperationMissing { get; init; }
}

public static partial class TestDataResourceRules
{
    public const int MaxPayloadBytes = 256 * 1024;
    public const int MaxJsonDepth = 32;
    public static readonly string[] Methods = ["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS"];
    public static readonly string[] ParameterLocations = ["path", "query", "header", "cookie"];
    public static readonly string[] ContentTypes = ["application/json", "application/xml", "application/x-www-form-urlencoded", "multipart/form-data", "text/plain", "application/octet-stream"];

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,63}$", RegexOptions.CultureInvariant)] private static partial Regex AliasPattern();
    [GeneratedRegex(@"(?i)(password|passwd|secret|token|api[-_]?key|authorization|cookie|session[-_]?id|connection[-_]?string|private[-_]?key|client[-_]?secret|bearer)")]
    private static partial Regex SensitiveName();
    [GeneratedRegex(@"(?i)^\s*(bearer|basic)\s+\S{8,}|eyJ[A-Za-z0-9_-]{8,}\.eyJ[A-Za-z0-9_-]{8,}\.|-----BEGIN [A-Z ]*PRIVATE KEY-----|(?:AccountKey|SharedAccessKey|Password)=")]
    private static partial Regex SensitiveValue();
    [GeneratedRegex(@"(?<!\d)\d{11}(?!\d)")] private static partial Regex ElevenDigits();
    [GeneratedRegex(@"\{([A-Za-z_][A-Za-z0-9_]*)\}")] private static partial Regex PathParameter();
    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_.\-\[\]]{0,127}$")] private static partial Regex EntryName();

    public static string Label(TestDataResourceKind kind) => kind switch
    {
        TestDataResourceKind.SingleValue => "Single value",
        TestDataResourceKind.KeyValue => "Key-value collection",
        TestDataResourceKind.Json => "JSON",
        TestDataResourceKind.GraphQlVariables => "GraphQL variables",
        TestDataResourceKind.RestRequest => "REST request template",
        TestDataResourceKind.Dataset => "Dataset",
        TestDataResourceKind.Generated => "Generated data",
        _ => "File reference",
    };

    public static string Label(TestDataPrimitive type) => type switch
    {
        TestDataPrimitive.Int32 => "Integer (32-bit)",
        TestDataPrimitive.Int64 => "Long (64-bit)",
        TestDataPrimitive.Double => "Float / double",
        TestDataPrimitive.Uuid => "UUID",
        TestDataPrimitive.DateTime => "Date-time (ISO-8601)",
        TestDataPrimitive.Duration => "Duration (ISO-8601)",
        TestDataPrimitive.Json => "Object / array (JSON)",
        _ => type.ToString(),
    };

    /// <summary>The example shown next to a type, in the invariant format the value must use.</summary>
    public static string Format(TestDataPrimitive type) => type switch
    {
        TestDataPrimitive.Int32 => "e.g. 42",
        TestDataPrimitive.Int64 => "e.g. 9007199254740993",
        TestDataPrimitive.Decimal => "e.g. 1234.50 ('.' decimal separator)",
        TestDataPrimitive.Double => "e.g. 1.5e3",
        TestDataPrimitive.Boolean => "true or false",
        TestDataPrimitive.Date => "yyyy-MM-dd",
        TestDataPrimitive.Time => "HH:mm or HH:mm:ss",
        TestDataPrimitive.DateTime => "yyyy-MM-ddTHH:mm:ss with offset or Z",
        TestDataPrimitive.Duration => "e.g. PT15M, P1D",
        TestDataPrimitive.Uuid => "8-4-4-4-12 hexadecimal",
        TestDataPrimitive.Json => "a JSON object or array",
        _ => "",
    };

    /// <summary>Parses one typed value exactly (invariant culture). Null when valid, otherwise the reason.</summary>
    public static string? TypeError(TestDataPrimitive type, string? value, IReadOnlyCollection<string>? enumValues = null)
    {
        if (type == TestDataPrimitive.Null) return value is null ? null : "A Null value must be the explicit null marker, not text.";
        if (value is null) return "A value is required (use the Null type for an explicit null).";
        var inv = CultureInfo.InvariantCulture;
        return type switch
        {
            TestDataPrimitive.String => null,
            TestDataPrimitive.Int32 => int.TryParse(value, NumberStyles.AllowLeadingSign, inv, out _) ? null : "Not a 32-bit integer.",
            TestDataPrimitive.Int64 => long.TryParse(value, NumberStyles.AllowLeadingSign, inv, out _) ? null : "Not a 64-bit integer.",
            TestDataPrimitive.Decimal => decimal.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, inv, out _) ? null : "Not a decimal (use '.' as decimal separator, no grouping).",
            TestDataPrimitive.Double => double.TryParse(value, NumberStyles.Float, inv, out var d) && double.IsFinite(d) ? null : "Not a finite floating-point number.",
            TestDataPrimitive.Boolean => value is "true" or "false" ? null : "A boolean is exactly true or false.",
            TestDataPrimitive.Date => DateOnly.TryParseExact(value, "yyyy-MM-dd", inv, DateTimeStyles.None, out _) ? null : "Not a date (yyyy-MM-dd).",
            TestDataPrimitive.Time => TimeOnly.TryParseExact(value, ["HH:mm", "HH:mm:ss", "HH:mm:ss.FFFFFFF"], inv, DateTimeStyles.None, out _) ? null : "Not a time (HH:mm or HH:mm:ss).",
            TestDataPrimitive.DateTime => value.Contains('T') && DateTimeOffset.TryParse(value, inv, DateTimeStyles.RoundtripKind, out _)
                && (value.EndsWith('Z') || Regex.IsMatch(value, @"[+-]\d{2}:\d{2}$")) ? null : "Not an ISO-8601 date-time with offset or Z.",
            TestDataPrimitive.Duration => DurationError(value),
            TestDataPrimitive.Uuid => Guid.TryParseExact(value, "D", out _) ? null : "Not a UUID (8-4-4-4-12).",
            TestDataPrimitive.Enum => enumValues is { Count: > 0 } allowed ? allowed.Contains(value) ? null : $"Not one of the allowed values ({string.Join(", ", allowed)})." : "An enum needs its allowed values.",
            TestDataPrimitive.Json => JsonError(value, requireContainer: true).Error,
            _ => null,
        };
    }

    private static string? DurationError(string value)
    {
        try { _ = XmlConvert.ToTimeSpan(value); return value.StartsWith('P') || value.StartsWith("-P", StringComparison.Ordinal) ? null : "Not an ISO-8601 duration."; }
        catch (FormatException) { return "Not an ISO-8601 duration (e.g. PT15M)."; }
        catch (OverflowException) { return "Duration out of range."; }
    }

    /// <summary>The value as a typed JSON node: numbers, booleans and null are never strings.</summary>
    public static JsonNode? ToJson(TestDataTypedEntry entry)
    {
        var inv = CultureInfo.InvariantCulture;
        return entry.Type switch
        {
            TestDataPrimitive.Null => null,
            TestDataPrimitive.Int32 => JsonValue.Create(int.Parse(entry.Value!, NumberStyles.AllowLeadingSign, inv)),
            TestDataPrimitive.Int64 => JsonValue.Create(long.Parse(entry.Value!, NumberStyles.AllowLeadingSign, inv)),
            TestDataPrimitive.Decimal => JsonValue.Create(decimal.Parse(entry.Value!, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, inv)),
            TestDataPrimitive.Double => JsonValue.Create(double.Parse(entry.Value!, NumberStyles.Float, inv)),
            TestDataPrimitive.Boolean => JsonValue.Create(entry.Value == "true"),
            TestDataPrimitive.Json => JsonNode.Parse(entry.Value!),
            _ => JsonValue.Create(entry.Value),
        };
    }

    /// <summary>Parses JSON with a depth limit; on failure the line and column (1-based) of the error.</summary>
    public static (JsonNode? Node, string? Error) JsonError(string? text, bool requireContainer = false)
    {
        if (string.IsNullOrWhiteSpace(text)) return (null, "Empty: enter a JSON document.");
        try
        {
            var node = JsonNode.Parse(text, null, new JsonDocumentOptions { MaxDepth = MaxJsonDepth });
            if (requireContainer && node is not (JsonObject or JsonArray)) return (null, "Expected a JSON object or array.");
            return (node, null);
        }
        catch (JsonException ex) { return (null, $"Invalid JSON at line {(ex.LineNumber ?? 0) + 1}, column {(ex.BytePositionInLine ?? 0) + 1}."); }
    }

    /// <summary>Well-formed XML with DTDs prohibited and no external resolution.</summary>
    public static string? XmlError(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "Empty: enter an XML document.";
        try
        {
            using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxPayloadBytes });
            while (reader.Read()) { }
            return null;
        }
        catch (XmlException ex) { return $"Invalid XML at line {ex.LineNumber}, column {ex.LinePosition}: {ex.Message.Split('.')[0]}."; }
    }

    /// <summary>The canonical stored payload: typed JSON for structured kinds, so a consumer never sees numbers as text.</summary>
    public static string Payload(TestDataResourceContent c) => c.Kind switch
    {
        TestDataResourceKind.SingleValue => c.Value is null ? "" : c.Value.Type is TestDataPrimitive.Null ? "null" : ToJson(c.Value)!.ToJsonString(),
        TestDataResourceKind.KeyValue => Object(c.Entries).ToJsonString(),
        TestDataResourceKind.Json or TestDataResourceKind.GraphQlVariables => JsonError(c.Json).Node?.ToJsonString() ?? c.Json ?? "",
        _ => JsonSerializer.Serialize(c, PayloadJson),
    };

    private static readonly JsonSerializerOptions PayloadJson = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public static JsonObject Object(IEnumerable<TestDataTypedEntry> entries)
    {
        var o = new JsonObject();
        foreach (var e in entries.Where(e => TypeError(e.Type, e.Value, e.EnumValues) is null && !o.ContainsKey(e.Name))) o[e.Name] = ToJson(e);
        return o;
    }

    public static TestDataResourceValidation Validate(TestDataResourceDraft draft, TestDataValidationContext? context = null)
    {
        context ??= new();
        var checks = new List<TestDataCheck>();
        void Add(string tier, TestDataCheckState state, string message, string? location = null) => checks.Add(new(tier, state, message, location));
        var c = draft.Content;

        if (!AliasPattern().IsMatch(draft.Alias ?? "")) Add(TestDataCheckTiers.Syntax, TestDataCheckState.Failed, "Name: lower-case letters, digits and '-', starting with a letter or digit (max 64).", "name");
        var size = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(c, PayloadJson));
        if (size > MaxPayloadBytes) Add(TestDataCheckTiers.Size, TestDataCheckState.Failed, $"Content is {size / 1024} KB; the limit is {MaxPayloadBytes / 1024} KB.");

        switch (c.Kind)
        {
            case TestDataResourceKind.SingleValue:
                if (c.Value is null) Add(TestDataCheckTiers.Syntax, TestDataCheckState.Failed, "Enter a value.", "value");
                else Typed(c.Value, "value");
                break;
            case TestDataResourceKind.KeyValue:
                if (c.Entries.Count == 0) Add(TestDataCheckTiers.Syntax, TestDataCheckState.Failed, "Add at least one row.", "entries");
                Rows(c.Entries.Select(e => (e.Name, (TestDataTypedEntry?)e)).ToList(), "entries");
                break;
            case TestDataResourceKind.Json:
                if (JsonError(c.Json) is { Error: { } jsonError }) Add(TestDataCheckTiers.Syntax, TestDataCheckState.Failed, jsonError, "json");
                else { Add(TestDataCheckTiers.Syntax, TestDataCheckState.Passed, "Well-formed JSON."); Scan(JsonError(c.Json).Node, "json"); }
                Add(TestDataCheckTiers.Contract, TestDataCheckState.NotAssessed, "No JSON Schema selected: contract validity is not assessed.");
                break;
            case TestDataResourceKind.GraphQlVariables:
                GraphQl();
                break;
            case TestDataResourceKind.RestRequest:
                Rest();
                break;
            case TestDataResourceKind.Dataset:
                Dataset();
                break;
            case TestDataResourceKind.Generated:
                Generated();
                break;
            case TestDataResourceKind.FileReference:
                var path = (c.FilePath ?? "").Replace('\\', '/');
                if (path.Length == 0 || path.StartsWith('/') || path.Contains(':') || path.Split('/').Any(s => s is ".." or "."))
                    Add(TestDataCheckTiers.Syntax, TestDataCheckState.Failed, "A file reference is a relative path inside the Test Data folder (no '..', no drive or absolute path).", "file");
                else Add(TestDataCheckTiers.Syntax, TestDataCheckState.Passed, "Relative path inside the Test Data folder.");
                Add(TestDataCheckTiers.References, TestDataCheckState.NotAssessed, "The file itself is not read when saving; its presence is checked when a consumer resolves it.");
                break;
        }
        if (!checks.Any(x => x.Tier == TestDataCheckTiers.Type) && c.Kind is not TestDataResourceKind.FileReference)
            Add(TestDataCheckTiers.Type, checks.Any(x => x.State == TestDataCheckState.Failed && x.Tier == TestDataCheckTiers.Syntax) ? TestDataCheckState.NotAssessed : TestDataCheckState.Passed, "Types checked.");
        if (!checks.Any(x => x.Tier == TestDataCheckTiers.Syntax)) Add(TestDataCheckTiers.Syntax, TestDataCheckState.Passed, "Well-formed.");
        if (!checks.Any(x => x.Tier == TestDataCheckTiers.Sensitive)) Add(TestDataCheckTiers.Sensitive, TestDataCheckState.Passed, "No credential-like or identity-number values found.");
        return new(checks);

        void Typed(TestDataTypedEntry e, string location)
        {
            if (TypeError(e.Type, e.Value, e.EnumValues) is { } error) Add(TestDataCheckTiers.Type, TestDataCheckState.Failed, $"{(e.Name.Length > 0 ? e.Name + ": " : "")}{error}", location);
            Sensitive(e.Name, e.Value, location);
            if (e.Type == TestDataPrimitive.Json && e.Value is not null) Scan(JsonError(e.Value).Node, location);
        }

        void Rows(List<(string Name, TestDataTypedEntry? Entry)> rows, string location)
        {
            foreach (var group in rows.GroupBy(r => r.Name, StringComparer.Ordinal).Where(g => g.Count() > 1))
                Add(TestDataCheckTiers.Syntax, TestDataCheckState.Failed, $"Duplicate key \"{group.Key}\": keys of one collection are unique.", location);
            for (var i = 0; i < rows.Count; i++)
            {
                if (!EntryName().IsMatch(rows[i].Name ?? "")) Add(TestDataCheckTiers.Syntax, TestDataCheckState.Failed, $"Row {i + 1}: a key is required (letters, digits, '_', '-', '.').", $"{location}[{i}]");
                if (rows[i].Entry is { } e) Typed(e, $"{location}[{i}]");
            }
        }

        void Sensitive(string? name, string? value, string location)
        {
            if (value is null) return;
            if (name is not null && SensitiveName().IsMatch(name) && value.Length > 0 && !value.StartsWith("{{", StringComparison.Ordinal))
                Add(TestDataCheckTiers.Sensitive, TestDataCheckState.Failed, $"\"{name}\" looks like a credential. Test Data never stores credentials; use the Target Environment's authentication.", location);
            else if (SensitiveValue().IsMatch(value))
                Add(TestDataCheckTiers.Sensitive, TestDataCheckState.Failed, "A value looks like a token, key or password. Test Data never stores credentials.", location);
            else if (ElevenDigits().IsMatch(value))
                Add(TestDataCheckTiers.Sensitive, TestDataCheckState.Failed, "An 11-digit number may be a national identity number. Use a Test Data identity reference instead of a literal.", location);
        }

        void Scan(JsonNode? node, string location)
        {
            switch (node)
            {
                case JsonObject o: foreach (var (k, v) in o) { if (v is JsonValue jv) Sensitive(k, jv.ToString(), $"{location}.{k}"); else Scan(v, $"{location}.{k}"); } break;
                case JsonArray a: for (var i = 0; i < a.Count; i++) { if (a[i] is JsonValue jv) Sensitive(null, jv.ToString(), $"{location}[{i}]"); else Scan(a[i], $"{location}[{i}]"); } break;
            }
        }

        void Contract(string what)
        {
            if (c.Operation is null) Add(TestDataCheckTiers.Contract, TestDataCheckState.NotAssessed, $"No operation selected: {what} is not validated against a contract.");
            else if (context.OperationMissing || context.Operation is null)
                Add(TestDataCheckTiers.Contract, TestDataCheckState.Failed, $"Contract missing: operation \"{c.Operation.Operation}\" was not found in the selected source snapshot.");
        }

        void GraphQl()
        {
            var (node, error) = JsonError(c.Json);
            if (error is not null) { Add(TestDataCheckTiers.Syntax, TestDataCheckState.Failed, error, "variables"); Contract("the variables document"); return; }
            if (node is not JsonObject variables) { Add(TestDataCheckTiers.Syntax, TestDataCheckState.Failed, "GraphQL variables are a JSON object.", "variables"); return; }
            Add(TestDataCheckTiers.Syntax, TestDataCheckState.Passed, "Well-formed variables object.");
            Scan(variables, "variables");
            Contract("the variables document");
            if (context.Operation is not { } op) return;
            foreach (var finding in TestDataOperationCompatibility.GraphQl(op, context.Types, variables)) checks.Add(finding);
        }

        void Rest()
        {
            if (!Methods.Contains(c.Method ?? "")) Add(TestDataCheckTiers.Syntax, TestDataCheckState.Failed, "Choose an HTTP method.", "method");
            if (string.IsNullOrWhiteSpace(c.Path) || !c.Path.StartsWith('/') || c.Path.Contains("://", StringComparison.Ordinal))
                Add(TestDataCheckTiers.Syntax, TestDataCheckState.Failed, "The path is relative to the target (starts with '/'; no scheme or host).", "path");
            var parameters = c.Parameters.Select((p, i) => (p, i)).ToList();
            foreach (var (p, i) in parameters)
            {
                if (!ParameterLocations.Contains(p.In)) Add(TestDataCheckTiers.Syntax, TestDataCheckState.Failed, $"Parameter {i + 1}: location is path, query, header or cookie.", $"parameters[{i}]");
                if (p.In is "header" or "cookie" && SensitiveName().IsMatch(p.Name))
                    Add(TestDataCheckTiers.Sensitive, TestDataCheckState.Failed, $"\"{p.Name}\" is an authentication {p.In}. Templates never store credentials; enable \"Use the target's authentication\".", $"parameters[{i}]");
                else Typed(new TestDataTypedEntry(p.Name, p.Type, p.Value, p.Required), $"parameters[{i}]");
            }
            foreach (var dup in c.Parameters.GroupBy(p => (p.In, p.In == "header" ? p.Name.ToLowerInvariant() : p.Name)).Where(g => g.Count() > 1))
                Add(TestDataCheckTiers.Syntax, TestDataCheckState.Failed, $"Duplicate {dup.Key.In} parameter \"{dup.Key.Item2}\".", "parameters");
            foreach (var name in PathParameter().Matches(c.Path ?? "").Select(m => m.Groups[1].Value).Where(n => c.Parameters.All(p => p.In != "path" || p.Name != n)))
                Add(TestDataCheckTiers.Syntax, TestDataCheckState.Failed, $"Path parameter {{{name}}} has no value.", "path");
            if (c.Body is { Length: > 0 } body)
            {
                var media = (c.ContentType ?? "").Split(';')[0].Trim().ToLowerInvariant();
                if (!ContentTypes.Contains(media)) Add(TestDataCheckTiers.Syntax, TestDataCheckState.Failed, "Choose a body content type.", "contentType");
                else if (media == "application/json") { var (node, error) = JsonError(body); if (error is not null) Add(TestDataCheckTiers.Syntax, TestDataCheckState.Failed, "Body: " + error, "body"); else Scan(node, "body"); }
                else if (media == "application/xml" && XmlError(body) is { } xmlError) Add(TestDataCheckTiers.Syntax, TestDataCheckState.Failed, "Body: " + xmlError, "body");
                else if (media is "multipart/form-data" or "application/octet-stream") Add(TestDataCheckTiers.Syntax, TestDataCheckState.Failed, "A file or multipart body is a File reference resource, not inline text.", "body");
                else if (media != "application/json" && SensitiveValue().IsMatch(body)) Add(TestDataCheckTiers.Sensitive, TestDataCheckState.Failed, "The body looks like it holds a credential.", "body");
            }
            Contract("the request");
            if (context.Operation is { } op) foreach (var finding in TestDataOperationCompatibility.Rest(op, c)) checks.Add(finding);
        }

        void Dataset()
        {
            if (c.Members.Count == 0) Add(TestDataCheckTiers.Syntax, TestDataCheckState.Failed, "Add at least one member.", "members");
            Rows(c.Members.Select(m => (m.Name, m.Inline is null ? null : m.Inline with { Name = m.Name })).ToList(), "members");
            for (var i = 0; i < c.Members.Count; i++)
            {
                var m = c.Members[i];
                if ((m.Reference is null) == (m.Inline is null)) { Add(TestDataCheckTiers.Syntax, TestDataCheckState.Failed, $"{m.Name}: either a reference or an inline value.", $"members[{i}]"); continue; }
                if (m.Reference is not { } reference) continue;
                var alias = reference.Split('@')[0];
                if (alias == draft.Alias) Add(TestDataCheckTiers.References, TestDataCheckState.Failed, $"{m.Name}: a dataset cannot reference itself.", $"members[{i}]");
                else if (!context.KnownAliases.Contains(alias)) Add(TestDataCheckTiers.References, TestDataCheckState.Failed, $"{m.Name}: \"{alias}\" does not exist; an unresolved reference is never replaced by a default.", $"members[{i}]");
                else if (Reaches(alias, draft.Alias, [])) Add(TestDataCheckTiers.References, TestDataCheckState.Failed, $"{m.Name}: \"{alias}\" refers back to {draft.Alias} (cycle).", $"members[{i}]");
                if (reference.Contains('@') && !Regex.IsMatch(reference, @"@v\d+$")) Add(TestDataCheckTiers.Syntax, TestDataCheckState.Failed, $"{m.Name}: pin a version as alias@vN.", $"members[{i}]");
            }
            if (!checks.Any(x => x.Tier == TestDataCheckTiers.References)) Add(TestDataCheckTiers.References, TestDataCheckState.Passed, "Every reference resolves.");
        }

        bool Reaches(string from, string target, HashSet<string> seen)
        {
            if (!seen.Add(from) || !context.DatasetReferences.TryGetValue(from, out var next)) return false;
            return next.Any(n => n == target || Reaches(n, target, seen));
        }

        void Generated()
        {
            if (c.Generator is not { } g) { Add(TestDataCheckTiers.Syntax, TestDataCheckState.Failed, "Choose a generator.", "generator"); return; }
            if (TestDataGenerators.Random(g.Kind) && g.Seed is null) Add(TestDataCheckTiers.Syntax, TestDataCheckState.Failed, "A random generator needs a fixed seed so runs are reproducible.", "seed");
            if (TestDataGenerators.DefinitionError(g) is { } error) Add(TestDataCheckTiers.Syntax, TestDataCheckState.Failed, error, "generator");
            else Add(TestDataCheckTiers.Type, TestDataCheckState.Passed, "Generator definition is complete; values are materialized per execution.");
        }
    }
}

/// <summary>Contract compatibility of a REST template or GraphQL variables with a discovered operation. Findings feed both validation and readiness.</summary>
public static class TestDataOperationCompatibility
{
    private static bool NonNull(string? type) => (type ?? "").Trim().EndsWith('!');

    public static IEnumerable<TestDataCheck> GraphQl(ContractOperation op, IReadOnlyList<ContractTypeShape> types, JsonObject variables)
    {
        var definitions = op.ParameterDetails.Count > 0 ? op.ParameterDetails : op.Parameters.Select(p => new ContractParameter(p, "argument", false, null)).ToList();
        if (!op.Detailed && op.ParameterDetails.Count == 0) { yield return new(TestDataCheckTiers.Contract, TestDataCheckState.NotAssessed, "The snapshot records variable names only; types are not assessed."); }
        var passed = true;
        foreach (var d in definitions)
        {
            var name = d.Name.TrimStart('$');
            var required = d.Required || NonNull(d.Type);
            if (!variables.TryGetPropertyValue(name, out var value)) { if (required) { passed = false; yield return new(TestDataCheckTiers.Contract, TestDataCheckState.Failed, $"Missing required variable ${name} ({d.Type}).", $"variables.{name}"); } continue; }
            foreach (var problem in Value(d.Type, value, types, $"${name}")) { passed = false; yield return new(TestDataCheckTiers.Contract, TestDataCheckState.Failed, problem, $"variables.{name}"); }
        }
        foreach (var (name, _) in variables.Where(v => definitions.All(d => d.Name.TrimStart('$') != v.Key)))
            yield return new(TestDataCheckTiers.Contract, TestDataCheckState.Warning, $"${name} is not a variable of {op.Name}.", $"variables.{name}");
        if (passed) yield return new(TestDataCheckTiers.Contract, TestDataCheckState.Passed, $"Variables match the declared variables of {op.Name}.");
    }

    /// <summary>Type check of one GraphQL value against a declared type: non-null, lists, built-in scalars, enums and input objects (when known).</summary>
    public static IEnumerable<string> Value(string? type, JsonNode? value, IReadOnlyList<ContractTypeShape> types, string path)
    {
        if (value is null) { if (NonNull(type)) yield return $"{path} is non-null ({type}) but null."; yield break; }
        var t = (type ?? "").Trim();
        if (t.EndsWith('!')) t = t[..^1];
        if (t.StartsWith('['))
        {
            if (value is not JsonArray array) { yield return $"{path} must be a list ({type})."; yield break; }
            var inner = t.Trim('[', ']');
            for (var i = 0; i < array.Count; i++) foreach (var p in Value(inner, array[i], types, $"{path}[{i}]")) yield return p;
            yield break;
        }
        var kind = value.GetValueKind();
        string? problem = t switch
        {
            "Int" => kind == JsonValueKind.Number && value.AsValue().TryGetValue<int>(out _) ? null : $"{path} must be an Int.",
            "Float" => kind == JsonValueKind.Number ? null : $"{path} must be a Float.",
            "String" => kind == JsonValueKind.String ? null : $"{path} must be a String.",
            "Boolean" => kind is JsonValueKind.True or JsonValueKind.False ? null : $"{path} must be a Boolean.",
            "ID" => kind is JsonValueKind.String or JsonValueKind.Number ? null : $"{path} must be an ID (string or integer).",
            _ => null,
        };
        if (problem is not null) { yield return problem; yield break; }
        if (types.FirstOrDefault(x => x.Name == t) is not { } shape) yield break;   // custom scalar or unknown type: not asserted
        if (shape.Kind.Equals("enum", StringComparison.OrdinalIgnoreCase))
        {
            if (kind != JsonValueKind.String || !shape.Values.Contains(value.GetValue<string>())) yield return $"{path} must be one of {t}: {string.Join(", ", shape.Values)}.";
            yield break;
        }
        if (value is not JsonObject o) { yield return $"{path} must be an input object ({t})."; yield break; }
        foreach (var f in shape.Fields)
        {
            if (!o.TryGetPropertyValue(f.Name, out var fv)) { if (f.Required || NonNull(f.Type)) yield return $"{path}.{f.Name} is required ({f.Type})."; continue; }
            foreach (var p in Value(f.Type, fv, types, $"{path}.{f.Name}")) yield return p;
        }
    }

    public static IEnumerable<TestDataCheck> Rest(ContractOperation op, TestDataResourceContent c)
    {
        var passed = true;
        var method = op.Name.Split(' ')[0].ToUpperInvariant();
        if (TestDataResourceRules.Methods.Contains(method) && !string.Equals(method, c.Method, StringComparison.OrdinalIgnoreCase))
        { passed = false; yield return new(TestDataCheckTiers.Contract, TestDataCheckState.Failed, $"The operation is {method}, the template uses {c.Method}.", "method"); }
        foreach (var p in op.ParameterDetails)
        {
            var given = c.Parameters.FirstOrDefault(x => x.In == p.In && (p.In == "header" ? string.Equals(x.Name, p.Name, StringComparison.OrdinalIgnoreCase) : x.Name == p.Name));
            if (given is null) { if (p.Required) { passed = false; yield return new(TestDataCheckTiers.Contract, TestDataCheckState.Failed, $"Missing required {p.In} parameter \"{p.Name}\".", "parameters"); } continue; }
            var expected = Expected(p.Type);
            if (expected is { } e && given.Type != e && !(e == TestDataPrimitive.Double && given.Type is TestDataPrimitive.Decimal or TestDataPrimitive.Int32 or TestDataPrimitive.Int64)
                && !(e == TestDataPrimitive.Int64 && given.Type == TestDataPrimitive.Int32))
            { passed = false; yield return new(TestDataCheckTiers.Contract, TestDataCheckState.Failed, $"\"{p.Name}\": expected {TestDataResourceRules.Label(e)} ({p.Type}), the template has {TestDataResourceRules.Label(given.Type)}.", "parameters"); }
            if (p.Enum is { Count: > 0 } allowed && given.Value is { } v && !allowed.Contains(v))
            { passed = false; yield return new(TestDataCheckTiers.Contract, TestDataCheckState.Failed, $"\"{p.Name}\" must be one of {string.Join(", ", allowed)}.", "parameters"); }
        }
        if (op.RequestBodyRequired && string.IsNullOrWhiteSpace(c.Body)) { passed = false; yield return new(TestDataCheckTiers.Contract, TestDataCheckState.Failed, "The operation requires a request body.", "body"); }
        if (c.Body is { Length: > 0 } && op.RequestContentTypes.Count > 0 && !op.RequestContentTypes.Any(t => t.Split(';')[0].Trim().Equals((c.ContentType ?? "").Split(';')[0].Trim(), StringComparison.OrdinalIgnoreCase)))
        { passed = false; yield return new(TestDataCheckTiers.Contract, TestDataCheckState.Failed, $"Body content type {c.ContentType} is not accepted ({string.Join(", ", op.RequestContentTypes)}).", "contentType"); }
        if (c.Body is { Length: > 0 }) yield return new(TestDataCheckTiers.Contract, TestDataCheckState.NotAssessed, "The body is not validated against the operation's schema (no JSON Schema validation).", "body");
        if (passed) yield return new(TestDataCheckTiers.Contract, TestDataCheckState.Passed, $"Parameters match {op.Name}.");
    }

    /// <summary>OpenAPI parameter type → the exact primitive a value must have (null = not asserted).</summary>
    /// <remarks>Source Analysis records OpenAPI types as <c>type</c> or <c>type:format</c> (e.g. <c>string:uuid</c>, <c>integer:int64</c>).</remarks>
    public static TestDataPrimitive? Expected(string? type) => (type ?? "").ToLowerInvariant() switch
    {
        "integer" or "integer:int32" => TestDataPrimitive.Int32,
        "integer:int64" => TestDataPrimitive.Int64,
        "number" or "number:double" or "number:float" => TestDataPrimitive.Double,
        "boolean" => TestDataPrimitive.Boolean,
        "string:uuid" => TestDataPrimitive.Uuid,
        "string:date" => TestDataPrimitive.Date,
        "string:date-time" => TestDataPrimitive.DateTime,
        _ => null,
    };
}

/// <summary>Deterministic generators: the definition is stored; values are materialized from (definition, index), never stored as test data.</summary>
public static class TestDataGenerators
{
    private static readonly string[] Given = ["Alex", "Kari", "Ola", "Sam", "Nora", "Ivar", "Ingrid", "Emil", "Sara", "Jonas"];
    private static readonly string[] Family = ["Testesen", "Prøvesen", "Eksempel", "Demosen", "Fiktiv", "Syntetisk"];
    private static readonly string[] Streets = ["Testveien", "Eksempelgata", "Prøvebakken", "Demoalleen"];

    public static bool Random(TestDataGeneratorKind kind) => kind is TestDataGeneratorKind.Uuid or TestDataGeneratorKind.IntegerRange or TestDataGeneratorKind.DecimalRange
        or TestDataGeneratorKind.EnumChoice or TestDataGeneratorKind.FakeName or TestDataGeneratorKind.FakeEmail or TestDataGeneratorKind.FakeAddress or TestDataGeneratorKind.Pattern;

    public static string? DefinitionError(TestDataGeneratorDefinition g) => g.Kind switch
    {
        TestDataGeneratorKind.IntegerRange or TestDataGeneratorKind.DecimalRange when g.Min is null || g.Max is null || g.Min > g.Max => "A range needs Min ≤ Max.",
        TestDataGeneratorKind.EnumChoice when g.Values is not { Count: > 0 } => "Enum selection needs allowed values.",
        TestDataGeneratorKind.Pattern when string.IsNullOrEmpty(g.Pattern) || g.Pattern.Length > 64 => "A pattern is 1–64 characters of literals, '#', 'A' and 'a'.",
        TestDataGeneratorKind.Pattern when g.Pattern!.Count(ch => ch == '#') == 11 && g.Pattern.All(ch => ch == '#') => "An 11-digit pattern could produce national identity numbers; it is not allowed.",
        _ => null,
    };

    /// <summary>The value for one index (0-based) — the same definition and index always give the same value. Time-based kinds use <paramref name="now"/>.</summary>
    public static string Materialize(TestDataGeneratorDefinition g, int index, DateTimeOffset now)
    {
        var rng = new System.Random(unchecked((g.Seed ?? 0) * 7919 + index));
        var inv = CultureInfo.InvariantCulture;
        return g.Kind switch
        {
            TestDataGeneratorKind.Uuid => new Guid(Enumerable.Range(0, 16).Select(_ => (byte)rng.Next(256)).ToArray()).ToString("D"),
            TestDataGeneratorKind.Sequence => ((g.Start ?? 1) + (g.Step ?? 1) * index).ToString(inv),
            TestDataGeneratorKind.DateOffset => DateOnly.FromDateTime(now.UtcDateTime).AddDays(g.OffsetDays ?? 0).ToString("yyyy-MM-dd", inv),
            TestDataGeneratorKind.Timestamp => now.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", inv),
            TestDataGeneratorKind.IntegerRange => ((long)g.Min! + (long)Math.Floor(rng.NextDouble() * (double)((long)g.Max! - (long)g.Min! + 1))).ToString(inv),
            TestDataGeneratorKind.DecimalRange => Math.Round(g.Min!.Value + (decimal)rng.NextDouble() * (g.Max!.Value - g.Min.Value), g.Scale ?? 2).ToString(inv),
            TestDataGeneratorKind.EnumChoice => g.Values![rng.Next(g.Values.Count)],
            TestDataGeneratorKind.FakeName => $"{Given[rng.Next(Given.Length)]} {Family[rng.Next(Family.Length)]}",
            TestDataGeneratorKind.FakeEmail => $"{Given[rng.Next(Given.Length)].ToLowerInvariant()}.{index}@example.test",
            TestDataGeneratorKind.FakeAddress => $"{Streets[rng.Next(Streets.Length)]} {rng.Next(1, 200)}, 0{rng.Next(100, 999)} Testby",
            TestDataGeneratorKind.Pattern => new string(g.Pattern!.Select(ch => ch switch { '#' => (char)('0' + rng.Next(10)), 'A' => (char)('A' + rng.Next(26)), 'a' => (char)('a' + rng.Next(26)), _ => ch }).ToArray()),
            _ => "",
        };
    }
}

/// <summary>
/// Reference expressions inside authored content: <c>{{dataset.member}}</c>, <c>{{data.alias}}</c>, <c>{{environment.name}}</c>,
/// <c>{{scenario.name}}</c>, <c>{{step.name}}</c>. Precedence for an unqualified name: step → scenario → dataset → environment. A missing value
/// stays unresolved (never ""), a cycle is reported, and a value marked sensitive is masked in previews. (Performance scenarios keep their own
/// <c>{name}</c> placeholders.)
/// </summary>
public static partial class TestDataBindings
{
    public static readonly string[] Scopes = ["step", "scenario", "dataset", "environment"];
    [GeneratedRegex(@"\{\{\s*(?:(step|scenario|dataset|environment|data)\.)?([A-Za-z_][A-Za-z0-9_.\-]*)\s*\}\}")] private static partial Regex Expression();

    public sealed record Scope(string Name, IReadOnlyDictionary<string, string> Values, IReadOnlySet<string>? Sensitive = null);
    public sealed record Result(string Preview, string? Resolved, List<string> Unresolved, List<string> Cycles);

    public static IReadOnlyList<string> References(string? text) => [.. Expression().Matches(text ?? "").Select(m => m.Value)];

    /// <summary>Resolves every expression. <see cref="Result.Resolved"/> is null when anything stayed unresolved or cyclic; the preview masks sensitive values.</summary>
    public static Result Resolve(string text, IReadOnlyList<Scope> scopes, int maxDepth = 8)
    {
        var unresolved = new List<string>(); var cycles = new List<string>();
        string Expand(string input, bool mask, ImmutableStack stack, int depth) => Expression().Replace(input, m =>
        {
            var scope = m.Groups[1].Success ? m.Groups[1].Value : null; var name = m.Groups[2].Value; var key = $"{scope ?? "*"}.{name}";
            if (stack.Contains(key) || depth > maxDepth) { if (!cycles.Contains(m.Value)) cycles.Add(m.Value); return m.Value; }
            var hit = scopes.Where(s => scope is null ? Scopes.Contains(s.Name) : s.Name == scope || scope == "data" && s.Name == "dataset")
                .OrderBy(s => Array.IndexOf(Scopes, s.Name)).FirstOrDefault(s => s.Values.ContainsKey(name));
            if (hit is null) { if (!unresolved.Contains(m.Value)) unresolved.Add(m.Value); return m.Value; }
            if (mask && hit.Sensitive?.Contains(name) == true) return "••••••";
            return Expand(hit.Values[name], mask, stack.Push(key), depth + 1);
        });
        var preview = Expand(text, true, ImmutableStack.Empty, 0);
        var resolved = Expand(text, false, ImmutableStack.Empty, 0);
        return new(preview, unresolved.Count == 0 && cycles.Count == 0 ? resolved : null, unresolved, cycles);
    }

    private sealed class ImmutableStack
    {
        public static readonly ImmutableStack Empty = new(null, null);
        private readonly string? _head; private readonly ImmutableStack? _tail;
        private ImmutableStack(string? head, ImmutableStack? tail) { _head = head; _tail = tail; }
        public ImmutableStack Push(string item) => new(item, this);
        public bool Contains(string item) { for (var s = this; s is { _head: not null }; s = s._tail!) if (s._head == item) return true; return false; }
    }
}
