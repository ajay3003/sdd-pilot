using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BirkNext.ExecutionEvidence;

namespace BirkNext.Api.Services.ExecutionEvidence;

/// <summary>
/// Evaluates configured assertions against the response actually received (raw, in memory) and returns redacted results for persistence.
/// HTTP 200 is not GraphQL success and 404 is not automatically failure: only the configured assertions decide. No assertions = Not assessed.
/// </summary>
public static class ExecutionAssertions
{
    public sealed record GraphQlEnvelope(bool Parsed, bool HasData, int ErrorCount, List<string> Messages, List<string> Paths, List<string> Codes);

    /// <summary>GraphQL response envelope (data / errors / extensions.code / path), independent of the HTTP status.</summary>
    public static GraphQlEnvelope GraphQl(JsonNode? root)
    {
        if (root is not JsonObject o) return new(false, false, 0, [], [], []);
        var hasData = o.TryGetPropertyValue("data", out var data) && data is not null;
        var errors = o["errors"] as JsonArray ?? [];
        var messages = errors.Select(e => ExecutionEvidenceRedaction.Text(e?["message"]?.GetValueKind() == JsonValueKind.String ? e!["message"]!.GetValue<string>() : "").Text)
            .Select(m => m.Length > 300 ? m[..300] + "…" : m).Take(20).ToList();
        var paths = errors.Select(e => e?["path"] is JsonArray p ? string.Join('.', p.Select(x => x?.ToString())) : null).OfType<string>().Take(20).ToList();
        var codes = errors.Select(e => e?["extensions"]?["code"]?.ToString()).OfType<string>().Distinct().Take(20).ToList();
        return new(true, hasData, errors.Count, messages, paths, codes);
    }

    public static JsonNode? ParseJson(byte[]? raw, out bool invalid)
    {
        invalid = false;
        if (raw is null || raw.Length == 0) return null;
        try { return JsonNode.Parse(Encoding.UTF8.GetString(raw)); }
        catch (JsonException) { invalid = true; return null; }
    }

    /// <summary>Resolves "a.b[0].c" (optional leading "$."); null = absent. A present JSON null is returned as a JsonValue null marker via <paramref name="present"/>.</summary>
    public static JsonNode? Select(JsonNode? root, string path, out bool present)
    {
        present = false;
        var node = root;
        foreach (var raw in path.Trim().TrimStart('$').TrimStart('.').Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var part = raw; var name = part.Contains('[') ? part[..part.IndexOf('[')] : part;
            if (name.Length > 0) { if (node is not JsonObject o || !o.TryGetPropertyValue(name, out node)) return null; }
            foreach (var idx in part.Contains('[') ? part[part.IndexOf('[')..].Split('[', StringSplitOptions.RemoveEmptyEntries).Select(s => s.TrimEnd(']')) : [])
            {
                if (node is not JsonArray a || !int.TryParse(idx, NumberStyles.None, CultureInfo.InvariantCulture, out var i) || i < 0 || i >= a.Count) return null;
                node = a[i];
            }
        }
        present = true;
        return node;
    }

    private static string? Scalar(JsonNode? n) => n switch { null => null, JsonValue v when v.GetValueKind() == JsonValueKind.String => v.GetValue<string>(), _ => n.ToJsonString() };

