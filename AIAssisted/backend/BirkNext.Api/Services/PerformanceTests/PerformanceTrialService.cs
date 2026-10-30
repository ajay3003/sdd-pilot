using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using BirkNext.Api.Services.LocalHttpsProxy;
using BirkNext.PerformanceTests;

namespace BirkNext.Api.Services.PerformanceTests;

/// <summary>
/// "Send validation request": every enabled step once, in order, with the same value resolution, encoding and extraction rules as the k6 load
/// script — so a person can see the request, the response and what each output extracts before a load run. One request per step, never a load:
/// the same environment/target/method safety as a run applies, authenticated steps are not sent (BirkNext issues no credential for automated
/// requests), response bodies are read up to <see cref="MaxInspectedBytes"/> and previews are bounded and redacted. Nothing is stored or logged.
/// </summary>
public sealed partial class PerformanceTrialService(IHttpClientFactory clients, PerformanceTestOptions options, IServiceProvider services, TimeProvider? clock = null)
{
    public const string ClientName = "performance-trial";
    public const int MaxInspectedBytes = 256 * 1024;
    public const long MaxCountedBytes = 16L * 1024 * 1024;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    [GeneratedRegex(@"\{([A-Za-z_][A-Za-z0-9_]*)\}")] private static partial Regex PlaceholderPattern();
    [GeneratedRegex(@"\.([A-Za-z_][A-Za-z0-9_]*)|\[(\d{1,4})\]")] private static partial Regex PathSegment();

    public async Task<PerformanceTrialResult> RunAsync(PerformanceTrialRequest request, CancellationToken ct)
    {
        var d = request.Definition;
        var now = (clock ?? TimeProvider.System).GetUtcNow();
        if (PerformanceTestSafety.EnvironmentBlock(d.EnvironmentType) is { } env) return new() { Message = env, At = now };
        var (origin, originError) = PerformanceTestSafety.Origin(d.TargetOrigin, d.EnvironmentType, options);
        if (origin is null) return new() { Message = originError, At = now };
        if (d.Scenario.Steps.Count is 0 or > 20) return new() { Message = "A validation request needs 1–20 steps.", At = now };

        var row = TestDataRow(d);
        var graphQl = d.TargetType == PerformanceTargetType.GraphQlHttp;
        var ctx = new Dictionary<string, string?>(StringComparer.Ordinal);
        var results = new List<PerformanceTrialStep>();
        var last = Math.Min(request.ThroughStep ?? d.Scenario.Steps.Count - 1, d.Scenario.Steps.Count - 1);
        for (var i = 0; i <= last; i++)
        {
            var s = d.Scenario.Steps[i];
            if (s.Disabled) continue;
            results.Add(await StepAsync(d, i, s, origin, graphQl, row, ctx, ct));
        }
        return new() { Steps = results, At = now };
    }

