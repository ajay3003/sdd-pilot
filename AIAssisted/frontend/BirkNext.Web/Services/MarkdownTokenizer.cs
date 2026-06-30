using System.Text.RegularExpressions;
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

    private static MarkdownToken Tok(MarkdownTokenKind k, int i, string raw, string content) =>
        new(k, i, raw, content, 0, null);

    private static IReadOnlyList<string> SplitCells(string line) =>
        line.Split('|', StringSplitOptions.TrimEntries)
            .Where(c => !string.IsNullOrEmpty(c))
            .ToList();

    private static bool IsSeparatorCell(string cell) =>
        cell.Replace("-", "").Replace(":", "").Replace(" ", "").Length == 0;
}
