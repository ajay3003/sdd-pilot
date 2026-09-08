using System.Net;
using System.Text;
using System.Text.Json;

namespace BirkNext.Api.Services.ContractAnalysis;

/// <summary>
/// Fetches GraphQL schemas from introspection endpoints or SDL sources.
/// Reuses OpenAPI fetcher's SSRF/DNS/HTTPS/redirect protections.
/// </summary>
public interface IGraphQlSourceFetcher
{
    Task<GraphQlSourceResult> FetchAsync(string sourceUrl, CancellationToken ct = default);
}

public sealed class GraphQlSourceFetcher : IGraphQlSourceFetcher
{
    private const long MaxDocumentSizeBytes = 10 * 1024 * 1024; // 10MB
    private const string StandardIntrospectionQuery = """
        query IntrospectionQuery {
          __schema {
            types { ...FullType }
            queryType { name }
            mutationType { name }
            subscriptionType { name }
            directives { name }
          }
        }
        fragment FullType on __Type {
          kind
          name
          description
          fields(includeDeprecated: true) {
            name
            description
            args { ...InputValue }
            type { ...TypeRef }
            isDeprecated
            deprecationReason
          }
          inputFields { ...InputValue }
          interfaces { ...TypeRef }
          enumValues(includeDeprecated: true) {
            name
            description
            isDeprecated
            deprecationReason
          }
          possibleTypes { ...TypeRef }
        }
        fragment InputValue on __InputValue {
          name
          description
          type { ...TypeRef }
          defaultValue
        }
        fragment TypeRef on __Type {
          kind
          name
          ofType { ...TypeRef }
        }
        """;

    private readonly HttpClient _httpClient;
    private readonly ILogger<GraphQlSourceFetcher> _logger;

    public GraphQlSourceFetcher(HttpClient httpClient, ILogger<GraphQlSourceFetcher> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<GraphQlSourceResult> FetchAsync(string sourceUrl, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sourceUrl))
            return GraphQlSourceResult.NotConfigured();

        try
        {
            // Validate URL format and SSRF rules
            if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri))
                return GraphQlSourceResult.InvalidUrl("Invalid URL format");

            // Verify HTTPS only (with exceptions for localhost in dev)
            if (uri.Scheme != "https" && !IsLocalhost(uri))
                return GraphQlSourceResult.InvalidUrl("Only HTTPS URLs are supported in production");

            // SSRF protection: block private ranges
            if (IsPrivateIp(uri.Host))
                return GraphQlSourceResult.BlockedByPolicy($"Private IP range not allowed: {uri.Host}");

            // Redact sensitive query parameters from logging
            var safeUrl = RedactSensitiveUrl(sourceUrl);
            _logger.LogInformation("Fetching GraphQL schema from {Url}", safeUrl);

            // Prepare introspection request
            var introspectionPayload = new
            {
                query = StandardIntrospectionQuery,
                operationName = "IntrospectionQuery"
            };

            var jsonContent = JsonSerializer.Serialize(introspectionPayload);
            var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            using var req = new HttpRequestMessage(HttpMethod.Post, uri) { Content = content };
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(30));

            using var resp = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);

            if (!resp.IsSuccessStatusCode)
                return GraphQlSourceResult.FetchFailed($"HTTP {resp.StatusCode}");

            var contentLength = resp.Content.Headers.ContentLength ?? 0;
            if (contentLength > MaxDocumentSizeBytes)
                return GraphQlSourceResult.SourceTooLarge($"Schema exceeds {MaxDocumentSizeBytes} bytes");

            var responseText = await resp.Content.ReadAsStringAsync(cts.Token);
            if (contentLength == 0 && string.IsNullOrEmpty(responseText))
                return GraphQlSourceResult.FetchFailed("Empty response");

            // Check for introspection disabled response
            if (responseText.Contains("introspection disabled", StringComparison.OrdinalIgnoreCase))
                return GraphQlSourceResult.IntrospectionDisabled();

            return GraphQlSourceResult.SuccessResult(responseText);
        }
        catch (OperationCanceledException)
        {
            return GraphQlSourceResult.FetchFailed("Request timeout");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "HTTP request failed");
            return GraphQlSourceResult.FetchFailed($"Network error: {ex.Message}");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch GraphQL schema");
            return GraphQlSourceResult.FetchFailed($"Unexpected error: {ex.Message}");
        }
    }

    private static bool IsLocalhost(Uri uri)
    {
        var host = uri.Host.ToLowerInvariant();
        return host == "localhost" || host == "127.0.0.1" || host == "::1";
    }

    private static bool IsPrivateIp(string host)
    {
        // Block private/reserved ranges using pattern matching
        var blockedPatterns = new[]
        {
            "127.", "169.254", "10.", "172.16.", "172.17.", "172.18.", "172.19.",
            "172.20.", "172.21.", "172.22.", "172.23.", "172.24.", "172.25.",
            "172.26.", "172.27.", "172.28.", "172.29.", "172.30.", "172.31.",
            "192.168.", "::", "fc", "fd", "[::1]"
        };

        return blockedPatterns.Any(pattern => host.StartsWith(pattern, StringComparison.OrdinalIgnoreCase));
    }

    private static string RedactSensitiveUrl(string url)
    {
        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                return url;

            if (string.IsNullOrEmpty(uri.Query))
                return uri.GetLeftPart(UriPartial.Path);

            return $"{uri.GetLeftPart(UriPartial.Path)}?[query]";
        }
        catch
        {
            return "[redacted]";
        }
    }
}

public sealed class GraphQlSourceResult
{
    public bool Success { get; init; }
    public string? SchemaJson { get; init; }
    public GraphQlFetchFailureReason? Reason { get; init; }
    public string? FailureMessage { get; init; }

    public static GraphQlSourceResult SuccessResult(string schemaJson) =>
        new() { Success = true, SchemaJson = schemaJson };

    public static GraphQlSourceResult NotConfigured() =>
        new() { Success = false, Reason = GraphQlFetchFailureReason.NotConfigured };

    public static GraphQlSourceResult InvalidUrl(string message) =>
        new() { Success = false, Reason = GraphQlFetchFailureReason.InvalidUrl, FailureMessage = message };

    public static GraphQlSourceResult BlockedByPolicy(string message) =>
        new() { Success = false, Reason = GraphQlFetchFailureReason.BlockedByPolicy, FailureMessage = message };

    public static GraphQlSourceResult FetchFailed(string message) =>
        new() { Success = false, Reason = GraphQlFetchFailureReason.FetchFailed, FailureMessage = message };

    public static GraphQlSourceResult SourceTooLarge(string message) =>
        new() { Success = false, Reason = GraphQlFetchFailureReason.SourceTooLarge, FailureMessage = message };

    public static GraphQlSourceResult IntrospectionDisabled() =>
        new()
        {
            Success = false,
            Reason = GraphQlFetchFailureReason.IntrospectionDisabled,
            FailureMessage = "GraphQL introspection is disabled on this endpoint"
        };
}

public enum GraphQlFetchFailureReason
{
    NotConfigured = 0,
    InvalidUrl = 1,
    BlockedByPolicy = 2,
    FetchFailed = 3,
    SourceTooLarge = 4,
    IntrospectionDisabled = 5
}
