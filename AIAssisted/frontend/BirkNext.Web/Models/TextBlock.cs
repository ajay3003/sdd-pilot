namespace BirkNext.Web.Models;

internal sealed record TextBlock(
    string RawText,
    BlockType BlockType,
    int IndentationLevel,
    string? PrecedingHeading);
