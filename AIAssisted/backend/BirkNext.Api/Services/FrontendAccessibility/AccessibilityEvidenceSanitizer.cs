using System.Text.RegularExpressions;

namespace BirkNext.Api.Services.FrontendAccessibility;

public sealed partial class AccessibilityEvidenceSanitizer
{
    public const int MaxSnippetLength = 300;
    public const int MaxSummaryLength = 500;
    public const int MaxNodesPerRule = 3;

    public string SanitizeHtml(string? value) => Bound(Sanitize(value), MaxSnippetLength);
    public string SanitizeSummary(string? value) => Bound(Sanitize(value), MaxSummaryLength);
    public string SanitizeSelector(string? value) => Bound(value ?? string.Empty, 250);

    public List<AccessibilityFinding> SanitizeAuthenticatedFindings(List<AccessibilityFinding> findings)
    {
        if (findings is null || findings.Count == 0) return [];
        return findings.Select(f => f with
        {
            Selectors = [],
            HtmlSnippets = [],
            FailureSummaries = []
        }).ToList();
    }

    public string SanitizeAuthenticatedUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? uri.GetLeftPart(UriPartial.Authority)
            : "[REDACTED]";

    private static string Sanitize(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var sanitized = InputValueRegex().Replace(value, "$1[REDACTED]$3");
        sanitized = SecretRegex().Replace(sanitized, "[REDACTED]");
        sanitized = TokenQueryRegex().Replace(sanitized, "$1[REDACTED]");
        return sanitized;
    }

    private static string Bound(string value, int max) => value.Length <= max ? value : value[..max] + "…";

    [GeneratedRegex("(?i)(\\bvalue\\s*=\\s*['\"])(.*?)(['\"])")]
    private static partial Regex InputValueRegex();
    [GeneratedRegex("(?i)(SECRET-[A-Z0-9-]+|Bearer\\s+[A-Za-z0-9._~-]+|eyJ[A-Za-z0-9_-]+\\.[A-Za-z0-9_-]+\\.[A-Za-z0-9_-]+)")]
    private static partial Regex SecretRegex();
    [GeneratedRegex("(?i)([?&](?:token|access_token|api[_-]?key|code)=)[^&#\"'\\s>]+")]
    private static partial Regex TokenQueryRegex();
}
