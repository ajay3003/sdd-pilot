using System.Globalization;
using System.Text.RegularExpressions;

namespace BirkNext.Api.Services.SourceAnalysis.GeneratedDocumentation;

/// <summary>
/// Deterministic structure of a Markdown document: headings, tables (column style and property/value style), code spans and fenced blocks.
/// Structured keys are read ONLY from these structures (table cells under recognised column names, heading code spans, inline
/// "METHOD /path" spans) — never from prose similarity. Shared by generated and authored documentation comparisons.
/// </summary>
internal sealed class DocumentStructure
{
    internal sealed record Heading(int Level, string Text, int Line, string? Parent);
    internal sealed record Table(int Line, string? Heading, List<string> Headers, List<List<string>> Rows);

    public List<Heading> Headings { get; } = [];
    public List<Table> Tables { get; } = [];
    public List<(string Language, string Body, int Line)> Fences { get; } = [];
    public string Text { get; }
    public int LineCount { get; }

    private static readonly Regex CodeSpan = new(@"`([^`\r\n]{1,200})`", RegexOptions.Compiled);
    private static readonly Regex Separator = new(@"^\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?\s*$", RegexOptions.Compiled);
    private static readonly Regex HeadingLine = new(@"^(#{1,6})\s+(.+?)\s*#*\s*$", RegexOptions.Compiled);

