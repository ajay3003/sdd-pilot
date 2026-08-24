namespace BirkNext.Api.Services.FrontendBrowserRuntime;

/// <summary>
/// Classifies browser resources (failed requests) by criticality.
/// Distinguishes between critical framework resources and non-critical resources.
/// </summary>
public sealed class BrowserResourceClassifier
{
    private static readonly HashSet<string> CriticalResourcePatterns = new(StringComparer.OrdinalIgnoreCase)
    {
        // Blazor WASM framework
        "blazor.webassembly.js",
        "blazor.boot.json",
        "_framework",
        "dotnet.wasm",
        "dotnet.js",

        // Critical startup resources
        "app.js",
        "app.css",
        "index.html",
    };

    private static readonly HashSet<string> NonCriticalPatterns = new(StringComparer.OrdinalIgnoreCase)
    {
        // Analytics, tracking, non-essential
        "google-analytics",
        "analytics",
        "googletagmanager",
        "gtag",
        "mixpanel",
        "segment",
        "amplitude",
        "hotjar",
        "intercom",
        "zendesk",
        "datadog",
        "newrelic",
        "favicon.ico",
        ".png",
        ".jpg",
        ".jpeg",
        ".gif",
        ".svg",
        ".webp",
        ".woff",
        ".woff2",
        ".ttf",
        ".eot",
    };

    public sealed record ResourceClassification(
        bool IsCritical,
        string Category,
        string ResourceType);

    public ResourceClassification Classify(string resourceUrl, string resourceType)
    {
        if (string.IsNullOrWhiteSpace(resourceUrl))
            return new ResourceClassification(false, "Unknown", "unknown");

        var url = resourceUrl.ToLowerInvariant();

        // Check critical patterns first
        foreach (var pattern in CriticalResourcePatterns)
        {
            if (url.Contains(pattern))
                return new ResourceClassification(true, "Critical", resourceType);
        }

        // Check non-critical patterns
        foreach (var pattern in NonCriticalPatterns)
        {
            if (url.Contains(pattern))
                return new ResourceClassification(false, "NonCritical", resourceType);
        }

        // Treat other resources as important but not critical
        return new ResourceClassification(false, "Important", resourceType);
    }
}
