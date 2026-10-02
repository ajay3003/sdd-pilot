using Path = System.IO.Path;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BirkNext.PerformanceTests;

namespace BirkNext.Api.Services.PerformanceTests;

/// <summary>
/// The first performance-test provider: invokes a local/configured k6 executable (not embedded, not forked) with a script BirkNext generates
/// from a validated definition. k6 concepts (VUs as executors, stages, metric keys, JavaScript) exist only inside this provider. Results are
/// read from k6's structured end-of-test summary (<c>handleSummary</c> JSON), never from console text.
/// </summary>
public sealed partial class K6PerformanceTestProvider(PerformanceTestOptions options, IProcessRunner runner, ILogger<K6PerformanceTestProvider> logger) : IPerformanceTestProvider
{
    /// <summary>handleSummary, k6/execution and ramping-arrival-rate are all available from this version.</summary>
    public static readonly Version MinimumVersion = new(0, 45, 0);

    public string ProviderId => PerformanceProviderIds.K6;
    public string DisplayName => "k6";

    public PerformanceProviderCapabilities Capabilities { get; } = new()
    {
        Http = true, GraphQl = true, Purposes = [.. Enum.GetValues<WorkloadPurpose>()], Modes = [WorkloadMode.VirtualUsers, WorkloadMode.ArrivalRate],
        Cancellation = true, Metrics = [.. Enum.GetValues<PerformanceMetric>()], RequiresExternalExecutable = true,
    };

    private string Executable => string.IsNullOrWhiteSpace(options.K6ExecutablePath) ? "k6" : options.K6ExecutablePath!;

    public async Task<PerformanceProviderStatus> StatusAsync(CancellationToken ct = default)
    {
        var status = new PerformanceProviderStatus { ProviderId = ProviderId, DisplayName = DisplayName, Capabilities = Capabilities };
        if (!string.IsNullOrWhiteSpace(options.K6ExecutablePath) && !File.Exists(options.K6ExecutablePath))
            return status with { Availability = ProviderAvailability.Misconfigured, Detail = "The configured k6 executable path (PerformanceTests:K6ExecutablePath) does not exist." };
        var outcome = await runner.RunAsync(new ProcessSpec(Executable, ["version"], Path.GetTempPath()), TimeSpan.FromSeconds(15), 4096, ct);
        if (outcome.StartError is not null || outcome.ExitCode != 0)
            return status with { Availability = ProviderAvailability.Unavailable, Detail = "k6 is not installed or configured on this BirkNext host. Install k6 or set PerformanceTests:K6ExecutablePath." };
        var match = VersionPattern().Match(outcome.StandardOutput + outcome.StandardError);
        if (!match.Success || !Version.TryParse(match.Groups[1].Value, out var version))
            return status with { Availability = ProviderAvailability.Misconfigured, Detail = "k6 started but did not report a recognisable version." };
        if (version < MinimumVersion)
            return status with { Availability = ProviderAvailability.VersionUnsupported, Version = version.ToString(), Detail = $"k6 {version} is older than the supported minimum {MinimumVersion}." };
        return status with { Availability = ProviderAvailability.Available, Version = version.ToString(), Detail = $"k6 {version} is available." };
    }

    public IReadOnlyList<string> Validate(PerformanceTestDefinition definition) =>
        Capabilities.Modes.Contains(definition.Workload.Mode) ? [] : [$"k6 does not support the {definition.Workload.Mode} workload mode."];

