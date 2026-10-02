using System.Text;
using System.Text.RegularExpressions;

namespace BirkNext.Api.Services.SourceAnalysis.Evidence;

internal sealed record HclAttribute(string Name, string Expression, int Line);

internal sealed class HclBlock
{
    public string Type { get; init; } = "";
    public List<string> Labels { get; init; } = [];
    public int Line { get; init; }
    public List<HclAttribute> Attributes { get; } = [];
    public List<HclBlock> Blocks { get; } = [];

    public HclAttribute? Attribute(string name) => Attributes.FirstOrDefault(a => a.Name == name);
    public IEnumerable<HclBlock> Nested(string type) => Blocks.Where(b => b.Type == type);
    /// <summary>Every attribute of this block and its nested blocks, keyed "block.attr" for nested ones.</summary>
    public IEnumerable<(string Key, HclAttribute Attribute)> AllAttributes(string prefix = "")
    {
        foreach (var a in Attributes) yield return (prefix + a.Name, a);
        foreach (var b in Blocks) foreach (var x in b.AllAttributes(prefix + b.Type + ".")) yield return x;
    }
}

/// <summary>
/// Static HCL (Terraform) structure reader: blocks with labels, attributes with their raw expression text and line, nested blocks, strings with
/// interpolation, heredocs and the three comment styles. It evaluates nothing (no functions, no count/for_each expansion, no provider calls);
/// <see cref="Literal"/> resolves plain literals only. Expressions keep their raw text in memory for reference extraction and are never stored.
/// </summary>
internal sealed class HclReader
{
    private readonly string _s;
    private int _p;
    private int _line = 1;
    public List<string> Errors { get; } = [];

    private HclReader(string text) => _s = text.Replace("\r\n", "\n");

    public static HclBlock Parse(string text, out List<string> errors)
    {
        var reader = new HclReader(text);
        var root = new HclBlock { Type = "", Line = 1 };
        try { reader.Body(root, topLevel: true); }
        catch (FormatException ex) { reader.Errors.Add(ex.Message); }
        errors = reader.Errors;
        return root;
    }

    private char Peek(int offset = 0) => _p + offset < _s.Length ? _s[_p + offset] : '\0';
    private char Next() { var c = _s[_p++]; if (c == '\n') _line++; return c; }

    private void SkipTrivia(bool newlines)
    {
        while (_p < _s.Length)
        {
            var c = Peek();
            if (c == '\n' && !newlines) return;
            if (char.IsWhiteSpace(c)) { Next(); continue; }
            if (c == '#' || (c == '/' && Peek(1) == '/')) { while (_p < _s.Length && Peek() != '\n') Next(); continue; }
            if (c == '/' && Peek(1) == '*') { Next(); Next(); while (_p < _s.Length && !(Peek() == '*' && Peek(1) == '/')) Next(); if (_p < _s.Length) { Next(); Next(); } continue; }
            return;
        }
    }

    private static bool IdentStart(char c) => char.IsLetter(c) || c == '_';
    private static bool IdentPart(char c) => char.IsLetterOrDigit(c) || c is '_' or '-';

    private string Identifier()
    {
        var start = _p;
        while (_p < _s.Length && IdentPart(Peek())) Next();
        return _s[start.._p];
    }

    private void Body(HclBlock block, bool topLevel)
    {
        while (true)
        {
            SkipTrivia(newlines: true);
            if (_p >= _s.Length) { if (!topLevel) Errors.Add($"Unclosed block '{block.Type}' at line {block.Line}."); return; }
            if (Peek() == '}') { if (topLevel) { Errors.Add($"Unexpected '}}' at line {_line}."); Next(); continue; } Next(); return; }
            var line = _line;
            if (!IdentStart(Peek()) && Peek() != '"') { Errors.Add($"Unexpected character '{Peek()}' at line {_line}."); SkipLine(); continue; }
            var name = Peek() == '"' ? StringLiteral() : Identifier();
            SkipTrivia(newlines: false);
            if (Peek() == '=' && Peek(1) != '=') { Next(); block.Attributes.Add(new HclAttribute(name, Expression().Trim(), line)); continue; }
            var labels = new List<string>();
            while (_p < _s.Length && Peek() != '{')
            {
                SkipTrivia(newlines: false);
                if (Peek() == '"') labels.Add(StringLiteral());
                else if (IdentStart(Peek())) labels.Add(Identifier());
                else if (Peek() == '{') break;
                else { Errors.Add($"Unexpected token after '{name}' at line {_line}."); SkipLine(); labels = null!; break; }
                SkipTrivia(newlines: false);
            }
            if (labels is null || Peek() != '{') continue;
            Next();
            var child = new HclBlock { Type = name, Labels = labels, Line = line };
            Body(child, topLevel: false);
            block.Blocks.Add(child);
        }
    }

