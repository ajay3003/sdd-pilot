namespace BirkNext.Web.Models;

public enum MarkdownTokenKind
{
    Blank,
    Heading,
    BulletItem,      // -, *, + prefix — content is the text after the prefix
    OrderedItem,     // N. prefix — content is the text after the prefix
    TableRow,        // | cell | cell | — cells are pre-split and trimmed
    TableSeparator,  // |---|---| header/body separator
    FencedCodeStart, // opening ``` — content = language hint or ""
    FencedCodeLine,  // line inside a fenced block
    FencedCodeEnd,   // closing ```
    BlockQuote,      // > prefix — content stripped of leading >
    HorizontalRule,  // ---, ***, ___
    Text             // prose / paragraph line
}

public sealed record MarkdownToken(
    MarkdownTokenKind Kind,
    int LineIndex,                         // 0-based line number in the source
    string RawLine,                        // original line, TrimEnd only
    string Content,                        // semantic content (heading text, bullet body, prose)
    int HeadingLevel,                      // 1-6 for Heading; 0 for all other kinds
    IReadOnlyList<string>? TableCells      // non-null only for TableRow / TableSeparator
);