    public async Task<PerformanceProviderResult> ExecuteAsync(PerformanceProviderInput input, IProgress<string>? progress, CancellationToken ct)
    {
        var status = await StatusAsync(ct);
        if (status.Availability != ProviderAvailability.Available)
            return new PerformanceProviderResult { State = PerformanceRunState.ExecutionFailed, Reason = status.Detail, MetricsSource = "k6 (not run)" };
        Directory.CreateDirectory(input.WorkingDirectory);
        var scriptPath = Path.Combine(input.WorkingDirectory, "birknext-test.js");
        var summaryPath = Path.Combine(input.WorkingDirectory, "birknext-summary.json");
        try
        {
            await File.WriteAllTextAsync(scriptPath, K6ScriptGenerator.Generate(input.Definition, input.TestData, summaryPath), new UTF8Encoding(false), ct);
            progress?.Report("Running");
            var outcome = await runner.RunAsync(new ProcessSpec(Executable, ["run", "--no-color", "--quiet", "--no-usage-report", scriptPath], input.WorkingDirectory,
                new Dictionary<string, string> { ["K6_NO_USAGE_REPORT"] = "true" }), input.Timeout, options.MaxProviderOutputBytes, ct);
            var diagnostics = Redact(outcome.StandardError.Length > 0 ? outcome.StandardError : outcome.StandardOutput, options.MaxProviderOutputBytes);
            var source = $"k6 {status.Version} end-of-test summary (handleSummary JSON)";
            if (outcome.Cancelled)
                return new PerformanceProviderResult { State = PerformanceRunState.Cancelled, Reason = "Cancelled; the k6 process was terminated.", ProviderVersion = status.Version, MetricsSource = source,
                    Diagnostics = diagnostics, Limitations = ["A cancelled run has no end-of-test summary: no metrics are reported and nothing is assessed."] };
            if (outcome.TimedOut)
                return new PerformanceProviderResult { State = PerformanceRunState.TimedOut, Reason = $"The provider exceeded its {input.Timeout.TotalSeconds:0} s timeout; the k6 process was terminated.",
                    ProviderVersion = status.Version, MetricsSource = source, Diagnostics = diagnostics };
            if (outcome.StartError is not null)
                return new PerformanceProviderResult { State = PerformanceRunState.ExecutionFailed, Reason = outcome.StartError, ProviderVersion = status.Version, MetricsSource = source };
            // 0 = finished; 99 = a k6 threshold crossed (BirkNext only declares always-true thresholds to expose per-step metrics). Others = errors.
            if (outcome.ExitCode is not (0 or 99))
                return new PerformanceProviderResult { State = PerformanceRunState.ExecutionFailed, Reason = $"k6 exited with code {outcome.ExitCode}.", ProviderVersion = status.Version,
                    MetricsSource = source, Diagnostics = diagnostics };
            if (!File.Exists(summaryPath))
                return new PerformanceProviderResult { State = PerformanceRunState.ExecutionFailed, Reason = "k6 finished without writing its structured summary.", ProviderVersion = status.Version,
                    MetricsSource = source, Diagnostics = diagnostics };
            var (metrics, error) = K6SummaryParser.Parse(await File.ReadAllTextAsync(summaryPath, ct), input.Definition.Scenario.Steps.Select(s => s.Name).ToList());
            if (metrics is null)
                return new PerformanceProviderResult { State = PerformanceRunState.ExecutionFailed, Reason = error, ProviderVersion = status.Version, MetricsSource = source, Diagnostics = diagnostics };
            var limitations = new List<string>();
            if (metrics.DroppedIterations is > 0)
                limitations.Add($"The load generator dropped {metrics.DroppedIterations} iteration(s): it lacked capacity to sustain the configured rate. Measured throughput may reflect the generator, not the target.");
            return new PerformanceProviderResult { State = PerformanceRunState.Completed, Metrics = metrics, ProviderVersion = status.Version, MetricsSource = source, Diagnostics = diagnostics, Limitations = limitations };
        }
        finally
        {
            // The generated script and summary are temporary artifacts: removed after completion, cancellation, timeout or failure.
            try { if (Directory.Exists(input.WorkingDirectory)) Directory.Delete(input.WorkingDirectory, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { logger.LogWarning("Performance test temp directory could not be removed: {Type}", ex.GetType().Name); }
        }
    }

    internal static string Redact(string text, int max)
    {
        var safe = LocalHttpsProxy.SensitiveDataRedactor.RedactText(text);
        safe = BearerPattern().Replace(safe, "Bearer [redacted]");
        return safe.Length > max ? safe[..max] + "[truncated]" : safe;
    }

    [GeneratedRegex(@"v?(\d+\.\d+\.\d+)")] private static partial Regex VersionPattern();
    [GeneratedRegex(@"(?i)bearer\s+[A-Za-z0-9\-_.~+/]+=*")] private static partial Regex BearerPattern();
}

/// <summary>
/// Deterministic k6 script from a validated definition. Every user-supplied value (origin, paths, headers, bodies, test data, step names) enters
/// the script only as a JSON literal produced by System.Text.Json (which escapes quotes, backslashes, &lt;, &gt;, &amp;, U+2028/2029), so it cannot
/// escape a string or inject code. The same definition always yields the same script text.
/// </summary>
public static class K6ScriptGenerator
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public static string Generate(PerformanceTestDefinition d, PerformanceTestDataProfile? data, string summaryPath)
    {
        var steps = d.Scenario.Steps.Select(s => new JsonObject
        {
            ["name"] = s.Name, ["method"] = s.Method.ToUpperInvariant(), ["path"] = s.RelativePath,
            ["query"] = new JsonArray(s.QueryParameters.Select(q => (JsonNode)new JsonArray(q.Name, q.Value)).ToArray()),
            ["headers"] = new JsonObject(Headers(d, s).Select(h => KeyValuePair.Create(h.Key, (JsonNode?)h.Value))),
            ["body"] = Body(d, s), ["expected"] = new JsonArray(s.ExpectedStatusCodes.Distinct().Order().Select(c => (JsonNode)c).ToArray()),
            ["thinkMs"] = s.ThinkTimeMs ?? 0,
        }).ToList();
        var columns = data?.Columns ?? [];
        var rows = data?.Rows.Select(r => columns.Select((c, i) => (c, v: i < r.Count ? r[i] : "")).ToDictionary(x => x.c, x => x.v)).ToList() ?? [];
        var sb = new StringBuilder();
        sb.Append("// Generated by BirkNext from performance test definition ").Append(Lit(d.Id)).Append(" v").Append(d.Version).Append(". Do not edit.\n");
        sb.Append("import http from 'k6/http';\nimport { sleep } from 'k6';\nimport exec from 'k6/execution';\n");
        sb.Append("const ORIGIN = ").Append(Lit(d.TargetOrigin.TrimEnd('/'))).Append(";\n");
        sb.Append("const STEPS = ").Append(JsonSerializer.Serialize(steps, Json)).Append(";\n");
        sb.Append("const ROWS = ").Append(JsonSerializer.Serialize(rows, Json)).Append(";\n");
        sb.Append("const SELECTION = ").Append(Lit((data?.Selection ?? TestDataSelection.RoundRobin).ToString())).Append(";\n");
        sb.Append("const SUMMARY_PATH = ").Append(Lit(summaryPath)).Append(";\n");
        sb.Append("const thresholds = {};\nfor (const s of STEPS) {\n");
        sb.Append("  thresholds[`http_req_duration{name:${s.name}}`] = ['max>=0'];\n  thresholds[`http_req_failed{name:${s.name}}`] = ['rate>=0'];\n  thresholds[`http_reqs{name:${s.name}}`] = ['count>=0'];\n}\n");
        sb.Append("export const options = {\n  scenarios: { workload: ").Append(Scenario(d.Workload)).Append(" },\n");
        sb.Append("  thresholds,\n  summaryTrendStats: ['avg', 'min', 'med', 'max', 'p(90)', 'p(95)', 'p(99)'],\n  discardResponseBodies: true,\n};\n");
        sb.Append("""
            function row() {
              if (ROWS.length === 0) return {};
              let i;
              if (SELECTION === 'Sequential') i = exec.scenario.iterationInTest;
              else if (SELECTION === 'Random') i = Math.floor(Math.random() * ROWS.length);
              else if (SELECTION === 'UniquePerVirtualUser') i = exec.vu.idInTest - 1;
              else i = exec.vu.idInTest - 1 + exec.vu.iterationInInstance;
              return ROWS[i % ROWS.length];
            }
            function fill(text, r, encode) {
              if (text === null) return null;
              return text.replace(/\{([A-Za-z_][A-Za-z0-9_]*)\}/g, (m, k) => (Object.prototype.hasOwnProperty.call(r, k) ? (encode ? encodeURIComponent(r[k]) : r[k]) : m));
            }
            export default function () {
              const r = row();
              for (const s of STEPS) {
                const q = s.query.map(([k, v]) => `${encodeURIComponent(k)}=${encodeURIComponent(fill(v, r, false))}`).join('&');
                const url = ORIGIN + fill(s.path, r, true) + (q.length > 0 ? `?${q}` : '');
                http.request(s.method, url, fill(s.body, r, false), { headers: s.headers, tags: { name: s.name }, responseCallback: http.expectedStatuses(...s.expected) });
                if (s.thinkMs > 0) sleep(s.thinkMs / 1000);
              }
            }
            export function handleSummary(data) {
              const out = {};
              out[SUMMARY_PATH] = JSON.stringify({ birknext: 1, metrics: data.metrics, state: data.state });
              return out;
            }

            """);
        return sb.ToString();
    }

    private static string Lit(string value) => JsonSerializer.Serialize(value, Json);

    private static IEnumerable<KeyValuePair<string, string>> Headers(PerformanceTestDefinition d, HttpPerformanceStep s)
    {
        var headers = s.Headers.Select(h => KeyValuePair.Create(h.Name, h.Value)).ToList();
        var contentType = d.TargetType == PerformanceTargetType.GraphQlHttp ? "application/json" : s.ContentType;
        if (contentType is not null && !headers.Any(h => h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))) headers.Add(KeyValuePair.Create("Content-Type", contentType));
        return headers.OrderBy(h => h.Key, StringComparer.Ordinal);
    }