    public static List<AssertionResult> Evaluate(IReadOnlyList<AssertionDefinition> assertions, CapturedResponse? response, byte[]? raw, ExecutionProtocol protocol)
    {
        var results = new List<AssertionResult>();
        JsonNode? json = null; var invalid = false; var parsed = false;
        foreach (var a in assertions)
        {
            AssertionResult R(string? actual, AssertionOutcome outcome, string why) =>
                new(a.Id, a.Kind, a.Operator, a.Target, ExecutionEvidenceRedaction.Value(a.Expected, a.Target), ExecutionEvidenceRedaction.Value(actual, a.Target), outcome, why);
            if (response is null) { results.Add(R(null, AssertionOutcome.NotAssessed, "No response was received, so the assertion could not be evaluated.")); continue; }
            if (!parsed && a.Kind is AssertionKind.JsonPath or AssertionKind.GraphQlDataPath or AssertionKind.GraphQlErrorCount or AssertionKind.GraphQlErrorCode
                    or AssertionKind.GraphQlErrorPath or AssertionKind.GraphQlHasData) { json = ParseJson(raw, out invalid); parsed = true; }
            switch (a.Kind)
            {
                case AssertionKind.StatusCode:
                    results.Add(Compare(a, response.StatusCode.ToString(CultureInfo.InvariantCulture), true, R, "HTTP status"));
                    break;
                case AssertionKind.ResponseTime:
                    results.Add(Compare(a, response.DurationMs.ToString(CultureInfo.InvariantCulture), true, R, "Client elapsed time (ms)"));
                    break;
                case AssertionKind.Header:
                    var header = response.Headers.FirstOrDefault(h => h.Name.Equals(a.Target, StringComparison.OrdinalIgnoreCase));
                    results.Add(Compare(a, header?.Value, header is not null, R, $"Header {a.Target}"));
                    break;
                case AssertionKind.JsonPath or AssertionKind.GraphQlDataPath:
                    if (invalid || json is null) { results.Add(R(null, a.Operator == AssertionOperator.NotExists && !invalid ? AssertionOutcome.Passed : AssertionOutcome.Failed,
                        invalid ? "The response body is not valid JSON." : "The response has no JSON body.")); break; }
                    var node = Select(a.Kind == AssertionKind.GraphQlDataPath ? json["data"] : json, a.Target ?? "", out var present);
                    results.Add(Compare(a, present ? Scalar(node) ?? "null" : null, present, R, $"Value at {a.Target}"));
                    break;
                default:
                    var env = GraphQl(json);
                    if (!env.Parsed) { results.Add(R(null, AssertionOutcome.Failed, invalid ? "The GraphQL response is not valid JSON." : "No GraphQL response envelope (data/errors).")); break; }
                    results.Add(a.Kind switch
                    {
                        AssertionKind.GraphQlErrorCount => Compare(a, env.ErrorCount.ToString(CultureInfo.InvariantCulture), true, R, "GraphQL error count"),
                        AssertionKind.GraphQlHasData => Compare(a, env.HasData ? "true" : "false", true, R, "GraphQL data present"),
                        AssertionKind.GraphQlErrorCode => Compare(a, string.Join(",", env.Codes), env.Codes.Count > 0, R, "GraphQL error codes"),
                        _ => Compare(a, string.Join(",", env.Paths), env.Paths.Count > 0, R, "GraphQL error paths"),
                    });
                    break;
            }
        }
        return results;
    }

    private static AssertionResult Compare(AssertionDefinition a, string? actual, bool present, Func<string?, AssertionOutcome, string, AssertionResult> R, string what)
    {
        var inv = CultureInfo.InvariantCulture;
        bool Num(string? s, out decimal d) => decimal.TryParse(s, NumberStyles.Float, inv, out d);
        var expected = a.Expected ?? "";
        var (ok, why) = a.Operator switch
        {
            AssertionOperator.Exists => (present, present ? $"{what} is present." : $"{what} is absent."),
            AssertionOperator.NotExists => (!present, present ? $"{what} is present but must be absent." : $"{what} is absent as expected."),
            AssertionOperator.Equals => (present && actual == expected, $"{what}: expected {expected}, actual {(present ? actual : "absent")}."),
            AssertionOperator.NotEquals => (!present || actual != expected, $"{what}: must not be {expected}, actual {(present ? actual : "absent")}."),
            AssertionOperator.Contains => (present && actual is not null && (actual.Split(',').Contains(expected) || actual.Contains(expected, StringComparison.Ordinal)), $"{what}: must contain {expected}, actual {(present ? actual : "absent")}."),
            AssertionOperator.In => (present && expected.Split(',', StringSplitOptions.TrimEntries).Contains(actual), $"{what}: expected one of {expected}, actual {(present ? actual : "absent")}."),
            AssertionOperator.LessThan => (present && Num(actual, out var x) && Num(expected, out var y) && x < y, $"{what}: must be < {expected}, actual {(present ? actual : "absent")}."),
            _ => (present && Num(actual, out var x2) && Num(expected, out var y2) && x2 > y2, $"{what}: must be > {expected}, actual {(present ? actual : "absent")}."),
        };
        return R(present ? actual : null, ok ? AssertionOutcome.Passed : AssertionOutcome.Failed, why);
    }

    /// <summary>Final verdict of a call from its last attempt and assertion results.</summary>
    public static ExecutionState Verdict(ExecutionAttempt last, IReadOnlyList<AssertionResult> results) => last.Transport switch
    {
        TransportResult.TimedOut => ExecutionState.TimedOut,
        TransportResult.Cancelled => ExecutionState.Cancelled,
        TransportResult.NotSent => ExecutionState.Blocked,
        TransportResult.ConnectionFailed => ExecutionState.Error,
        _ when results.Count == 0 => ExecutionState.NotAssessed,
        _ when results.Any(r => r.Outcome == AssertionOutcome.Failed) => ExecutionState.Failed,
        _ when results.All(r => r.Outcome == AssertionOutcome.Passed) => ExecutionState.Passed,
        _ => ExecutionState.NotAssessed,
    };
}
