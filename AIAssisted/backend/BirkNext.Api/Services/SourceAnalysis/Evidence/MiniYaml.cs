using System.Text;

namespace BirkNext.Api.Services.SourceAnalysis.Evidence;

internal enum YamlKind { Scalar, Map, Seq }

/// <summary>One YAML node with its 1-based line. Values stay in memory for the analysis only.</summary>
internal sealed class YamlNode
{
    public YamlKind Kind { get; init; }
    public int Line { get; init; }
    public string? Value { get; init; }
    /// <summary>True for a block scalar (| or >): script text, never a key/value.</summary>
    public bool Block { get; init; }
    public List<KeyValuePair<string, YamlNode>> Entries { get; } = [];
    public List<YamlNode> Items { get; } = [];

    public YamlNode? this[string key] => Kind == YamlKind.Map ? Entries.FirstOrDefault(e => e.Key.Equals(key, StringComparison.Ordinal)).Value : null;
    public YamlNode? Get(string key) => Kind == YamlKind.Map ? Entries.FirstOrDefault(e => e.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value : null;
    public bool Has(string key) => Kind == YamlKind.Map && Entries.Any(e => e.Key.Equals(key, StringComparison.Ordinal));
    public string? Str(string key) => this[key] is { Kind: YamlKind.Scalar } n ? n.Value : null;
    public IEnumerable<YamlNode> List(string key) => this[key] switch { { Kind: YamlKind.Seq } s => s.Items, { Kind: YamlKind.Scalar, Value: { } v } n => [n], _ => [] };
    public IEnumerable<string> Strings(string key) => List(key).Where(n => n.Kind == YamlKind.Scalar && n.Value is not null).Select(n => n.Value!);

    public static YamlNode Scalar(string? value, int line, bool block = false) => new() { Kind = YamlKind.Scalar, Value = value, Line = line, Block = block };
}

/// <summary>
/// A bounded YAML subset reader: block maps and sequences, inline "- key: value" items, quoted/plain scalars, block scalars (| >), simple flow
/// collections and multi-document files. Anchors/aliases, tags and complex keys are not resolved (reported through <see cref="Partial"/>).
/// It never executes or expands anything. Analyzers using it report their support as Partial.
/// </summary>
internal sealed class MiniYaml
{
    private readonly record struct Line(int Indent, string Text, int Number);
    private readonly List<Line> _lines;
    private int _i;
    public bool Partial { get; private set; }

    private MiniYaml(List<Line> lines) => _lines = lines;

    public static List<YamlNode> Documents(string text, out bool partial)
    {
        var documents = new List<YamlNode>();
        var anyPartial = false;
        var current = new List<Line>();
        var raw = text.Replace("\r\n", "\n").Split('\n');
        void Flush()
        {
            if (current.Count == 0) return;
            var reader = new MiniYaml(current);
            if (reader.ParseNode(current[0].Indent) is { } node) documents.Add(node);
            anyPartial |= reader.Partial || reader._i < current.Count;
            current = [];
        }
        for (var n = 0; n < raw.Length; n++)
        {
            var line = raw[n].Replace('\t', ' ');
            var trimmed = line.TrimEnd();
            if (trimmed is "---" or "..." || trimmed.StartsWith("--- ")) { Flush(); continue; }
            if (trimmed.TrimStart().StartsWith('%') && current.Count == 0) continue;
            var indent = line.Length - line.TrimStart().Length;
            current.Add(new Line(indent, trimmed.TrimStart(), n + 1));
        }
        Flush();
        partial = anyPartial;
        return documents;
    }

    public static YamlNode? Parse(string text) => Documents(text, out _).FirstOrDefault();

