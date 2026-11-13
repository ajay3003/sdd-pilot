using Markdig;
using Markdig.Syntax;
using System.Text.RegularExpressions;

namespace BirkNext.Web.Services;

/// <summary>
/// Shared Markdown rendering service for BirkNext artifact/document views.
/// Converts Markdown to safe HTML, preserving semantic structure.
/// Prevents HTML injection and dangerous URLs.
/// </summary>
public sealed class MarkdownRenderingService
{
    private readonly MarkdownPipeline _pipeline;
    private readonly MarkdownPipeline _pipelinePreserveSoftBreaks;

    public MarkdownRenderingService()
    {
        // Configure Markdig pipeline with common extensions
        var builder = new MarkdownPipelineBuilder()
            .UseAdvancedExtensions()
            .UsePipeTables()
            .UseTaskLists()
            .UseAutoIdentifiers();

        _pipeline = builder.Build();

        // Configure alternate pipeline with soft-line-break preservation (for Technical Context)
        var builderPreserve = new MarkdownPipelineBuilder()
            .UseAdvancedExtensions()
            .UsePipeTables()
            .UseTaskLists()
            .UseAutoIdentifiers()
            .UseSoftlineBreakAsHardlineBreak(); // Convert soft breaks to <br /> tags

        _pipelinePreserveSoftBreaks = builderPreserve.Build();
    }

    /// <summary>
    /// Render Markdown to HTML. HTML tags from user Markdown are escaped (not rendered).
    /// Links are validated to only allow safe schemes (http, https, mailto, relative).
    /// </summary>
    public string Render(string markdown, bool preserveSoftLineBreaks = false)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return string.Empty;

        // Use Markdig's built-in HTML rendering with appropriate pipeline
        var pipeline = preserveSoftLineBreaks ? _pipelinePreserveSoftBreaks : _pipeline;
        var html = Markdown.ToHtml(markdown, pipeline);

        // Post-process: sanitize links
        html = SanitizeHtmlLinks(html);

        // Strip any raw HTML that slipped through
        html = StripDangerousHtml(html);

        return html;
    }

    // Imported (untrusted) documents: raw HTML in the Markdown is never HTML here — it is shown as text — and headings get ids for navigation.
    private static readonly MarkdownPipeline UntrustedPipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions().UsePipeTables().UseTaskLists().UseAutoIdentifiers().DisableHtml().Build();

    /// <summary>
    /// Render an imported document for reading: raw HTML is escaped (never interpreted), links keep only safe schemes, and images that point
    /// outside the application are not loaded (their alt text is shown instead) — reading an imported document never contacts a remote host.
    /// </summary>
    public static string RenderUntrusted(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return string.Empty;
        var html = Markdown.ToHtml(markdown, UntrustedPipeline);
        html = SanitizeHtmlLinks(html);
        html = StripDangerousHtml(html);
        return RemoteImageRe.Replace(html, m => $"<span class=\"md-external-image\">[image not loaded: {System.Net.WebUtility.HtmlEncode(System.Net.WebUtility.HtmlDecode(AltRe.Match(m.Value).Groups["alt"].Value))}]</span>");
    }

    /// <summary>The headings of an imported document with the ids <see cref="RenderUntrusted"/> gives them (same pipeline), in order.</summary>
    public static IReadOnlyList<(int Level, string Text, string Id)> UntrustedHeadings(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return [];
        var document = Markdown.Parse(markdown, UntrustedPipeline);
        return [.. document.Descendants<Markdig.Syntax.HeadingBlock>().Select(h => (h.Level, InlineText(h.Inline), Markdig.Renderers.Html.HtmlAttributesExtensions.GetAttributes(h).Id ?? ""))];
    }

    private static string InlineText(Markdig.Syntax.Inlines.ContainerInline? inline) =>
        inline is null ? "" : string.Concat(inline.Descendants<Markdig.Syntax.Inlines.Inline>().Select(i => i switch
        {
            Markdig.Syntax.Inlines.LiteralInline literal => literal.Content.ToString(),
            Markdig.Syntax.Inlines.CodeInline code => code.Content,
            _ => "",
        })).Trim();

    // <img src="http(s)://…" or "//…"> — any image that is not a relative application path.
    private static readonly Regex RemoteImageRe = new(@"<img\b[^>]*?\bsrc=""(?:https?:)?//[^""]*""[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AltRe = new(@"\balt=""(?<alt>[^""]*)""", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Sanitize link URLs in rendered HTML to only allow safe schemes.
    /// Allows: http://, https://, mailto:, relative paths (/)
    /// Blocks: javascript:, data:, vbscript:, and other dangerous schemes
    /// </summary>
    private static string SanitizeHtmlLinks(string html)
    {
        // Match href="..." attributes in <a> tags
        var linkPattern = new Regex(@"href=[""']([^""']*)[""']", RegexOptions.IgnoreCase);

        return linkPattern.Replace(html, match =>
        {
            var url = match.Groups[1].Value;

            // Decode URL-encoded characters to check the actual scheme
            var decodedUrl = System.Net.WebUtility.UrlDecode(url);

            // Check if URL is safe
            if (IsSafeUrl(decodedUrl))
            {
                return match.Value; // Keep original
            }

            // Replace unsafe URL with #
            return @"href=""#""";
        });
    }

    /// <summary>
    /// Strip dangerous HTML tags that should never appear.
    /// </summary>
    private static string StripDangerousHtml(string html)
    {
        // Remove script tags and their content
        html = Regex.Replace(html, @"<script[^>]*>.*?</script>", "", RegexOptions.IgnoreCase | RegexOptions.Singleline);

        // Remove event handlers (onclick, onload, etc.)
        html = Regex.Replace(html, @"\s+on\w+=[""'][^""']*[""']", "", RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"\s+on\w+=\S+", "", RegexOptions.IgnoreCase);

        return html;
    }

    /// <summary>
    /// Determine if a URL is safe to use in href attribute.
    /// </summary>
    private static bool IsSafeUrl(string url)
    {
        if (string.IsNullOrEmpty(url))
            return true; // Empty href is safe

        // Browsers ignore whitespace and control characters inside a scheme ("java\tscript:"), and the href is HTML-encoded in the markup:
        // decode both and drop those characters before deciding.
        var normalized = new string(System.Net.WebUtility.HtmlDecode(url).Where(c => !char.IsWhiteSpace(c) && !char.IsControl(c)).ToArray()).ToLowerInvariant();

        // Allow http, https, mailto
        if (normalized.StartsWith("http://") ||
            normalized.StartsWith("https://") ||
            normalized.StartsWith("mailto:"))
            return true;

        // Any other scheme ("javascript:", "data:", "vbscript:", "file:" …) is blocked. A scheme is letters/digits/+.- before the first ':' that
        // comes before any '/', '?' or '#'; without one the URL is relative ("docs/a.md", "#section", "../x"). Previously a URL without "://"
        // counted as relative, which let "javascript:alert(1)" through.
        var colon = normalized.IndexOf(':');
        var firstDelimiter = normalized.IndexOfAny(['/', '?', '#']);
        var hasScheme = colon > 0 && (firstDelimiter < 0 || colon < firstDelimiter)
            && normalized[..colon].All(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '-' or '.');
        return !hasScheme;
    }
}
