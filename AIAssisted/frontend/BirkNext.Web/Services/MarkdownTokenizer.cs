using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
    /// Preserves authored blocks that the structured Explorer projection did not consume. These blocks are rendered verbatim in the
    /// Explorer's Source Notes region; this is a deterministic exact-content check, not fuzzy text matching.
    /// </summary>
    public static List<MarkdownSourceNote> FindUnrepresentedBlocks(string markdown, object parsedProjection, bool preserveFreeTextForRender = false)
    {
        var projection = JsonSerializer.Serialize(parsedProjection);
        var documentFingerprint = DocumentFingerprint(markdown);
        var claimed = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var notes = new List<MarkdownSourceNote>();
        foreach (var token in Tokenize(markdown).Where(t => t.Kind is not (MarkdownTokenKind.Blank or MarkdownTokenKind.HorizontalRule or MarkdownTokenKind.TableSeparator)))
        {
            // Parser models may retain free-form prose/code/quotes in internal fields without the Explorer rendering those fields.
            // Keep these blocks in the shared full-text region so parser serialization cannot be mistaken for rendered evidence.
            var requiresDirectTextAccess = preserveFreeTextForRender && token.Kind is
                (MarkdownTokenKind.Text or MarkdownTokenKind.BlockQuote or MarkdownTokenKind.FencedCodeStart or MarkdownTokenKind.FencedCodeLine or MarkdownTokenKind.FencedCodeEnd);
            var values = token.Kind == MarkdownTokenKind.TableRow && token.TableCells is { Count: > 0 }
                ? token.TableCells.Where(c => !string.IsNullOrWhiteSpace(c)).Select(NormalizeInlineMarkup).ToArray()
                : [NormalizeInlineMarkup(token.Content.Trim())];
            var matches = !requiresDirectTextAccess && values.Length > 0 && values.All(value => Consume(value));
            if (matches) continue;
            var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token.RawLine.Trim())));
            notes.Add(new MarkdownSourceNote(CreateSourceBlockId(documentFingerprint, token.LineIndex, token.RawLine), token.LineIndex + 1, token.LineIndex + 1,
                token.Kind.ToString(), fingerprint, token.RawLine));
        }
        return notes;

        bool Consume(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return true;
            var count = 0;
            var offset = 0;
            while ((offset = projection.IndexOf(value, offset, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                count++;
                offset += value.Length;
            }
            var used = claimed.GetValueOrDefault(value);
            if (count <= used) return false;
            claimed[value] = used + 1;
            return true;
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