    private static string StripComment(string text)
    {
        var quote = '\0';
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quote != '\0') { if (c == quote) quote = '\0'; continue; }
            if ((c is '"' or '\'') && (i == 0 || text[i - 1] is ' ' or '[' or '{' or ',' or ':')) quote = c;
            else if (c == '#' && (i == 0 || text[i - 1] == ' ')) return text[..i].TrimEnd();
        }
        return text;
    }

    private bool Blank(int i) => _lines[i].Text.Length == 0 || StripComment(_lines[i].Text).Length == 0;
    private void SkipBlank() { while (_i < _lines.Count && Blank(_i)) _i++; }

    private YamlNode? ParseNode(int minIndent)
    {
        SkipBlank();
        if (_i >= _lines.Count) return null;
        var line = _lines[_i];
        if (line.Indent < minIndent) return null;
        var text = StripComment(line.Text);
        if (text == "-" || text.StartsWith("- ")) return ParseSeq(line.Indent);
        if (KeyOf(text) is not null) return ParseMap(line.Indent);
        _i++;
        return YamlNode.Scalar(Unquote(ContinueScalar(text, line.Indent)), line.Number);
    }

    private string ContinueScalar(string text, int indent)
    {
        var sb = new StringBuilder(text);
        while (_i < _lines.Count && !Blank(_i) && _lines[_i].Indent > indent && KeyOf(StripComment(_lines[_i].Text)) is null && !_lines[_i].Text.StartsWith("- "))
            sb.Append(' ').Append(StripComment(_lines[_i++].Text));
        return sb.ToString();
    }

    /// <summary>The key of a "key: value" line, or null. Handles quoted keys; ignores colons inside URLs/values (needs ": " or a trailing colon).</summary>
    private static (string Key, string Value)? KeyOf(string text)
    {
        if (text.Length == 0 || text[0] is '[' or '{' or '|' or '>' or '&' or '*' or '!') return null;
        if (text[0] is '"' or '\'')
        {
            var end = text.IndexOf(text[0], 1);
            if (end > 0 && end + 1 < text.Length && text[end + 1] == ':' && (end + 2 == text.Length || text[end + 2] == ' '))
                return (text[1..end], text[(end + 2)..].Trim());
            return null;
        }
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != ':') continue;
            if (i + 1 == text.Length || text[i + 1] == ' ')
            {
                var key = text[..i].Trim();
                if (key.Length == 0 || key.Contains(" #")) return null;
                return (key, i + 1 == text.Length ? "" : text[(i + 2)..].Trim());
            }
        }
        return null;
    }

    private YamlNode ParseMap(int indent)
    {
        var map = new YamlNode { Kind = YamlKind.Map, Line = _lines[_i].Number };
        while (true)
        {
            SkipBlank();
            if (_i >= _lines.Count) break;
            var line = _lines[_i];
            if (line.Indent != indent) break;
            var text = StripComment(line.Text);
            if (text.StartsWith("- ") || text == "-") break;
            if (KeyOf(text) is not { } kv) { Partial = true; _i++; continue; }
            _i++;
            var (key, rest) = kv;
            if (key == "<<") Partial = true;
            map.Entries.Add(new(key, Value(rest, indent, line.Number)));
        }
        return map;
    }

    private YamlNode Value(string rest, int indent, int number)
    {
        if (rest.StartsWith('&')) { Partial = true; var space = rest.IndexOf(' '); rest = space < 0 ? "" : rest[(space + 1)..].Trim(); }
        if (rest.StartsWith('!')) { var space = rest.IndexOf(' '); rest = space < 0 ? "" : rest[(space + 1)..].Trim(); }
        if (rest.StartsWith('*')) { Partial = true; return YamlNode.Scalar(null, number); }
        if (rest.Length == 0)
        {
            SkipBlank();
            if (_i < _lines.Count && (_lines[_i].Indent > indent || (_lines[_i].Indent == indent && (StripComment(_lines[_i].Text).StartsWith("- ") || StripComment(_lines[_i].Text) == "-"))))
                return ParseNode(_lines[_i].Indent) ?? YamlNode.Scalar(null, number);
            return YamlNode.Scalar(null, number);
        }
        if (rest[0] is '|' or '>') return BlockScalar(indent, number);
        if (rest[0] is '[' or '{') return Flow(Collect(rest, indent), number);
        return YamlNode.Scalar(Unquote(ContinueScalar(rest, indent)), number);
    }

    private YamlNode BlockScalar(int indent, int number)
    {
        var sb = new StringBuilder();
        while (_i < _lines.Count && (_lines[_i].Text.Length == 0 || _lines[_i].Indent > indent))
        {
            if (_lines[_i].Text.Length > 0) sb.Append(_lines[_i].Text).Append('\n');
            _i++;
        }
        return YamlNode.Scalar(sb.ToString(), number, block: true);
    }

    private string Collect(string rest, int indent)
    {
        var sb = new StringBuilder(rest);
        int Depth() => sb.ToString().Count(c => c is '[' or '{') - sb.ToString().Count(c => c is ']' or '}');
        while (Depth() > 0 && _i < _lines.Count && _lines[_i].Indent > indent) sb.Append(' ').Append(StripComment(_lines[_i++].Text));
        return sb.ToString();
    }

    private YamlNode ParseSeq(int indent)
    {
        var seq = new YamlNode { Kind = YamlKind.Seq, Line = _lines[_i].Number };
        while (true)
        {
            SkipBlank();
            if (_i >= _lines.Count) break;
            var line = _lines[_i];
            var text = StripComment(line.Text);
            if (line.Indent != indent || !(text == "-" || text.StartsWith("- "))) break;
            var content = text == "-" ? "" : text[2..].TrimStart();
            var offset = indent + (text.Length - content.Length);
            if (content.Length == 0) { _i++; seq.Items.Add(ParseNode(indent + 1) ?? YamlNode.Scalar(null, line.Number)); continue; }
            if (content.StartsWith("- ") || KeyOf(content) is not null && content[0] is not ('[' or '{'))
            {
                // "- key: value" starts a map (or nested sequence) whose first line is this one, indented at the content offset.
                _lines[_i] = new Line(offset, content, line.Number);
                seq.Items.Add(ParseNode(offset) ?? YamlNode.Scalar(null, line.Number));
                continue;
            }
            _i++;
            seq.Items.Add(Value(content, indent, line.Number));
        }
        return seq;
    }

    private static YamlNode Flow(string text, int line)
    {
        text = text.Trim();
        if (text.Length < 2) return YamlNode.Scalar(Unquote(text), line);
        var map = text[0] == '{';
        var inner = text[1..^1];
        var parts = new List<string>();
        var depth = 0; var quote = '\0'; var start = 0;
        for (var i = 0; i < inner.Length; i++)
        {
            var c = inner[i];
            if (quote != '\0') { if (c == quote) quote = '\0'; continue; }
            if (c is '"' or '\'') quote = c;
            else if (c is '[' or '{') depth++;
            else if (c is ']' or '}') depth--;
            else if (c == ',' && depth == 0) { parts.Add(inner[start..i]); start = i + 1; }
        }
        parts.Add(inner[start..]);
        var node = new YamlNode { Kind = map ? YamlKind.Map : YamlKind.Seq, Line = line };
        foreach (var part in parts.Select(p => p.Trim()).Where(p => p.Length > 0))
        {
            if (map)
            {
                var colon = part.IndexOf(':');
                if (colon < 0) node.Entries.Add(new(Unquote(part), YamlNode.Scalar(null, line)));
                else
                {
                    var value = part[(colon + 1)..].Trim();
                    node.Entries.Add(new(Unquote(part[..colon].Trim()), value.StartsWith('[') || value.StartsWith('{') ? Flow(value, line) : YamlNode.Scalar(Unquote(value), line)));
                }
            }
            else node.Items.Add(part.StartsWith('[') || part.StartsWith('{') ? Flow(part, line) : YamlNode.Scalar(Unquote(part), line));
        }
        return node;
    }

    internal static string? Unquote(string? value)
    {
        if (value is null) return null;
        value = value.Trim();
        if (value is "~" or "null" or "Null" or "NULL") return null;
        if (value.Length >= 2 && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\''))) return value[1..^1].Replace("''", "'");
        return value;
    }

    /// <summary>Leaf scalars as ("a:b:0:c", value, line). Block scalars are skipped unless asked for.</summary>
    public static IEnumerable<(string Key, string? Value, int Line, bool Block)> Flatten(YamlNode node, string prefix = "")
    {
        switch (node.Kind)
        {
            case YamlKind.Map:
                foreach (var (key, child) in node.Entries)
                    foreach (var leaf in Flatten(child, prefix.Length == 0 ? key : $"{prefix}:{key}")) yield return leaf;
                break;
            case YamlKind.Seq:
                for (var i = 0; i < node.Items.Count; i++)
                    foreach (var leaf in Flatten(node.Items[i], $"{prefix}:{i}")) yield return leaf;
                break;
            default:
                yield return (prefix, node.Value, node.Line, node.Block);
                break;
        }
    }
}
