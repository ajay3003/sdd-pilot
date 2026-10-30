using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml;

namespace BirkNext.PerformanceTests;

// ── Performance scenario request/response handling ──────────────────────────────────────────────────────────────────────
// A step is REQUEST → execute → RESPONSE → named outputs. Shared (frontend + backend): payload formats, request preview, extraction rules and
// the bounded validation-request (trial) contracts. Load runs keep metrics and status only; a validation request returns one bounded, redacted
// sample per step and is never stored. Pure and deterministic here; nothing is sent from this file.

/// <summary>Where an output is read from: the JSON response body (path) or a response header (name).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum StepOutputSource { Body, Header }

/// <summary>A request or response payload format, from the declared Content-Type (never guessed from the body text).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PayloadFormat { None, Json, Xml, Text, Html, Form, GraphQl, Binary, Stream }

/// <summary>What a sample keeps of a response body.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ResponseCaptureState { Available, MetadataOnly, CaptureDisabled, NotRetained, Truncated, Redacted, Unavailable }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TrialStepState { Sent, Skipped, Blocked, Failed }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum OutputSampleState { Ready, Missing, NeedsReview, Unsupported }

/// <summary>One validation request: the (possibly unsaved) definition, sent step by step in order up to and including <see cref="ThroughStep"/>.</summary>
public sealed record PerformanceTrialRequest
{
    public PerformanceTestDefinition Definition { get; init; } = new();
    /// <summary>Zero-based index of the last step to send (null = every enabled step).</summary>
    public int? ThroughStep { get; init; }
}

/// <summary>The result of one validation request: per step, the request sent and the response received — bounded, redacted, never stored.</summary>
public sealed record PerformanceTrialResult
{
    public List<PerformanceTrialStep> Steps { get; init; } = [];
    /// <summary>Why nothing was sent (unsafe environment/target) — null when steps were evaluated.</summary>
    public string? Message { get; init; }
    public DateTimeOffset At { get; init; }
}

public sealed record PerformanceTrialStep
{
    public string StepId { get; init; } = "";
    public int Index { get; init; }
    public string Name { get; init; } = "";
    public TrialStepState State { get; init; }
    public string? Reason { get; init; }
    public PerformanceRequestSample? Request { get; init; }
    public PerformanceResponseSample? Response { get; init; }
    public List<PerformanceTrialOutput> Outputs { get; init; } = [];
    /// <summary>Whether the HTTP status is one of the step's expected codes (null when nothing was received).</summary>
    public bool? ExpectedStatus { get; init; }
}

/// <summary>A request as configured or as sent: Test Data values and sensitive headers are never shown in clear.</summary>
public sealed record PerformanceRequestSample
{
    public string Method { get; init; } = "GET";
    public string Url { get; init; } = "";
    public List<PerformanceNameValue> Query { get; init; } = [];
    public List<PerformanceNameValue> Headers { get; init; } = [];
    public string? ContentType { get; init; }
    public PayloadFormat Format { get; init; }
    public string? Body { get; init; }
    public long? BodyBytes { get; init; }
    public bool BodyTruncated { get; init; }
    /// <summary>GraphQL: the document and variables kept apart from the HTTP body.</summary>
    public string? GraphQlQuery { get; init; }
    public string? GraphQlVariables { get; init; }
    /// <summary>Runtime values shown as references (Test Data, unresolved) instead of values.</summary>
    public List<string> Masked { get; init; } = [];
}

/// <summary>Response metadata and a bounded, redacted preview. The preview is display text only (escaped by the UI; never rendered as HTML).</summary>
public sealed record PerformanceResponseSample
{
    public int Status { get; init; }
    public string? Reason { get; init; }
    public string? ContentType { get; init; }
    public PayloadFormat Format { get; init; }
    public double DurationMs { get; init; }
    /// <summary>Decoded body size; a lower bound when <see cref="SizeIsLowerBound"/>.</summary>
    public long? SizeBytes { get; init; }
    public bool SizeIsLowerBound { get; init; }
    public List<PerformanceNameValue> Headers { get; init; } = [];
    public ResponseCaptureState Capture { get; init; }
    public string? CaptureNote { get; init; }
    public string? Preview { get; init; }
    public long PreviewBytes { get; init; }
    public bool Redacted { get; init; }
    /// <summary>"Invalid JSON response" / "Invalid XML response" when the declared format does not parse.</summary>
    public string? ParseError { get; init; }
    /// <summary>GraphQL: errors in the body (separate from the HTTP status); null for non-GraphQL responses.</summary>
    public int? GraphQlErrors { get; init; }
    public List<string> GraphQlErrorMessages { get; init; } = [];
    public bool? GraphQlData { get; init; }
}

