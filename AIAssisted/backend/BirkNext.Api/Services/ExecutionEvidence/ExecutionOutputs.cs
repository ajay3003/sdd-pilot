using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BirkNext.ExecutionEvidence;

namespace BirkNext.Api.Services.ExecutionEvidence;

/// <summary>
/// Outputs of chained calls: extraction from a received response (JSON path into the body, the GraphQL <c>data</c> object, or a non-sensitive
/// response header) with an optional expected type, and resolution of <c>{{steps.&lt;step&gt;.&lt;output&gt;}}</c> placeholders in a later call at
/// send time. JSON paths use the one path engine of the assertions. A value that looks like a credential is never extracted; an output that is
/// not available is reported, never replaced by a default.
/// </summary>
public static partial class ExecutionOutputs
{
    public const int MaxExtractions = 20;
    [GeneratedRegex(@"\{\{steps\.([A-Za-z0-9_\-]{1,64})\.([A-Za-z_][A-Za-z0-9_]{0,63})\}\}")] private static partial Regex AnyStep();
    [GeneratedRegex(@"^\{\{steps\.([A-Za-z0-9_\-]{1,64})\.([A-Za-z_][A-Za-z0-9_]{0,63})\}\}$")] private static partial Regex WholeStep();
    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]{0,63}$")] public static partial Regex OutputName();
    [GeneratedRegex(@"^[A-Za-z0-9_\-]{1,64}$")] public static partial Regex StepId();

    private static readonly string[] Types = ["String", "Integer", "Number", "Boolean", "Object", "Array"];

    /// <summary>Why an extraction definition is refused (null = acceptable).</summary>
    public static string? DefinitionProblem(OutputExtraction e)
    {
        if (!OutputName().IsMatch(e.Name ?? "")) return $"Output name \"{e.Name}\": letters, digits and '_' (max 64), starting with a letter or '_'.";
        if (string.IsNullOrWhiteSpace(e.Path) || e.Path.Length > 300) return $"Output {e.Name}: a path is required (max 300 characters).";
        if (ExecutionEvidenceRedaction.IsSensitiveName(e.Name) || ExecutionEvidenceRedaction.IsSensitiveName(e.Path))
            return $"Output {e.Name}: credentials, tokens, cookies and secrets are never extracted.";
        if (e.Source == OutputSource.Header && ExecutionEvidenceRedaction.IsSensitiveHeader(e.Path)) return $"Output {e.Name}: header {e.Path} carries credentials and is never extracted.";
        if (e.Type is { } t && !Types.Contains(t, StringComparer.Ordinal)) return $"Output {e.Name}: type is one of {string.Join(", ", Types)}.";
        return null;
    }

    /// <summary>Extracts the outputs of a received response. Values = the typed JSON value per found output (for later steps; memory only).</summary>
    public static (List<ExtractedOutput> Outputs, Dictionary<string, JsonNode?> Values) Extract(IReadOnlyList<OutputExtraction> extract, CapturedResponse? response, byte[]? raw)
    {
        var outputs = new List<ExtractedOutput>();
        var values = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        var root = ExecutionAssertions.ParseJson(raw, out _);
        foreach (var e in extract)
        {
            JsonNode? node = null; var found = false; string? problem = null;
            switch (e.Source)
            {
                case OutputSource.JsonBody: node = ExecutionAssertions.Select(root, e.Path, out found); break;
                case OutputSource.GraphQlData: node = ExecutionAssertions.Select((root as JsonObject)?["data"], e.Path, out found); break;
                case OutputSource.Header:
                    var header = response?.Headers.FirstOrDefault(h => h.Name.Equals(e.Path, StringComparison.OrdinalIgnoreCase));
                    if (header is { Redacted: true }) problem = "the header is redacted in evidence and is never extracted";
                    else if (header is not null) { node = JsonValue.Create(header.Value); found = true; }
                    break;
            }
            if (!found) problem ??= response is null ? "no response was received" : $"{e.Path} is not present in the {Describe(e.Source)}";
            else if (e.Type is { } type && !Matches(node, type)) { problem = $"{e.Path} is {Kind(node)}, not {type}"; found = false; }
            string? preview = null;
            if (found)
            {
                var text = Scalar(node);
                var (redacted, masked) = ExecutionEvidenceRedaction.Text(text);
                if (masked > 0) { found = false; problem = "the value looks like a credential and is never extracted"; }
                else { preview = redacted.Length > 120 ? redacted[..120] + "…" : redacted; values[e.Name] = node?.DeepClone(); }
            }
            outputs.Add(new(e.Name, e.Source, e.Path, e.Type ?? (found ? Kind(node) : null), found, preview, found ? null : problem));
        }
        return (outputs, values);
    }

    /// <summary>
    /// Fills the step placeholders of a call with the outputs of earlier steps: in JSON bodies and GraphQL variables a whole placeholder becomes
    /// the typed JSON value; in paths it is URL-escaped text; elsewhere text. Missing = placeholders whose output does not exist.
    /// </summary>
    public static (ExecutionCallDefinition Call, List<string> Missing, List<ResolvedValue> Used) Resolve(ExecutionCallDefinition d,
        IReadOnlyDictionary<string, Dictionary<string, JsonNode?>> outputs)
    {
        var missing = new List<string>();
        var used = new Dictionary<string, ResolvedValue>(StringComparer.Ordinal);
        bool Lookup(string step, string name, out JsonNode? node)
        {
            node = null;
            var key = $"steps.{step}.{name}";
            if (!outputs.TryGetValue(step, out var values) || !values.TryGetValue(name, out node)) { if (!missing.Contains(key)) missing.Add(key); return false; }
            var text = Scalar(node);
            used[key] = new(key, Kind(node), $"step {step} → {name}", text.Length > 120 ? text[..120] + "…" : text);
            return true;
        }
        string Text(string? text, bool escape) => text is null ? "" : AnyStep().Replace(text, m =>
            Lookup(m.Groups[1].Value, m.Groups[2].Value, out var node) ? escape ? Uri.EscapeDataString(Scalar(node)) : Scalar(node) : m.Value);
        string? Json(string? text)
        {
            if (text is null || !AnyStep().IsMatch(text)) return text;
            JsonNode? root;
            try { root = JsonNode.Parse(text); } catch (JsonException) { return Text(text, false); }
            return Walk(root)?.ToJsonString() ?? "null";
        }
        JsonNode? Walk(JsonNode? node)
        {
            switch (node)
            {
                case JsonObject o: foreach (var (k, v) in o.ToList()) o[k] = Walk(v); return o;
                case JsonArray a: for (var i = 0; i < a.Count; i++) a[i] = Walk(a[i]); return a;
                case JsonValue v when v.GetValueKind() == JsonValueKind.String:
                    var s = v.GetValue<string>();
                    if (WholeStep().Match(s) is { Success: true } m) return Lookup(m.Groups[1].Value, m.Groups[2].Value, out var value) ? value?.DeepClone() : node;
                    return AnyStep().IsMatch(s) ? JsonValue.Create(Text(s, false)) : node;
                default: return node;
            }
        }
        var json = (d.ContentType ?? "application/json").Contains("json", StringComparison.OrdinalIgnoreCase);
        var call = d with
        {
            Path = Text(d.Path, true),
            Query = d.Query.ToDictionary(q => q.Key, q => Text(q.Value, false), StringComparer.Ordinal),
            Headers = d.Headers.ToDictionary(h => h.Key, h => Text(h.Value, false), StringComparer.OrdinalIgnoreCase),
            Body = d.Body is null ? null : json ? Json(d.Body) : Text(d.Body, false),
            GraphQlVariablesJson = Json(d.GraphQlVariablesJson),
        };
        return (call, missing, [.. used.Values]);
    }

    /// <summary>Every step an unresolved call refers to through placeholders.</summary>
    public static IEnumerable<string> ReferencedSteps(ExecutionCallDefinition d) =>
        new[] { d.Path, d.Body, d.GraphQlVariablesJson }.Concat(d.Query.Values).Concat(d.Headers.Values)
            .SelectMany(t => AnyStep().Matches(t ?? "").Select(m => m.Groups[1].Value)).Distinct(StringComparer.Ordinal);

    private static string Describe(OutputSource source) => source switch { OutputSource.GraphQlData => "GraphQL data", OutputSource.Header => "response headers", _ => "response body" };

    private static string Scalar(JsonNode? n) => n switch { null => "null", JsonValue v when v.GetValueKind() == JsonValueKind.String => v.GetValue<string>(), _ => n.ToJsonString() };

    private static string Kind(JsonNode? n) => n switch
    {
        null => "Null", JsonObject => "Object", JsonArray => "Array",
        JsonValue v => v.GetValueKind() switch
        {
            JsonValueKind.String => "String", JsonValueKind.True or JsonValueKind.False => "Boolean",
            JsonValueKind.Number => v.ToJsonString().Contains('.') || v.ToJsonString().Contains('e') || v.ToJsonString().Contains('E') ? "Number" : "Integer",
            _ => "Null",
        },
        _ => "Null",
    };

    private static bool Matches(JsonNode? node, string type) => type == "Number" ? Kind(node) is "Number" or "Integer" : Kind(node) == type;
}
