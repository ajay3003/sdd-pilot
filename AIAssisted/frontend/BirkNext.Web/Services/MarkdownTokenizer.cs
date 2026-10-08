using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Text;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

public static class MarkdownTokenizer
{
    private static readonly Regex HeadingRe = new(@"^(#{1,6})\s+(.+)$", RegexOptions.Compiled);
    private static readonly Regex BulletRe  = new(@"^[-*+]\s+(.+)$",    RegexOptions.Compiled);
    private static readonly Regex OrderedRe = new(@"^\d+\.\s+(.+)$",    RegexOptions.Compiled);
    private static readonly Regex HrRe      = new(@"^([-*_])\s*\1\s*\1[\s\1]*$", RegexOptions.Compiled);

    public static IReadOnlyList<MarkdownToken> Tokenize(string markdown)
    {
        if (string.IsNullOrEmpty(markdown)) return [];

        var rawLines  = markdown.Split('\n');
        var result    = new List<MarkdownToken>(rawLines.Length);
        var inCode    = false;
        var codeFence = "```";

        for (var i = 0; i < rawLines.Length; i++)
        {
            var raw     = rawLines[i].TrimEnd();
            var trimmed = raw.TrimStart();

            // ── Inside a fenced code block ─────────────────────────────────────

            if (inCode)
            {
                if (trimmed == codeFence || trimmed.StartsWith(codeFence, StringComparison.Ordinal))
                {
                    result.Add(Tok(MarkdownTokenKind.FencedCodeEnd, i, raw, ""));
                    inCode = false;
                }
                else
                {
                    result.Add(Tok(MarkdownTokenKind.FencedCodeLine, i, raw, raw));
                }
                continue;
            }

            // ── Blank ──────────────────────────────────────────────────────────

            if (string.IsNullOrWhiteSpace(raw))
            {
                result.Add(Tok(MarkdownTokenKind.Blank, i, raw, ""));
                continue;
            }

            // ── Fenced code start ──────────────────────────────────────────────

            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                codeFence   = "```";
                var lang    = trimmed.Length > 3 ? trimmed[3..].Trim() : "";
                result.Add(Tok(MarkdownTokenKind.FencedCodeStart, i, raw, lang));
                inCode = true;
                continue;
            }

            // ── Heading ────────────────────────────────────────────────────────

            var hm = HeadingRe.Match(trimmed);
            if (hm.Success)
            {
                var level = hm.Groups[1].Length;
                var text  = hm.Groups[2].Value.Trim();
                result.Add(new MarkdownToken(MarkdownTokenKind.Heading, i, raw, text, level, null));
                continue;
            }

            // ── Horizontal rule ────────────────────────────────────────────────

            if (HrRe.IsMatch(trimmed))
            {
                result.Add(Tok(MarkdownTokenKind.HorizontalRule, i, raw, ""));
                continue;
            }

            // ── Pipe table ─────────────────────────────────────────────────────

            if (trimmed.StartsWith('|'))
            {
                var cells = SplitCells(trimmed);
                var isSep = cells.Count > 0 && cells.All(IsSeparatorCell);
                var kind  = isSep ? MarkdownTokenKind.TableSeparator : MarkdownTokenKind.TableRow;
                result.Add(new MarkdownToken(kind, i, raw, trimmed, 0, cells));
                continue;
            }

            // ── Bullet list item ───────────────────────────────────────────────

            var bm = BulletRe.Match(trimmed);
            if (bm.Success)
            {
                result.Add(Tok(MarkdownTokenKind.BulletItem, i, raw, bm.Groups[1].Value.Trim()));
                continue;
            }

            // ── Ordered list item ──────────────────────────────────────────────

            var om = OrderedRe.Match(trimmed);
            if (om.Success)
            {
                result.Add(Tok(MarkdownTokenKind.OrderedItem, i, raw, om.Groups[1].Value.Trim()));
                continue;
            }

            // ── Block quote ────────────────────────────────────────────────────

            if (trimmed.StartsWith('>')  )
            {
                result.Add(Tok(MarkdownTokenKind.BlockQuote, i, raw, trimmed.TrimStart('>').Trim()));
                continue;
            }

            // ── Prose / text ───────────────────────────────────────────────────

            result.Add(Tok(MarkdownTokenKind.Text, i, raw, trimmed));
        }