    private async Task<PerformanceTrialStep> StepAsync(PerformanceTestDefinition d, int i, HttpPerformanceStep s, Uri origin, bool graphQl,
        IReadOnlyDictionary<string, string> row, Dictionary<string, string?> ctx, CancellationToken ct)
    {
        var step = new PerformanceTrialStep { StepId = s.Id, Index = i, Name = s.Name };
        var readiness = PerformanceScenarioRuntime.Readiness(d, i);
        if (readiness.Status == StepStatus.Blocked) return step with { State = TrialStepState.Blocked, Reason = string.Join(" ", readiness.Reasons) };
        if (PerformanceScenarioRuntime.EffectiveAuthentication(d, s) is not null)
            return step with { State = TrialStepState.Blocked, Reason = "This step is authenticated. BirkNext does not issue credentials for automated requests, so it is not sent." };
        if (s.Headers.FirstOrDefault(h => PerformanceRequestResponse.IsSensitiveHeader(h.Name)) is { } sensitive)
            return step with { State = TrialStepState.Blocked, Reason = $"The header '{sensitive.Name}' can carry a credential and is never sent from a scenario." };

        // Request: the same resolution and encoding as the k6 runtime; the display copy shows Test Data values as {placeholders}.
        var missing = new List<string>();
        var masked = new List<string>();
        (string Value, string Display) Fill(string? text, string mode) => (PlaceholderPattern().Replace(text ?? "", m => Resolve(m, mode, false)), PlaceholderPattern().Replace(text ?? "", m => Resolve(m, mode, true)));
        string Resolve(Match m, string mode, bool display)
        {
            var key = m.Groups[1].Value;
            var (value, secret) = Value(d, s, key, row, ctx);
            if (value is null) { if (!display && !missing.Contains(key)) missing.Add(key); return m.Value; }
            if (display && secret) { if (!masked.Contains(key)) masked.Add(key); return m.Value; }
            return PerformanceRequestResponse.Encode(value, mode);
        }
        var path = Fill(s.RelativePath, "path");
        var query = s.QueryParameters.Select(q => (q.Name, Value: Fill(q.Value, "text"))).ToList();
        string Query(bool display) => query.Count == 0 ? "" : "?" + string.Join("&", query.Select(q => $"{Uri.EscapeDataString(q.Name)}={(display ? q.Value.Display : Uri.EscapeDataString(q.Value.Value))}"));
        var headers = s.Headers.Select(h => (h.Name, Value: Fill(h.Value, "header"))).ToList();
        var contentType = graphQl ? "application/json" : string.IsNullOrEmpty(s.BodyTemplate) ? null : s.ContentType ?? "application/json";
        var bodyTemplate = graphQl ? GraphQlBody(s) : s.BodyTemplate;
        var body = bodyTemplate is null ? default : Fill(bodyTemplate, PerformanceRequestResponse.BodyEncoding(s, graphQl));
        var displayBody = body.Display is null ? null : SensitiveDataRedactor.RedactText(body.Display);
        var sample = new PerformanceRequestSample
        {
            Method = graphQl ? "POST" : s.Method.ToUpperInvariant(), Url = SensitiveDataRedactor.RedactText(origin.ToString().TrimEnd('/') + path.Display + Query(true)),
            Query = [.. query.Select(q => new PerformanceNameValue(q.Name, SensitiveDataRedactor.RedactText(q.Value.Display)))],
            Headers = [.. headers.Select(h => new PerformanceNameValue(h.Name, SensitiveDataRedactor.RedactHeaderValue(h.Name, h.Value.Display)))
                .Concat(contentType is null || headers.Any(h => h.Name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) ? [] : [new PerformanceNameValue("Content-Type", contentType)])
                .OrderBy(h => h.Name, StringComparer.Ordinal)],
            ContentType = contentType, Format = PerformanceRequestResponse.RequestFormat(s, graphQl),
            Body = graphQl ? null : PerformanceRequestResponse.Bound(displayBody, PerformanceRequestResponse.RequestPreviewBytes),
            BodyBytes = body.Value is null ? null : Encoding.UTF8.GetByteCount(body.Value), BodyTruncated = displayBody is not null && Encoding.UTF8.GetByteCount(displayBody) > PerformanceRequestResponse.RequestPreviewBytes,
            GraphQlQuery = graphQl ? s.GraphQlQuery : null, GraphQlVariables = graphQl ? SensitiveDataRedactor.RedactText(Fill(s.GraphQlVariablesJson, "json").Display) : null, Masked = masked,
        };
        step = step with { Request = sample };
        if (missing.Count > 0)
            return step with { State = TrialStepState.Skipped, Reason = $"Runtime value unavailable for {string.Join(", ", missing.Select(m => "{" + m + "}"))} — the request is not sent with a placeholder and no sample value is substituted." };

        using var message = new HttpRequestMessage(new HttpMethod(sample.Method), origin.ToString().TrimEnd('/') + path.Value + Query(false));
        foreach (var (name, value) in headers.Where(h => !h.Name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)))
            message.Headers.TryAddWithoutValidation(name, value.Value);
        if (body.Value is not null)
        {
            message.Content = new StringContent(body.Value, Encoding.UTF8);
            message.Content.Headers.ContentType = MediaTypeHeaderValue.TryParse(headers.FirstOrDefault(h => h.Name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)).Value.Value ?? contentType, out var mt) ? mt : null;
        }

        var client = clients.CreateClient(ClientName);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        var watch = Stopwatch.StartNew();
        HttpResponseMessage response;
        try { response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return step with { State = TrialStepState.Failed, Reason = ex is HttpRequestException h ? $"No response: {SensitiveDataRedactor.RedactText(h.Message)}" : "No response within 15 seconds." };
        }
        using (response)
        {
            var (sampleResponse, parsed) = await ReadAsync(response, graphQl, watch, timeout.Token);
            var outputs = (s.Outputs ?? []).Select(o => Extract(o, response, sampleResponse, parsed)).ToList();
            foreach (var o in outputs) ctx[$"{s.Id}.{o.Name}"] = o.State == OutputSampleState.Ready ? RawValue(o, response, parsed, s) : null;
            return step with
            {
                State = TrialStepState.Sent, Response = sampleResponse, ExpectedStatus = s.ExpectedStatusCodes.Contains((int)response.StatusCode),
                Outputs = [.. outputs.Select(o => o with { Value = o.Value is null ? null : SensitiveDataRedactor.RedactText(o.Value) })],
            };
        }
    }

    /// <summary>The same envelope as the k6 script: query, operationName, variables (variables keep their {placeholders} until filled).</summary>
    private static string GraphQlBody(HttpPerformanceStep s)
    {
        var body = new JsonObject { ["query"] = s.GraphQlQuery ?? "" };
        if (!string.IsNullOrWhiteSpace(s.GraphQlOperationName)) body["operationName"] = s.GraphQlOperationName;
        if (!string.IsNullOrWhiteSpace(s.GraphQlVariablesJson)) body["variables"] = JsonNode.Parse(s.GraphQlVariablesJson);
        return body.ToJsonString();
    }

    /// <summary>Row 0 of the central Test Data resolved for this request (values never leave the backend except inside the request itself).</summary>
    private IReadOnlyDictionary<string, string> TestDataRow(PerformanceTestDefinition d)
    {
        if (!PerformanceTestSafety.UsesCentralTestData(d) || services.GetService(typeof(TestData.TestDataCatalog)) is not TestData.TestDataCatalog catalog
            || PerformanceCentralTestData.Resolve(d, catalog) is not { Rows.Count: > 0 } profile) return new Dictionary<string, string>();
        return profile.Columns.Select((c, i) => (c, v: profile.Rows[0].ElementAtOrDefault(i))).Where(x => x.v is not null).ToDictionary(x => x.c, x => x.v!, StringComparer.Ordinal);
    }

    /// <summary>A placeholder's value in the k6 precedence (mapping source, else a Test Data column of the same name); Secret = a Test Data value.</summary>
    private static (string? Value, bool Secret) Value(PerformanceTestDefinition d, HttpPerformanceStep s, string key, IReadOnlyDictionary<string, string> row, IReadOnlyDictionary<string, string?> ctx)
    {
        var m = s.DataMappings?.FirstOrDefault(x => x.Variable == key);
        if (m is null) return (row.GetValueOrDefault(key), true);
        return m.Source switch
        {
            RuntimeValueSource.TestData => (row.GetValueOrDefault(key), true),
            RuntimeValueSource.PreviousStep => (m.FromStepId is null || m.Output is null ? null : ctx.GetValueOrDefault($"{m.FromStepId}.{m.Output}"), false),
            RuntimeValueSource.Fixed or RuntimeValueSource.AutoFilled => (m.FixedValue, false),
            RuntimeValueSource.Generated => (m.Generator is { } g ? Generate(g) : null, false),
            RuntimeValueSource.ScenarioVariable => d.Scenario.Variables?.FirstOrDefault(v => v.Name == m.ScenarioVariable) switch
            {
                { Source: RuntimeValueSource.Fixed, Value: { } v } => (v, false),
                { Source: RuntimeValueSource.Generated, Generator: { } g } => (Generate(g), false),
                { Source: RuntimeValueSource.TestData } v => (row.GetValueOrDefault(PerformanceCentralTestData.VariableColumn(v.Name)), true),
                _ => (null, false),
            },
            _ => (null, false),
        };
    }

    private static string Generate(GeneratedValueKind g) => g switch
    {
        GeneratedValueKind.Uuid => Guid.NewGuid().ToString(),
        GeneratedValueKind.Timestamp => DateTimeOffset.UtcNow.ToString("O"),
        _ => Random.Shared.Next(0, 1_000_000_000).ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    // ── Response ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    private static async Task<(PerformanceResponseSample Sample, JsonNode? Json)> ReadAsync(HttpResponseMessage response, bool graphQl, Stopwatch watch, CancellationToken ct)
    {
        var contentType = response.Content.Headers.ContentType?.ToString();
        var headers = response.Headers.Concat(response.Content.Headers).Take(40)
            .Select(h => new PerformanceNameValue(h.Key, PerformanceRequestResponse.IsSensitiveHeader(h.Key) ? SensitiveDataRedactor.Mask
                : PerformanceRequestResponse.Bound(SensitiveDataRedactor.RedactText(string.Join(", ", h.Value)), 300)!)).ToList();
        var sample = new PerformanceResponseSample { Status = (int)response.StatusCode, Reason = response.ReasonPhrase, ContentType = contentType, Headers = headers };
        if (PerformanceRequestResponse.Of(contentType) == PayloadFormat.Stream)
        {
            watch.Stop();
            return (sample with
            {
                Format = PayloadFormat.Stream, DurationMs = watch.Elapsed.TotalMilliseconds, Capture = ResponseCaptureState.Unavailable,
                CaptureNote = "Streaming response — not buffered; preview unavailable.",
            }, null);
        }

        // Bounded read: keep at most MaxInspectedBytes, count the rest up to MaxCountedBytes (a lower bound beyond that).
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var kept = new MemoryStream();
        var buffer = new byte[16 * 1024];
        long size = 0;
        int n;
        while (size < MaxCountedBytes && (n = await stream.ReadAsync(buffer, ct)) > 0)
        {
            if (kept.Length < MaxInspectedBytes) kept.Write(buffer, 0, (int)Math.Min(n, MaxInspectedBytes - kept.Length));
            size += n;
        }
        var lowerBound = size >= MaxCountedBytes;
        watch.Stop();
        var format = PerformanceRequestResponse.ResponseFormat(contentType, graphQl, size);
        sample = sample with { Format = format, DurationMs = Math.Round(watch.Elapsed.TotalMilliseconds, 1), SizeBytes = size, SizeIsLowerBound = lowerBound };
        if (size == 0) return (sample with { Capture = ResponseCaptureState.MetadataOnly, CaptureNote = "No response body." }, null);
        if (format == PayloadFormat.Binary) return (sample with { Capture = ResponseCaptureState.MetadataOnly, CaptureNote = "Binary content — preview unavailable." }, null);

        var complete = size <= MaxInspectedBytes;
        var text = Decode(kept.ToArray(), response.Content.Headers.ContentType?.CharSet);
        string preview, raw;
        JsonNode? json = null;
        string? parseError = null;
        int? gqlErrors = null;
        var gqlMessages = new List<string>();
        bool? gqlData = null;
        if (!complete && format is PayloadFormat.Json or PayloadFormat.GraphQl or PayloadFormat.Xml)
            (raw, preview) = (text, SensitiveDataRedactor.RedactText(text));
        else if (format is PayloadFormat.Json or PayloadFormat.GraphQl)
        {
            try
            {
                json = JsonNode.Parse(text);
                var indented = new JsonSerializerOptions { WriteIndented = true };
                raw = json?.ToJsonString(indented) ?? "null";
                preview = SensitiveDataRedactor.RedactText(Redacted(json)?.ToJsonString(indented) ?? "null");
                if (format == PayloadFormat.GraphQl && json is JsonObject root)
                {
                    var errors = root["errors"] as JsonArray;
                    gqlErrors = errors?.Count ?? 0;
                    gqlMessages = [.. (errors ?? []).Take(3).Select(e => PerformanceRequestResponse.Bound(SensitiveDataRedactor.RedactText(e?["message"]?.ToString() ?? "(no message)"), 200)!)];
                    gqlData = root["data"] is { } data && data.GetValueKind() != JsonValueKind.Null;
                }
            }
            catch (JsonException) { parseError = "Invalid JSON response"; (raw, preview) = (text, SensitiveDataRedactor.RedactText(text)); }
        }
        else if (format == PayloadFormat.Xml)
        {
            try
            {
                using var reader = XmlReader.Create(new StringReader(text), PerformanceRequestResponse.SafeXml);
                var doc = XDocument.Load(reader);
                raw = doc.ToString(SaveOptions.None);
                foreach (var e in doc.Descendants().Where(e => PerformanceRequestResponse.IsSensitiveName(e.Name.LocalName) && !e.HasElements).ToList()) e.Value = SensitiveDataRedactor.Mask;
                foreach (var a in doc.Descendants().Attributes().Where(a => PerformanceRequestResponse.IsSensitiveName(a.Name.LocalName)).ToList()) a.Value = SensitiveDataRedactor.Mask;
                preview = SensitiveDataRedactor.RedactText(doc.ToString(SaveOptions.None));
            }
            catch (XmlException) { parseError = "Invalid XML response"; (raw, preview) = (text, SensitiveDataRedactor.RedactText(text)); }
        }
        else (raw, preview) = (text, SensitiveDataRedactor.RedactText(text));

        var bounded = PerformanceRequestResponse.Bound(preview, PerformanceRequestResponse.ResponsePreviewBytes)!;
        var shownBytes = Encoding.UTF8.GetByteCount(bounded);
        var truncated = !complete || shownBytes < Encoding.UTF8.GetByteCount(preview);
        var redacted = preview != raw;
        return (sample with
        {
            Preview = bounded, PreviewBytes = shownBytes, Redacted = redacted, ParseError = parseError, GraphQlErrors = gqlErrors, GraphQlErrorMessages = gqlMessages, GraphQlData = gqlData,
            Capture = truncated ? ResponseCaptureState.Truncated : redacted ? ResponseCaptureState.Redacted : ResponseCaptureState.Available,
            CaptureNote = truncated ? $"Displaying {PerformanceRequestResponse.Size(shownBytes)} of {(lowerBound ? "more than " : "")}{PerformanceRequestResponse.Size(size)}." + (!complete && format is PayloadFormat.Json or PayloadFormat.GraphQl or PayloadFormat.Xml ? " Too large to parse; shown as text." : "")
                : redacted ? "Sensitive values are redacted." : null,
        }, complete ? json : null);
    }

    private static string Decode(byte[] bytes, string? charset)
    {
        try { return (charset is null ? Encoding.UTF8 : Encoding.GetEncoding(charset.Trim('"'))).GetString(bytes); }
        catch (ArgumentException) { return Encoding.UTF8.GetString(bytes); }
    }

    /// <summary>A copy with values of credential-like fields (token, secret, password, …) replaced; the original stays for extraction.</summary>
    private static JsonNode? Redacted(JsonNode? node)
    {
        var copy = node?.DeepClone();
        void Walk(JsonNode? n)
        {
            if (n is JsonObject o) { foreach (var key in o.Select(p => p.Key).ToList()) { if (PerformanceRequestResponse.IsSensitiveName(key) && o[key] is JsonValue) o[key] = SensitiveDataRedactor.Mask; else Walk(o[key]); } }
            else if (n is JsonArray a) foreach (var item in a) Walk(item);
        }
        Walk(copy);
        return copy;
    }

    // ── Extraction (k6-equivalent: JSON body path or response header; one scalar value) ─────────────────────────────────

    private static PerformanceTrialOutput Extract(StepOutput o, HttpResponseMessage response, PerformanceResponseSample sample, JsonNode? json)
    {
        var expression = PerformanceRequestResponse.OutputExpression(o);
        PerformanceTrialOutput Out(OutputSampleState state, string? value, string? problem) => new(o.Name, o.From, expression, state, value, problem);
        if (PerformanceRequestResponse.OutputProblem(o) is { } invalid) return Out(OutputSampleState.Unsupported, null, invalid);
        if (o.From == StepOutputSource.Header)
        {
            var values = response.Headers.TryGetValues(o.Path, out var h) ? h.ToList() : response.Content.Headers.TryGetValues(o.Path, out var c) ? c.ToList() : [];
            return values.Count switch
            {
                0 => Out(OutputSampleState.Missing, null, $"The response has no {o.Path} header."),
                1 => Out(OutputSampleState.Ready, values[0], null),
                _ => Out(OutputSampleState.NeedsReview, null, $"The response has {values.Count} {o.Path} headers — not one value."),
            };
        }
        if (sample.Format == PayloadFormat.Xml) return Out(OutputSampleState.Unsupported, null, "XML extraction is not supported by the k6 load engine — extract from a JSON response or a header.");
        if (json is null) return Out(OutputSampleState.Missing, null, sample.ParseError ?? (sample.Format is PayloadFormat.Json or PayloadFormat.GraphQl ? "The response was too large to parse." : "The response is not JSON."));
        return Select(json, o.Path) switch
        {
            JsonValue v => Out(OutputSampleState.Ready, Scalar(v), null),
            JsonArray a => Out(OutputSampleState.NeedsReview, null, $"{o.Path} is a list of {a.Count} values — not one value; select one element, e.g. {o.Path}[0]."),
            JsonObject => Out(OutputSampleState.NeedsReview, null, $"{o.Path} is an object — select a field, e.g. {o.Path}.id."),
            _ => Out(OutputSampleState.Missing, null, $"{o.Path} is not in the response."),
        };
    }

    private static string? RawValue(PerformanceTrialOutput o, HttpResponseMessage response, JsonNode? json, HttpPerformanceStep s)
    {
        var output = s.Outputs!.First(x => x.Name == o.Name);
        if (output.From == StepOutputSource.Header)
            return response.Headers.TryGetValues(output.Path, out var h) ? h.Single() : response.Content.Headers.TryGetValues(output.Path, out var c) ? c.Single() : null;
        return json is null ? null : Select(json, output.Path) is JsonValue v ? Scalar(v) : null;
    }

    /// <summary>"$.data.items[0].id" over the parsed body; null when absent or JSON null.</summary>
    public static JsonNode? Select(JsonNode? root, string path)
    {
        var node = root;
        foreach (Match m in PathSegment().Matches(path))
        {
            node = m.Groups[1].Success ? (node as JsonObject)?[m.Groups[1].Value]
                : node is JsonArray a && int.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture) is var i && i < a.Count ? a[i] : null;
            if (node is null) return null;
        }
        return node is JsonValue v && v.GetValueKind() == JsonValueKind.Null ? null : node;
    }

    private static string Scalar(JsonValue v) => v.GetValueKind() switch
    {
        JsonValueKind.String => v.GetValue<string>(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => v.ToJsonString(),
    };
}