/// <summary>An output evaluated against the sample: the value is shown for review only and never stored as a fixed value.</summary>
public sealed record PerformanceTrialOutput(string Name, StepOutputSource From, string Expression, OutputSampleState State, string? Value, string? Problem);

public static partial class PerformanceRequestResponse
{
    /// <summary>Preview limits: request body preview and response preview (bytes of UTF-8 text).</summary>
    public const int RequestPreviewBytes = 4 * 1024, ResponsePreviewBytes = 32 * 1024;

    private static readonly string[] SensitiveHeaderNames = ["authorization", "proxy-authorization", "cookie", "set-cookie", "x-api-key", "api-key", "apikey", "x-auth-token",
        "x-access-token", "x-client-secret", "client-secret", "x-functions-key", "ocp-apim-subscription-key", "x-csrf-token"];

    [GeneratedRegex(@"(?i)(access_?token|refresh_?token|id_?token|^token$|secret|password|passwd|api_?key|session|cookie|credential|bearer|assertion)")]
    private static partial Regex SensitiveNamePattern();
    [GeneratedRegex(@"^[A-Za-z0-9!#$%&'*+.^_`|~-]{1,100}$")] private static partial Regex HeaderNamePattern();
    [GeneratedRegex(@"\{([A-Za-z_][A-Za-z0-9_]*)\}")] private static partial Regex PlaceholderPattern();

    public static bool IsSensitiveHeader(string? name) => name is not null && SensitiveHeaderNames.Contains(name.Trim().ToLowerInvariant());

    /// <summary>A field, header or output name that can carry a credential (token, secret, password, API key, session, cookie).</summary>
    public static bool IsSensitiveName(string? name) => name is not null && SensitiveNamePattern().IsMatch(name.Trim());

    public static bool ValidHeaderName(string? name) => name is not null && HeaderNamePattern().IsMatch(name);

    private static string MediaType(string? contentType) => (contentType ?? "").Split(';')[0].Trim().ToLowerInvariant();

    /// <summary>The request body format from the step's Content-Type (GraphQL targets send a JSON GraphQL envelope).</summary>
    public static PayloadFormat RequestFormat(HttpPerformanceStep s, bool graphQl) =>
        graphQl ? PayloadFormat.GraphQl : string.IsNullOrEmpty(s.BodyTemplate) ? PayloadFormat.None : Of(s.ContentType ?? "application/json");

    /// <summary>The response format from its Content-Type; a GraphQL target's JSON response is GraphQL (data + errors).</summary>
    public static PayloadFormat ResponseFormat(string? contentType, bool graphQl, long? size)
    {
        if (size == 0) return PayloadFormat.None;
        var format = Of(contentType);
        return graphQl && format == PayloadFormat.Json ? PayloadFormat.GraphQl : format == PayloadFormat.None && size > 0 ? PayloadFormat.Binary : format;
    }

    public static PayloadFormat Of(string? contentType)
    {
        var m = MediaType(contentType);
        if (m.Length == 0) return PayloadFormat.None;
        if (m is "application/json" or "text/json" || m.EndsWith("+json", StringComparison.Ordinal)) return PayloadFormat.Json;
        if (m is "text/html" or "application/xhtml+xml") return PayloadFormat.Html;
        if (m is "application/xml" or "text/xml" || m.EndsWith("+xml", StringComparison.Ordinal)) return PayloadFormat.Xml;
        if (m == "application/x-www-form-urlencoded") return PayloadFormat.Form;
        if (m is "text/event-stream" or "application/x-ndjson" or "application/stream+json") return PayloadFormat.Stream;
        if (m == "application/graphql") return PayloadFormat.GraphQl;
        if (m.StartsWith("text/", StringComparison.Ordinal)) return PayloadFormat.Text;
        return PayloadFormat.Binary;
    }

    public static string Label(PayloadFormat f) => f switch
    {
        PayloadFormat.None => "No body",
        PayloadFormat.Json => "JSON",
        PayloadFormat.Xml => "XML",
        PayloadFormat.Text => "Text",
        PayloadFormat.Html => "HTML",
        PayloadFormat.Form => "Form",
        PayloadFormat.GraphQl => "GraphQL",
        PayloadFormat.Stream => "Stream",
        _ => "Binary",
    };

