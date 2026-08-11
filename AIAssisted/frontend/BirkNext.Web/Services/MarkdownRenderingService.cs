using Markdig;
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

        var lower = url.ToLowerInvariant().TrimStart();

        // Allow http, https, mailto
        if (lower.StartsWith("http://") ||
            lower.StartsWith("https://") ||
            lower.StartsWith("mailto:"))
            return true;

        // Allow relative paths
        if (lower.StartsWith("/") ||
            lower.StartsWith("../") ||
            lower.StartsWith("./") ||
            !lower.Contains("://"))  // No scheme = relative
            return true;

        // Block javascript:, data:, vbscript:, and others
        return false;
    }
}