    private static string? Body(PerformanceTestDefinition d, HttpPerformanceStep s)
    {
        if (d.TargetType != PerformanceTargetType.GraphQlHttp) return s.BodyTemplate;
        var body = new JsonObject { ["query"] = s.GraphQlQuery ?? "" };
        if (!string.IsNullOrWhiteSpace(s.GraphQlOperationName)) body["operationName"] = s.GraphQlOperationName;
        if (!string.IsNullOrWhiteSpace(s.GraphQlVariablesJson)) body["variables"] = JsonNode.Parse(s.GraphQlVariablesJson);
        return body.ToJsonString(Json);
    }

    /// <summary>Generic workload → a k6 scenario. Virtual users → ramping-vus; arrival rate → ramping-arrival-rate per minute (integer rates).</summary>
    internal static string Scenario(PerformanceWorkload w)
    {
        string Stage(int seconds, double target) => $"{{ duration: '{seconds}s', target: {target.ToString(CultureInfo.InvariantCulture)} }}";
        var stages = new List<string>();
        if (w.Mode == WorkloadMode.VirtualUsers)
        {
            var vus = w.VirtualUsers ?? 1;
            var warm = PerformanceTestRules.WarmupVirtualUsers(w);
            if (w.WarmupSeconds > 0) stages.Add(Stage(w.WarmupSeconds, warm));
            if (w.RampUpSeconds > 0) stages.Add(Stage(w.RampUpSeconds, vus));
            if (w.SteadyStateSeconds > 0) stages.Add(Stage(w.SteadyStateSeconds, vus));
            if (w.RampDownSeconds > 0) stages.Add(Stage(w.RampDownSeconds, 0));
            return $"{{ executor: 'ramping-vus', startVUs: {(w.WarmupSeconds > 0 ? warm : 0)}, stages: [{string.Join(", ", stages)}], gracefulRampDown: '10s', gracefulStop: '10s' }}";
        }
        var target = (int)Math.Round((w.RequestsPerSecond ?? 1) * 60);
        var start = (int)Math.Round((w.StartRequestsPerSecond ?? Math.Max(1, (w.RequestsPerSecond ?? 1) / 10)) * 60);
        if (w.WarmupSeconds > 0) stages.Add(Stage(w.WarmupSeconds, start));
        if (w.RampUpSeconds > 0) stages.Add(Stage(w.RampUpSeconds, target));
        if (w.SteadyStateSeconds > 0) stages.Add(Stage(w.SteadyStateSeconds, target));
        if (w.RampDownSeconds > 0) stages.Add(Stage(w.RampDownSeconds, 0));
        var maxVus = Math.Max(1, w.VirtualUsers ?? 10);
        var pre = Math.Min(maxVus, Math.Max(1, (int)Math.Ceiling(w.RequestsPerSecond ?? 1)));
        return $"{{ executor: 'ramping-arrival-rate', startRate: {start}, timeUnit: '1m', preAllocatedVUs: {pre}, maxVUs: {maxVus}, stages: [{string.Join(", ", stages)}], gracefulStop: '10s' }}";
    }
}