        return result;
    }

    /// <summary>
    /// Preserves source blocks that have no explicit construction-time projection range. These blocks are rendered verbatim in the
    /// Explorer's Source Notes region. Projection accounting is based only on provenance ranges, never serialized model text.
    /// </summary>
    public static List<MarkdownSourceNote> FindUnrepresentedBlocks(string markdown, object parsedProjection)
    {
        var documentFingerprint = DocumentFingerprint(markdown);
        var sourceRanges = GetProjectionSources(parsedProjection);
        var notes = new List<MarkdownSourceNote>();
        foreach (var token in Tokenize(markdown).Where(t => t.Kind is not (MarkdownTokenKind.Blank or MarkdownTokenKind.HorizontalRule or MarkdownTokenKind.TableSeparator)))
        {
            // Direct source notes are the full-text destination for any token that is not linked to an explicit projection.
            // This keeps repeated strings distinct by source line and avoids using JSON/display text as coverage evidence.
            if (sourceRanges.Any(source => token.LineIndex + 1 >= source.StartLine && token.LineIndex + 1 <= source.EndLine)) continue;
            var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token.RawLine.Trim())));
            notes.Add(new MarkdownSourceNote(CreateSourceBlockId(documentFingerprint, token.LineIndex, token.RawLine), token.LineIndex + 1, token.LineIndex + 1,
                token.Kind.ToString(), fingerprint, token.RawLine));
        }
        return notes;
    }

    private static IReadOnlyList<SourceRangeProvenance> GetProjectionSources(object root)
    {
        var sources = new Dictionary<string, SourceRangeProvenance>(StringComparer.Ordinal);
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        Visit(root);
        return sources.Values.ToArray();

        void Visit(object? value)
        {
            if (value is null || value is string || !visited.Add(value)) return;
            if (value is ProjectionProvenance projection)
            {
                foreach (var source in projection.Sources) sources.TryAdd(source.SourceBlockId, source);
                return;
            }
            if (value is System.Collections.IEnumerable sequence)
            {
                foreach (var item in sequence) Visit(item);
                return;
            }
            var type = value.GetType();
            if (type.IsPrimitive || type.IsEnum || type == typeof(decimal)) return;
            foreach (var property in type.GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public))
            {
                if (property.GetIndexParameters().Length != 0 || property.Name is "NodeId") continue;
                try { Visit(property.GetValue(value)); }
                catch (System.Reflection.TargetInvocationException) { }
            }
        }
    }

    public static string DocumentFingerprint(string markdown)
    {
        var canonical = markdown.TrimStart('\uFEFF').Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    public static string CreateSourceBlockId(string documentFingerprint, int zeroBasedLine, string rawLine)
    {
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawLine.Trim())));
        return $"{documentFingerprint[..16]}:{zeroBasedLine + 1}:{fingerprint[..12]}";
    }

    /// <summary>Removes only common Markdown presentation delimiters before exact authored-text accounting.</summary>
    public static string NormalizeInlineMarkup(string value)
    {
        var normalized = Regex.Replace(value, @"\[([^\]]+)\]\([^)]+\)", "$1");
        normalized = normalized.Replace("**", "", StringComparison.Ordinal).Replace("__", "", StringComparison.Ordinal)
            .Replace("`", "", StringComparison.Ordinal).Replace("*", "", StringComparison.Ordinal).Replace("_", "", StringComparison.Ordinal);
        return normalized;
    }

    private static MarkdownToken Tok(MarkdownTokenKind k, int i, string raw, string content) =>
        new(k, i, raw, content, 0, null);

    private static IReadOnlyList<string> SplitCells(string line) =>
        line.Split('|', StringSplitOptions.TrimEntries)
            .Where(c => !string.IsNullOrEmpty(c))
            .ToList();

    private static bool IsSeparatorCell(string cell) =>
        cell.Replace("-", "").Replace(":", "").Replace(" ", "").Length == 0;
}