    public static string CaptureLabel(ResponseCaptureState c) => c switch
    {
        ResponseCaptureState.Available => "Sample retained",
        ResponseCaptureState.MetadataOnly => "Metadata only",
        ResponseCaptureState.CaptureDisabled => "Capture disabled",
        ResponseCaptureState.NotRetained => "Not retained",
        ResponseCaptureState.Truncated => "Truncated",
        ResponseCaptureState.Redacted => "Redacted",
        _ => "Unavailable",
    };

    /// <summary>Content types a step body can declare (no format without a product need).</summary>
    public static readonly IReadOnlyList<string> BodyContentTypes = ["application/json", "application/xml", "text/xml", "text/plain", "application/x-www-form-urlencoded"];

    /// <summary>
    /// How a runtime value enters a body: a placeholder inside a JSON body is JSON-string escaped, inside XML it is XML-escaped, inside a form it is
    /// URL-encoded; a body that is exactly one placeholder is a whole payload (Test Data payload asset) and is inserted as is.
    /// </summary>
    public static string BodyEncoding(HttpPerformanceStep s, bool graphQl)
    {
        if (graphQl) return "json";
        var body = s.BodyTemplate?.Trim() ?? "";
        if (PlaceholderPattern().Match(body) is { Success: true } m && m.Value == body) return "text";
        return RequestFormat(s, false) switch { PayloadFormat.Json => "json", PayloadFormat.Xml => "xml", PayloadFormat.Form => "form", _ => "text" };
    }

    /// <summary>Encodes a value for one placeholder position (same rules as the k6 runtime).</summary>
    public static string Encode(string value, string mode) => mode switch
    {
        "path" or "form" => Uri.EscapeDataString(value),
        "json" => JsonSerializer.Serialize(value)[1..^1],
        "xml" => value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&apos;"),
        "header" => value.Replace("\r", "").Replace("\n", ""),
        _ => value,
    };

    /// <summary>
    /// Whether the body template is valid for its declared Content-Type once values are inserted: JSON parses (placeholders as 0, which is valid both
    /// inside and outside string quotes) and XML is well-formed (no DTD, no external entities). Null when valid or not structured.
    /// </summary>
    public static string? BodyProblem(HttpPerformanceStep s, bool graphQl)
    {
        if (graphQl || string.IsNullOrWhiteSpace(s.BodyTemplate) || BodyEncoding(s, false) == "text" && PlaceholderPattern().IsMatch(s.BodyTemplate.Trim())) return null;
        var format = RequestFormat(s, false);
        if (format == PayloadFormat.Json)
        {
            try { using var _ = JsonDocument.Parse(PlaceholderPattern().Replace(s.BodyTemplate, "0")); return null; }
            catch (JsonException) { return $"the request body is not valid JSON for {MediaType(s.ContentType ?? "application/json")}"; }
        }
        if (format == PayloadFormat.Xml)
        {
            try
            {
                using var reader = XmlReader.Create(new StringReader(PlaceholderPattern().Replace(s.BodyTemplate, "x")), SafeXml);
                while (reader.Read()) { }
                return null;
            }
            catch (XmlException) { return $"the request body is not well-formed XML for {MediaType(s.ContentType)}"; }
        }
        return null;
    }

    /// <summary>XML is read without DTD processing or external resolution (no XXE, no entity expansion).</summary>
    public static XmlReaderSettings SafeXml => new() { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersFromEntities = 0, IgnoreComments = true };

    /// <summary>
    /// Why an output cannot be extracted — null when valid: a name, a JSON path ($.id, $.data.items[0].id) for the body, or a header name. Credential
    /// material (tokens, secrets, cookies, Authorization) is never an ordinary output: authentication owns it.
    /// </summary>
    public static string? OutputProblem(StepOutput o)
    {
        if (!PerformanceScenarioRuntime.ValidName(o.Name)) return $"output '{o.Name}' needs a name (letters, digits, '_')";
        if (o.From == StepOutputSource.Header)
        {
            if (!ValidHeaderName(o.Path)) return $"output '{o.Name}' needs a response header name such as Location";
            if (IsSensitiveHeader(o.Path) || IsSensitiveName(o.Path)) return $"output '{o.Name}' reads {o.Path}, which can carry a credential — authentication is not a scenario value";
        }
        else
        {
            if (!PerformanceScenarioRuntime.ValidOutputPath(o.Path)) return $"output '{o.Name}' needs a JSON path such as $.id";
            if (o.Path.Split('.', '[').LastOrDefault(p => p.Length > 0 && !p.EndsWith(']')) is { } last && IsSensitiveName(last))
                return $"output '{o.Name}' reads '{last}', which can carry a credential — authentication is not a scenario value";
        }
        return IsSensitiveName(o.Name) ? $"output '{o.Name}' is named like a credential — authentication is not a scenario value" : null;
    }

