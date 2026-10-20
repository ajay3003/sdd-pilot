using System.Net;
using Microsoft.AspNetCore.Components;

namespace BirkNext.Web.Services.SchemaStudio;

/// <summary>
/// Long schema paths, file names and namespaces have no spaces, so their single "word" sets a table column's minimum width. Offering a line
/// break after each separator keeps the text intact (copy, search and accessible name are unchanged) while letting the column stay narrow.
/// </summary>
public static class SchemaText
{
    private static readonly char[] Separators = ['/', '.', ':', '#'];

    public static MarkupString Breakable(string? text)
    {
        if (string.IsNullOrEmpty(text)) return new MarkupString("");
        var builder = new System.Text.StringBuilder(text.Length + 16);
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (Array.IndexOf(Separators, text[i]) < 0 || i == text.Length - 1) continue;
            builder.Append(WebUtility.HtmlEncode(text[start..(i + 1)])).Append("<wbr>");
            start = i + 1;
        }
        builder.Append(WebUtility.HtmlEncode(text[start..]));
        return new MarkupString(builder.ToString());
    }
}
