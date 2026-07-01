using System.Text.RegularExpressions;

namespace BirkNext.Web.Services;

public static class MarkdownHelpers
{
    private static readonly Regex StripRe = new(@"[*_`#\[\]]", RegexOptions.Compiled);

    public static readonly Regex BoldLabelRe = new(@"\*\*(.+?):\*\*\s*(.*)", RegexOptions.Compiled);
    public static readonly Regex LinkRe      = new(@"\[([^\]]+)\]\(([^)]+)\)", RegexOptions.Compiled);

    // Matches cross-reference IDs: FR-001, NFR-02, US-001, TASK-003, T-1, etc.
    public static readonly Regex RefIdRe = new(
        @"\b(NFR|TASK|REQ|FR|US|UC|AC|SC|TS|PP|PS|MC|GV|ADR|T)-?\s*\d{1,4}\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static string StripMarkdown(string s) => StripRe.Replace(s, "").Trim();

    public static (string Key, string Value)? TryExtractBoldLabel(string text)
    {
        var m = BoldLabelRe.Match(text);
        return m.Success ? (m.Groups[1].Value.Trim(), m.Groups[2].Value.Trim()) : null;
    }

    public static (string Text, string Url)? TryExtractLink(string text)
    {
        var m = LinkRe.Match(text);
        return m.Success ? (m.Groups[1].Value, m.Groups[2].Value) : null;
    }

    public static IReadOnlyList<string> ExtractRefIds(string text) =>
        RefIdRe.Matches(text).Select(m => m.Value.ToUpperInvariant()).ToList();
}
