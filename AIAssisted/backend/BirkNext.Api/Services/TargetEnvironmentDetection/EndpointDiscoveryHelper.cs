using System.Text.RegularExpressions;

namespace BirkNext.Api.Services.TargetEnvironmentDetection;

/// <summary>
/// Helper for classifying and detecting endpoint URLs.
/// Reuses pattern logic from TargetEnvironmentHintExtractor but adapted for runtime inspection.
/// </summary>
public sealed class EndpointDiscoveryHelper
{
    private static readonly Regex ApiPathRegex = new(
        @"/(api|v\d+|rest)(/|$)",
        RegexOptions.IgnoreCase);

    private static readonly Regex GraphQlRegex = new(
        @"/(graphql|gql)(/|$|\?)",
        RegexOptions.IgnoreCase);

    private static readonly Regex SwaggerRegex = new(
        @"/(swagger|openapi|api-docs)(/|$)",
        RegexOptions.IgnoreCase);

    private static readonly Regex HealthRegex = new(
        @"/(health|healthz|health-check|ready|live)(/|$|\?)",
        RegexOptions.IgnoreCase);

    /// <summary>
    /// Classifies a path as API-related endpoint.
    /// Returns the endpoint type (REST, GraphQL, Swagger, Health) or null if not recognized.
    /// </summary>
    public string? ClassifyEndpointPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        if (HealthRegex.IsMatch(path))
            return "Health";

        if (SwaggerRegex.IsMatch(path))
            return "Swagger";

        if (GraphQlRegex.IsMatch(path))
            return "GraphQL";

        if (ApiPathRegex.IsMatch(path))
            return "REST";

        return null;
    }

    /// <summary>
    /// Determines if a URL path looks like a REST API endpoint.
    /// Examples: /api, /api/, /v1, /v1/users, /rest/config
    /// </summary>
    public bool IsLikelyRestEndpoint(string path)
    {
        return !string.IsNullOrWhiteSpace(path) && ApiPathRegex.IsMatch(path);
    }

    /// <summary>
    /// Determines if a URL path looks like a GraphQL endpoint.
    /// Examples: /graphql, /graphql/, /gql
    /// </summary>
    public bool IsLikelyGraphQlEndpoint(string path)
    {
        return !string.IsNullOrWhiteSpace(path) && GraphQlRegex.IsMatch(path);
    }

    /// <summary>
    /// Determines if a URL path looks like a Swagger/OpenAPI endpoint.
    /// Examples: /swagger, /swagger/v1/swagger.json, /openapi.json
    /// </summary>
    public bool IsLikelySwaggerEndpoint(string path)
    {
        return !string.IsNullOrWhiteSpace(path) && SwaggerRegex.IsMatch(path);
    }

    /// <summary>
    /// Determines if a URL path looks like a health check endpoint.
    /// Examples: /health, /healthz, /health-check, /ready, /live
    /// </summary>
    public bool IsLikelyHealthEndpoint(string path)
    {
        return !string.IsNullOrWhiteSpace(path) && HealthRegex.IsMatch(path);
    }

    /// <summary>
    /// Extracts the path component from a full URL.
    /// Example: https://api.example.com/health?live → /health?live
    /// </summary>
    public string? ExtractPath(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return null;

        return uri.PathAndQuery;
    }

    /// <summary>
    /// Constructs a full URL from base and path.
    /// Handles trailing slashes correctly.
    /// </summary>
    public string ConstructUrl(string baseUrl, string path)
    {
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(path))
            return "";

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri))
            return "";

        var trimmedPath = path.TrimStart('/');
        var basePath = baseUri.AbsolutePath.TrimEnd('/');

        return new Uri(baseUri, basePath + "/" + trimmedPath).ToString();
    }

    /// <summary>
    /// Determines if a URL is a safe candidate for safe probing.
    /// Filters out obviously unsafe patterns.
    /// </summary>
    public bool IsSafeProbeCandidate(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;

        // Must be HTTPS
        if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return false;

        // Avoid probing localhost or private ranges (should be caught by validator, but double-check)
        if (url.Contains("localhost", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("127.0.0.1") ||
            url.Contains("169.254") ||
            url.Contains("[::1]"))
            return false;

        return true;
    }

    /// <summary>
    /// Common REST endpoint candidate paths to probe.
    /// </summary>
    public IEnumerable<string> GetRestEndpointCandidates(string baseUrl)
    {
        return new[]
        {
            "/api",
            "/api/",
            "/v1",
            "/v1/",
            "/v2",
            "/v2/",
            "/rest",
            "/rest/"
        }.Select(path => ConstructUrl(baseUrl, path)).Where(u => !string.IsNullOrWhiteSpace(u));
    }

    /// <summary>
    /// Common GraphQL endpoint candidate paths to probe.
    /// </summary>
    public IEnumerable<string> GetGraphQlEndpointCandidates(string baseUrl)
    {
        return new[]
        {
            "/graphql",
            "/graphql/",
            "/gql",
            "/gql/",
            "/api/graphql",
            "/api/graphql/"
        }.Select(path => ConstructUrl(baseUrl, path)).Where(u => !string.IsNullOrWhiteSpace(u));
    }

    /// <summary>
    /// Common Swagger/OpenAPI endpoint candidate paths to probe.
    /// </summary>
    public IEnumerable<string> GetSwaggerEndpointCandidates(string baseUrl)
    {
        return new[]
        {
            "/swagger",
            "/swagger/v1/swagger.json",
            "/swagger/v1/swagger.yaml",
            "/swagger/v2/swagger.json",
            "/openapi.json",
            "/openapi.yaml",
            "/api-docs",
            "/api-docs/",
            "/.well-known/openapi.json"
        }.Select(path => ConstructUrl(baseUrl, path)).Where(u => !string.IsNullOrWhiteSpace(u));
    }

    /// <summary>
    /// Common health check endpoint candidate paths to probe.
    /// </summary>
    public IEnumerable<string> GetHealthEndpointCandidates(string baseUrl)
    {
        return new[]
        {
            "/health",
            "/healthz",
            "/health-check",
            "/health/live",
            "/health/ready",
            "/ready",
            "/live",
            "/.well-known/live",
            "/.well-known/ready"
        }.Select(path => ConstructUrl(baseUrl, path)).Where(u => !string.IsNullOrWhiteSpace(u));
    }
}
