using System.Text.RegularExpressions;

namespace BirkNext.Web.Services;

public enum XmlTokenKind { Text, Tag, AttributeName, AttributeValue, Punctuation, Comment, Declaration }

public sealed record XmlToken(XmlTokenKind Kind, string Text);

/// <summary>
/// Read-only syntax highlighting of one line of XML for the fixture preview: tags, attribute names and values, comments and the declaration. It
/// splits text only — the tokens joined are always the original line, so highlighting never changes what is shown or saved.
/// </summary>
public static partial class XmlHighlighter
{
    [GeneratedRegex("""(<!--.*?-->|<!--.*$)|(<\?.*?\?>)|(</?[\w:.\-]+)|([\w:.\-]+)(=)("[^"]*"|'[^']*')|(/?>)|(\s+|[^<>\s=]+|.)""")]
    private static partial Regex Pattern();

    public static IReadOnlyList<XmlToken> Tokens(string line)
    {
        var tokens = new List<XmlToken>();
        var inTag = false;
        foreach (Match m in Pattern().Matches(line))
        {
            if (m.Groups[1].Success) tokens.Add(new(XmlTokenKind.Comment, m.Value));
            else if (m.Groups[2].Success) tokens.Add(new(XmlTokenKind.Declaration, m.Value));
            else if (m.Groups[3].Success) { tokens.Add(new(XmlTokenKind.Tag, m.Value)); inTag = true; }
            else if (m.Groups[4].Success && inTag)
            {
                tokens.Add(new(XmlTokenKind.AttributeName, m.Groups[4].Value));
                tokens.Add(new(XmlTokenKind.Punctuation, m.Groups[5].Value));
                tokens.Add(new(XmlTokenKind.AttributeValue, m.Groups[6].Value));
            }
            else if (m.Groups[7].Success) { tokens.Add(new(XmlTokenKind.Tag, m.Value)); inTag = false; }
            else tokens.Add(new(XmlTokenKind.Text, m.Value));
        }
        // Never lose characters: anything the pattern skipped (should not happen) is returned as plain text instead.
        return string.Concat(tokens.Select(t => t.Text)) == line ? tokens : [new(XmlTokenKind.Text, line)];
    }
}
