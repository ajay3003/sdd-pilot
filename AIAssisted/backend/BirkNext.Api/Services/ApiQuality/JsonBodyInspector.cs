using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BirkNext.ApiReview;

namespace BirkNext.Api.Services.ApiQuality;

/// <summary>
/// Turns a transiently parsed response body into structural evidence only: JSON paths with observed types (values are never kept),
/// error-leak indicator names, ProblemDetails detection and stable hashes. Used by the authenticated execution service (so bodies never
/// leave it) and by the API review engine for public responses.
/// </summary>
public static class JsonBodyInspector
{
    public const int MaxShapeEntries = 300;
    private const int MaxArrayItemsSampled = 5;

    private static readonly (string Name, Regex Pattern)[] LeakPatterns =
    [
        ("stack-trace", new Regex(@"\bat\s+[A-Za-z_][\w.<>`]*\.[A-Za-z_]\w*\s*\(|StackTrace|Traceback \(most recent call last\)|\n\s+at\s+\S+\s+\(", RegexOptions.Compiled)),
        ("exception-type", new Regex(@"\b[A-Za-z_][\w.]*Exception\b(?!\s*Type)", RegexOptions.Compiled)),
        ("source-file-path", new Regex(@"[A-Za-z]:\\[^""\s]+\.(cs|vb|fs|dll)|/[^""\s]+\.(cs|py|js|ts|java)(:line\s*\d+|:\d+)", RegexOptions.Compiled)),
        ("sql-or-connection-string", new Regex(@"\b(SqlException|Npgsql|ORA-\d{5}|ConnectionString|Data Source=|Server=.*;Database=)", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        ("internal-host-or-port", new Regex(@"\b(localhost|127\.0\.0\.1|0\.0\.0\.0):\d{2,5}\b", RegexOptions.Compiled)),
    ];

    /// <summary>Shape of a JSON document: one entry per path with the observed type; arrays sample the first items into a [*] path.</summary>
    public static List<JsonShapeEntry> Shape(JsonElement root)
    {
        var entries = new Dictionary<string, (HashSet<string> Types, bool Nullable)>(StringComparer.Ordinal);
        Walk(root, "$", entries, 0);
        return entries.Take(MaxShapeEntries)
            .Select(kv => new JsonShapeEntry(kv.Key, string.Join("|", kv.Value.Types.OrderBy(t => t, StringComparer.Ordinal)), kv.Value.Nullable))
            .ToList();
    }

    private static void Walk(JsonElement element, string path, Dictionary<string, (HashSet<string>, bool)> entries, int depth)
    {
        if (entries.Count >= MaxShapeEntries || depth > 12) return;
        var type = TypeOf(element);
        if (!entries.TryGetValue(path, out var existing))
        {
            existing = (new HashSet<string>(StringComparer.Ordinal), false);
            entries[path] = existing;
        }
        if (type == "null") entries[path] = (existing.Item1, true);
        else existing.Item1.Add(type);
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                    Walk(property.Value, $"{path}.{property.Name}", entries, depth + 1);
                break;
            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    if (index++ >= MaxArrayItemsSampled) break;
                    Walk(item, $"{path}[*]", entries, depth + 1);
                }
                break;
        }
    }

    public static string TypeOf(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => "object",
        JsonValueKind.Array => "array",
        JsonValueKind.String => "string",
        JsonValueKind.Number => element.TryGetInt64(out _) ? "integer" : "number",
        JsonValueKind.True or JsonValueKind.False => "boolean",
        JsonValueKind.Null => "null",
        _ => "unknown",
    };

    /// <summary>Indicator names only. The text is scanned transiently and never returned.</summary>
    public static List<string> LeakIndicators(string? text)
    {
        if (string.IsNullOrEmpty(text)) return [];
        var sample = text.Length > 64 * 1024 ? text[..(64 * 1024)] : text;
        return LeakPatterns.Where(p => p.Pattern.IsMatch(sample)).Select(p => p.Name).ToList();
    }

    public static bool IsProblemDetails(string? contentType, IReadOnlyList<JsonShapeEntry> shape)
    {
        if (contentType is not null && contentType.Contains("problem+json", StringComparison.OrdinalIgnoreCase)) return true;
        var paths = shape.Select(s => s.Path).ToHashSet(StringComparer.Ordinal);
        return paths.Contains("$.title") && paths.Contains("$.status") && (paths.Contains("$.type") || paths.Contains("$.detail") || paths.Contains("$.traceId"));
    }

    public static bool IsJsonMediaType(string? contentType) =>
        contentType is not null && (contentType.Contains("json", StringComparison.OrdinalIgnoreCase));

    /// <summary>Stable SHA-256 digest (first 16 bytes, hex) of a string, for contract/schema versions.</summary>
    public static string Hash(string content) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)))[..32];

    public static string Hash(IEnumerable<string> parts) => Hash(string.Join("\n", parts.OrderBy(p => p, StringComparer.Ordinal)));
}
