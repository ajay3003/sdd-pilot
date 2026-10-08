namespace BirkNext.Web.Models;

/// <summary>A source Markdown block not mapped to a structured Explorer field, retained verbatim and rendered in its Source Notes section.</summary>
public sealed record MarkdownSourceNote(string BlockId, int StartLine, int EndLine, string BlockType, string Fingerprint, string Text);