/// <summary>Normalizes the k6 end-of-test summary into provider-independent metrics. Missing or malformed output is an error, never zero metrics.</summary>
public static class K6SummaryParser
{
    public static (PerformanceMetrics? Metrics, string? Error) Parse(string json, IReadOnlyList<string> stepNames)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(json); }
        catch (JsonException) { return (null, "The k6 summary is not valid JSON."); }
        if (root?["metrics"] is not JsonObject metrics) return (null, "The k6 summary has no metrics section.");
        var reqs = Values(metrics, "http_reqs");
        var duration = Values(metrics, "http_req_duration");
        if (reqs is null || duration is null) return (null, "The k6 summary has no HTTP request metrics (no request was issued or the summary is incomplete).");
        var count = Long(reqs, "count") ?? 0;
        var failedValues = Values(metrics, "http_req_failed");
        // http_req_failed is a Rate: "passes" counts failed requests (value true), "fails" counts successful ones.
        long? failed = Long(failedValues, "passes");
        long? ok = Long(failedValues, "fails");
        var steps = stepNames.Distinct().Select(name =>
        {
            var d = Values(metrics, $"http_req_duration{{name:{name}}}");
            var f = Values(metrics, $"http_req_failed{{name:{name}}}");
            var r = Values(metrics, $"http_reqs{{name:{name}}}");
            return d is null || r is null ? null : new PerformanceStepMetrics(name, Long(r, "count") ?? 0, Double(f, "rate") is { } rate ? Math.Round(rate * 100, 3) : null,
                Double(d, "p(95)"), Double(d, "p(99)"), Double(r, "rate"));
        }).Where(s => s is not null).Cast<PerformanceStepMetrics>().ToList();
        var stateMs = root["state"]?["testRunDurationMs"]?.GetValue<double>();
        return (new PerformanceMetrics
        {
            RequestCount = count, FailedRequests = failed ?? 0, SuccessfulRequests = ok ?? Math.Max(0, count - (failed ?? 0)),
            ErrorRatePercent = Double(failedValues, "rate") is { } er ? Math.Round(er * 100, 3) : null,
            RequestsPerSecond = Double(reqs, "rate") is { } rps ? Math.Round(rps, 3) : null,
            DurationSeconds = stateMs is { } ms ? Math.Round(ms / 1000, 1) : 0,
            Latency = new PerformanceLatency
            {
                MinMs = Double(duration, "min"), MeanMs = Double(duration, "avg"), P50Ms = Double(duration, "med"), P90Ms = Double(duration, "p(90)"),
                P95Ms = Double(duration, "p(95)"), P99Ms = Double(duration, "p(99)"), MaxMs = Double(duration, "max"),
            },
            Steps = steps,
            DroppedIterations = Long(Values(metrics, "dropped_iterations"), "count"),
        }, null);
    }

    private static JsonObject? Values(JsonObject metrics, string key) => metrics[key]?["values"] as JsonObject;

    private static double? Double(JsonObject? values, string key) =>
        values?[key] is JsonValue v && v.TryGetValue<double>(out var d) && double.IsFinite(d) ? Math.Round(d, 3) : null;

    private static long? Long(JsonObject? values, string key) => Double(values, key) is { } d ? (long)Math.Round(d) : null;
}