    public DocumentStructure(string text)
    {
        Text = text ?? "";
        var lines = Text.Replace("\r\n", "\n").Split('\n');
        LineCount = lines.Length;
        var parents = new string?[7];
        string? current = null;
        var inFence = false; var fenceLanguage = ""; var fenceStart = 0; var fenceBody = new List<string>();
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                if (!inFence) { inFence = true; fenceLanguage = trimmed[3..].Trim().ToLowerInvariant(); fenceStart = i + 1; fenceBody.Clear(); }
                else { inFence = false; Fences.Add((fenceLanguage, string.Join("\n", fenceBody), fenceStart)); }
                continue;
            }
            if (inFence) { fenceBody.Add(line); continue; }
            if (HeadingLine.Match(line) is { Success: true } h)
            {
                var level = h.Groups[1].Value.Length;
                var headingText = h.Groups[2].Value.Trim();
                var parent = Enumerable.Range(1, level - 1).Reverse().Select(l => parents[l]).FirstOrDefault(p => p is not null);
                Headings.Add(new Heading(level, headingText, i + 1, parent));
                parents[level] = headingText;
                for (var l = level + 1; l < parents.Length; l++) parents[l] = null;
                current = headingText;
                continue;
            }
            if (trimmed.StartsWith('|') && i + 1 < lines.Length && Separator.IsMatch(lines[i + 1].Trim()))
            {
                var headers = Cells(trimmed);
                var rows = new List<List<string>>();
                var j = i + 2;
                for (; j < lines.Length && lines[j].TrimStart().StartsWith('|'); j++) rows.Add(Cells(lines[j].Trim()));
                Tables.Add(new Table(i + 1, current, headers, rows));
                i = j - 1;
            }
        }
    }

    private static List<string> Cells(string row)
    {
        var cells = new List<string>();
        var cell = new System.Text.StringBuilder();
        var inCode = false;
        for (var k = 0; k < row.Length; k++)
        {
            var c = row[k];
            if (c == '`') inCode = !inCode;
            if (c == '\\' && k + 1 < row.Length && row[k + 1] == '|') { cell.Append('|'); k++; continue; }
            if (c == '|' && !inCode) { cells.Add(cell.ToString().Trim()); cell.Clear(); continue; }
            cell.Append(c);
        }
        cells.Add(cell.ToString().Trim());
        if (cells.Count > 0 && cells[0].Length == 0) cells.RemoveAt(0);
        if (cells.Count > 0 && cells[^1].Length == 0) cells.RemoveAt(cells.Count - 1);
        return cells;
    }

    public static List<string> CodeSpans(string text) => CodeSpan.Matches(text ?? "").Select(m => m.Groups[1].Value.Trim()).Where(v => v.Length > 0).ToList();

    /// <summary>Plain text of a cell or heading: markup removed (bold, links, code ticks).</summary>
    public static string Plain(string text)
    {
        var t = Regex.Replace(text ?? "", @"\[([^\]]*)\]\([^)]*\)", "$1");
        return Regex.Replace(t, @"[*_`]", "").Trim();
    }

    /// <summary>Identifier-like values of a cell: its code spans, or the whole cell when it is a single identifier.</summary>
    public static List<string> Identifiers(string cell)
    {
        var spans = CodeSpans(cell);
        if (spans.Count > 0) return spans.Where(IsIdentifier).ToList();
        var plain = Plain(cell);
        return IsIdentifier(plain) ? [plain] : [];
    }

    public static bool IsIdentifier(string value) =>
        value.Length is > 0 and <= 160 && Regex.IsMatch(value, @"^[\p{L}\p{N}$][\p{L}\p{N}._\-/:{}$]*$") && !Regex.IsMatch(value, @"^[-—–]+$");

    /// <summary>Index of the first header matching <paramref name="pattern"/>, or -1.</summary>
    public static int Column(Table table, string pattern) =>
        table.Headers.FindIndex(h => Regex.IsMatch(Plain(h), pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));

    public static string Cell(List<string> row, int index) => index >= 0 && index < row.Count ? row[index] : "";

    // ── Dates and versions ────────────────────────────────────────────────────────────────────────────────────────────────

    private static readonly Regex DeclaredDate = new(
        @"(?im)^\s*(?:[#>*\-]+\s*|<!--\s*|//\s*)?(?:\*\*)?(?:x-)?(?:last[\s_-]?updated|last[\s_-]?generated|generated(?:[\s_-]?(?:on|at))?|updated|date)(?:\*\*)?\s*[:=]\s*[""']?(\d{4}-\d{2}-\d{2})(?:[T\s](\d{2}:\d{2}(?::\d{2})?)\s*(Z|[+-]\d{2}:?\d{2})?)?",
        RegexOptions.Compiled);

    /// <summary>A generation date the document declares in its header region ("Last updated: 2026-01-31", "x-last-updated: …", "# Last updated: …").
    /// Day precision unless a time with an offset is given.</summary>
    public static (DateTimeOffset At, string Precision, int Line)? DeclaredGenerationDate(string text)
    {
        var head = string.Join("\n", (text ?? "").Replace("\r\n", "\n").Split('\n').Take(40));
        var m = DeclaredDate.Match(head);
        if (!m.Success) return null;
        if (!DateTime.TryParseExact(m.Groups[1].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)) return null;
        var line = head[..m.Index].Count(c => c == '\n') + 1;
        if (m.Groups[2].Success && m.Groups[3].Success && DateTimeOffset.TryParse($"{m.Groups[1].Value}T{m.Groups[2].Value}{(m.Groups[3].Value == "Z" ? "+00:00" : m.Groups[3].Value)}",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var exact))
            return (exact, "Second", line);
        return (new DateTimeOffset(day, TimeSpan.Zero), "Day", line);
    }

    private static readonly Regex ChangelogVersion = new(@"(?m)^#{1,3}\s*\[?v?(\d+\.\d+(?:\.\d+)?(?:[-+][\w.]+)?)\]?\s*(?:[—–-]\s*|\(|\s)*(\d{4}-\d{2}-\d{2})?", RegexOptions.Compiled);
    private static readonly Regex Unreleased = new(@"(?im)^#{1,3}\s*\[?unreleased\]?\s*$", RegexOptions.Compiled);

    /// <summary>Versions a changelog declares (newest first, as written) with their dates, and whether an "Unreleased" section has entries.</summary>
    public static (List<(string Version, DateTimeOffset? Date)> Versions, bool UnreleasedEntries) Changelog(string text)
    {
        var versions = ChangelogVersion.Matches(text ?? "").Select(m => (m.Groups[1].Value,
            m.Groups[2].Success && DateTime.TryParseExact(m.Groups[2].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? new DateTimeOffset(d, TimeSpan.Zero) : (DateTimeOffset?)null)).ToList();
        var unreleased = false;
        if (Unreleased.Match(text ?? "") is { Success: true } u)
        {
            var after = text![(u.Index + u.Length)..];
            var next = Regex.Match(after, @"(?m)^#{1,2}\s");
            var body = next.Success ? after[..next.Index] : after;
            unreleased = Regex.IsMatch(body, @"(?m)^\s*[-*]\s+\S");
        }
        return (versions, unreleased);
    }

    public static int CompareVersions(string a, string b)
    {
        static int[] Parts(string v) => Regex.Match(v, @"^\d+(\.\d+){0,3}").Value.Split('.', StringSplitOptions.RemoveEmptyEntries).Select(p => int.TryParse(p, out var n) ? n : 0).ToArray();
        var x = Parts(a); var y = Parts(b);
        for (var i = 0; i < Math.Max(x.Length, y.Length); i++)
        {
            var c = (i < x.Length ? x[i] : 0).CompareTo(i < y.Length ? y[i] : 0);
            if (c != 0) return c;
        }
        return 0;
    }
}