    /// <summary>What the load engine can extract. XML/XPath and text/regex are not executable by the k6 runtime, so they are not offered.</summary>
    public static readonly IReadOnlyList<(string Capability, bool Supported, string Detail)> ExtractionCapabilities =
    [
        ("JSON body path", true, "$.id, $.data.items[0].id"),
        ("Response header", true, "Location, X-Request-Id"),
        ("XML / XPath", false, "not supported by the k6 load engine — extract from a JSON response or a header"),
        ("Text / regex", false, "not supported"),
    ];

    public static string OutputExpression(StepOutput o) => o.From == StepOutputSource.Header ? $"header {o.Path}" : o.Path;

    /// <summary>
    /// The step's request as configured (no request is sent): URL from origin + path + query, headers (Content-Type from the step, sensitive values
    /// masked), body bounded to <see cref="RequestPreviewBytes"/>. Fixed and auto-filled values are shown; every other source stays a {placeholder}
    /// (Test Data values are never shown).
    /// </summary>
    public static PerformanceRequestSample Preview(PerformanceTestDefinition d, int index)
    {
        var s = d.Scenario.Steps[index];
        var graphQl = d.TargetType == PerformanceTargetType.GraphQlHttp;
        var masked = new List<string>();
        string Show(string? text, string mode) => PlaceholderPattern().Replace(text ?? "", m =>
        {
            var mapping = s.DataMappings?.FirstOrDefault(x => x.Variable == m.Groups[1].Value);
            if (mapping is { Source: RuntimeValueSource.Fixed or RuntimeValueSource.AutoFilled, FixedValue: { } v }) return Encode(v, mode);
            if (!masked.Contains(m.Groups[1].Value)) masked.Add(m.Groups[1].Value);
            return m.Value;
        });
        var query = s.QueryParameters.Select(q => new PerformanceNameValue(q.Name, Show(q.Value, "text"))).ToList();
        var url = d.TargetOrigin.TrimEnd('/') + Show(s.RelativePath, "path")
            + (query.Count == 0 ? "" : "?" + string.Join("&", query.Select(q => $"{Uri.EscapeDataString(q.Name)}={q.Value}")));
        var contentType = graphQl ? "application/json" : string.IsNullOrEmpty(s.BodyTemplate) ? null : s.ContentType ?? "application/json";
        var headers = s.Headers.Select(h => new PerformanceNameValue(h.Name, IsSensitiveHeader(h.Name) ? "[REDACTED]" : Show(h.Value, "header"))).ToList();
        if (contentType is not null && !headers.Any(h => h.Name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))) headers.Add(new("Content-Type", contentType));
        var body = graphQl ? null : s.BodyTemplate is null ? null : Show(s.BodyTemplate, BodyEncoding(s, false));
        var bytes = body is null ? (long?)null : System.Text.Encoding.UTF8.GetByteCount(body);
        return new PerformanceRequestSample
        {
            Method = graphQl ? "POST" : s.Method.ToUpperInvariant(), Url = url, Query = query, Headers = [.. headers.OrderBy(h => h.Name, StringComparer.Ordinal)],
            ContentType = contentType, Format = RequestFormat(s, graphQl), Body = Bound(body, RequestPreviewBytes), BodyBytes = bytes, BodyTruncated = bytes > RequestPreviewBytes,
            GraphQlQuery = graphQl ? s.GraphQlQuery : null, GraphQlVariables = graphQl ? Show(s.GraphQlVariablesJson, "json") : null, Masked = masked,
        };
    }

    /// <summary>At most <paramref name="maxBytes"/> UTF-8 bytes of text, cut on a character boundary.</summary>
    public static string? Bound(string? text, int maxBytes)
    {
        if (text is null || System.Text.Encoding.UTF8.GetByteCount(text) <= maxBytes) return text;
        var n = Math.Min(text.Length, maxBytes);
        while (n > 0 && System.Text.Encoding.UTF8.GetByteCount(text.AsSpan(0, n)) > maxBytes) n--;
        if (n > 0 && char.IsHighSurrogate(text[n - 1])) n--;
        return text[..n];
    }

    /// <summary>"1.8 KB", "512 B", "2.4 MB".</summary>
    public static string Size(long bytes) => bytes < 1024 ? $"{bytes} B"
        : bytes < 1024 * 1024 ? $"{(bytes / 1024.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)} KB"
        : $"{(bytes / 1024.0 / 1024.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)} MB";
}