    private void SkipLine() { while (_p < _s.Length && Peek() != '\n') Next(); }

    private string StringLiteral()
    {
        var sb = new StringBuilder();
        Next(); // opening quote
        while (_p < _s.Length && Peek() != '"')
        {
            if (Peek() == '\\') { sb.Append(Next()); if (_p < _s.Length) sb.Append(Next()); continue; }
            if (Peek() == '$' && Peek(1) == '{') { sb.Append(Interpolation()); continue; }
            if (Peek() == '\n') break;
            sb.Append(Next());
        }
        if (Peek() == '"') Next();
        return sb.ToString();
    }

    private string Interpolation()
    {
        var sb = new StringBuilder();
        sb.Append(Next()).Append(Next()); // ${
        var depth = 1;
        while (_p < _s.Length && depth > 0)
        {
            var c = Peek();
            if (c == '"') { sb.Append('"').Append(StringLiteral()).Append('"'); continue; }
            if (c == '{') depth++;
            if (c == '}') depth--;
            sb.Append(Next());
        }
        return sb.ToString();
    }

    /// <summary>The raw expression up to the end of line at bracket depth 0 (multi-line lists/objects/calls and heredocs included).</summary>
    private string Expression()
    {
        var sb = new StringBuilder();
        var depth = 0;
        SkipTrivia(newlines: false);
        while (_p < _s.Length)
        {
            var c = Peek();
            if (c == '\n' && depth == 0) break;
            if (c == '"') { sb.Append('"').Append(StringLiteral()).Append('"'); continue; }
            if (c == '<' && Peek(1) == '<' && Regex.Match(_s[_p..Math.Min(_s.Length, _p + 80)], @"^<<-?([A-Za-z_][A-Za-z0-9_]*)\s*\n") is { Success: true } heredoc)
            {
                var marker = heredoc.Groups[1].Value;
                for (var i = 0; i < heredoc.Length; i++) Next();
                var body = new StringBuilder();
                while (_p < _s.Length)
                {
                    var start = _p;
                    SkipLine();
                    var text = _s[start.._p];
                    if (_p < _s.Length) Next();
                    if (text.Trim() == marker) break;
                    body.Append(text).Append('\n');
                }
                sb.Append("<<HEREDOC>>").Append(body.ToString().Length > 0 ? "\n" + body : "");
                break;
            }
            if (c == '#' || (c == '/' && Peek(1) == '/')) { if (depth == 0) break; SkipLine(); continue; }
            if (c == '/' && Peek(1) == '*') { SkipTrivia(newlines: true); continue; }
            if (c is '(' or '[' or '{') depth++;
            if (c is ')' or ']' or '}') { if (depth == 0) break; depth--; }
            sb.Append(Next());
        }
        return sb.ToString();
    }

    // ── Literal and reference helpers ────────────────────────────────────────────────────────────────────────────────────

    private static readonly Regex Interp = new(@"\$\{", RegexOptions.Compiled);

    /// <summary>A plain literal (quoted string without interpolation, number, bool); null for every expression.</summary>
    public static string? Literal(string expression)
    {
        var e = expression.Trim();
        if (e.Length >= 2 && e[0] == '"' && e[^1] == '"' && !Interp.IsMatch(e)) return e[1..^1].Replace("\\\"", "\"").Replace("\\\\", "\\");
        if (e is "true" or "false") return e;
        if (Regex.IsMatch(e, @"^-?\d+(\.\d+)?$")) return e;
        return null;
    }

    public static bool IsHeredoc(string expression) => expression.StartsWith("<<HEREDOC>>", StringComparison.Ordinal);

    private static readonly Regex Reference = new(@"(?<![\w.""])((?:var|local|module|data)\.[A-Za-z_][A-Za-z0-9_\-]*(?:\.[A-Za-z_][A-Za-z0-9_\-]*)?|[a-z][a-z0-9]*_[a-z0-9_]+\.[A-Za-z_][A-Za-z0-9_\-]*)", RegexOptions.Compiled);

    /// <summary>Address-like references in an expression: var.x, local.x, module.m(.out), data.t.n(.attr), type.name — strings' literal text excluded, interpolations included.</summary>
    public static IEnumerable<string> References(string expression)
    {
        var withoutLiterals = Regex.Replace(expression, @"""(?:[^""\\$]|\\.|\$(?!\{))*""", "\"\"");
        foreach (Match m in Reference.Matches(withoutLiterals)) yield return m.Groups[1].Value;
    }

    /// <summary>Top-level keys of an object expression ("{ a = 1, b = x }" → a, b) — names only.</summary>
    public static List<string> ObjectKeys(string expression) =>
        Regex.Matches(expression, @"(?m)(?:^|[{,\n])\s*""?([A-Za-z_][A-Za-z0-9_\-.]*)""?\s*[=:](?!=)").Select(m => m.Groups[1].Value).Distinct().ToList();
}