/// <summary>Normalization of structured keys so the same thing written two ways matches exactly — and nothing else does.</summary>
internal static class StructuredKeys
{
    private static readonly Regex InlineRoute = new(@"\b(GET|POST|PUT|DELETE|PATCH)\s+(/[^\s`'"")<>|]*)", RegexOptions.Compiled);

    /// <summary>"GET /Orders/{id:guid}/" → "GET /orders/{}". Query strings, trailing slashes and parameter names/constraints are dropped.</summary>
    public static string Route(string method, string path)
    {
        var p = (path ?? "").Trim().Trim('`', '"', '\'');
        var q = p.IndexOfAny(['?', '#']);
        if (q >= 0) p = p[..q];
        p = Regex.Replace(p, @"\{[^}]*\}", "{}");
        p = Regex.Replace(p, @"(?<=/):[A-Za-z_]\w*", "{}");
        // A parameter segment that an upstream sanitizer already turned into "_name_" / "_id:guid_" is still a parameter.
        p = Regex.Replace(p, @"(?<=/)_[^/]*_(?=/|$)", "{}");
        p = Regex.Replace(p, "/{2,}", "/").TrimEnd('/');
        if (!p.StartsWith('/')) p = "/" + p;
        return $"{method.Trim().ToUpperInvariant()} {p.ToLowerInvariant()}";
    }

    public static IEnumerable<string> InlineRoutes(string text) => InlineRoute.Matches(text ?? "").Select(m => Route(m.Groups[1].Value, m.Groups[2].Value));

    /// <summary>Two routes are the same operation when methods match and one path ends with the other segment-wise (a route group or a
    /// gateway prefix may be outside the evidence). "ROUTE" matches any method (UI page routes).</summary>
    public static bool SameRoute(string a, string b)
    {
        var (ma, pa) = Split(a); var (mb, pb) = Split(b);
        if (ma != mb && ma != "ROUTE" && mb != "ROUTE") return false;
        if (pa == pb) return true;
        var sa = pa.Split('/', StringSplitOptions.RemoveEmptyEntries); var sb = pb.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var (shorter, longer) = sa.Length <= sb.Length ? (sa, sb) : (sb, sa);
        if (shorter.Length == 0) return false;
        return longer.Skip(longer.Length - shorter.Length).SequenceEqual(shorter);
    }

    private static (string Method, string Path) Split(string route)
    {
        var i = route.IndexOf(' ');
        return i < 0 ? ("ROUTE", route) : (route[..i], route[(i + 1)..]);
    }

    /// <summary>Configuration key as Configuration evidence normalizes it: lower case, ':'-separated ("__" folded).</summary>
    public static string ConfigurationKey(string key) => Evidence.ConfigurationAnalyzer.NormalizeKey(key ?? "");

    /// <summary>Name for exact case-insensitive comparison of entities/resources: schema prefix, quotes and separators ('_', '-') ignored.</summary>
    public static string Name(string name)
    {
        var n = (name ?? "").Trim().Trim('`', '"', '\'', '[', ']');
        var dot = n.LastIndexOf('.');
        if (dot > 0 && dot < n.Length - 1 && !n.Contains('/')) n = n[(dot + 1)..];
        return Regex.Replace(n, @"[_\-\s]", "").ToLowerInvariant();
    }

    public static string Resource(string name) => (name ?? "").Trim().Trim('`', '"', '\'').ToLowerInvariant();
}
