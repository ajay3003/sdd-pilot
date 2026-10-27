namespace BirkNext.Web.Models;

/// <summary>A source Markdown block not mapped to a structured Explorer field, retained verbatim and rendered under "Source content not
/// structured". <paramref name="Section"/> is the nearest authored heading above the block (presentation context only, never a fact).</summary>
public sealed record MarkdownSourceNote(string BlockId, int StartLine, int EndLine, string BlockType, string Fingerprint, string Text, string? Section = null);
