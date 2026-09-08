using System.Net;
using System.Text.Json;

namespace BirkNext.Api.Services.ContractAnalysis;

/// <summary>
/// Fetches OpenAPI documents from configured sources with SSRF protection.
/// </summary>
public interface IOpenApiSourceFetcher
{
    Task<OpenApiSourceResult> FetchAsync(string sourceUrl, CancellationToken ct = default);
}

public sealed class OpenApiSourceFetcher : IOpenApiSourceFetcher
{
    private const long MaxDocumentSizeBytes = 10 * 1024 * 1024; // 10MB

    private readonly HttpClient _httpClient;
    private readonly ILogger<OpenApiSourceFetcher> _logger;

    public OpenApiSourceFetcher(HttpClient httpClient, ILogger<OpenApiSourceFetcher> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<OpenApiSourceResult> FetchAsync(string sourceUrl, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sourceUrl))
            return OpenApiSourceResult.NotConfigured();

        try
        {
            // Validate URL format and SSRF rules
            if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri))
                return OpenApiSourceResult.InvalidUrl("Invalid URL format");

            // Verify HTTPS only (with exceptions for localhost in dev)
            if (uri.Scheme != "https" && !IsLocalhost(uri))
                return OpenApiSourceResult.InvalidUrl("Only HTTPS URLs are supported in production");

            // SSRF protection: block private ranges
            if (IsPrivateIp(uri.Host))
                return OpenApiSourceResult.BlockedByPolicy($"Private IP range not allowed: {uri.Host}");

            // Redact sensitive query parameters from logging
            var safeUrl = RedactSensitiveUrl(sourceUrl);
            _logger.LogInformation("Fetching OpenAPI from {Url}", safeUrl);

            using var req = new HttpRequestMessage(HttpMethod.Get, uri);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(30));

            using var resp = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);

            if (!resp.IsSuccessStatusCode)
                return OpenApiSourceResult.FetchFailed($"HTTP {resp.StatusCode}");

            var contentLength = resp.Content.Headers.ContentLength ?? 0;
            if (contentLength > MaxDocumentSizeBytes)
                return OpenApiSourceResult.SourceTooLarge($"Document size {contentLength} exceeds {MaxDocumentSizeBytes}");

            var json = await resp.Content.ReadAsStringAsync(cts.Token);

            if (json.Length > MaxDocumentSizeBytes)
                return OpenApiSourceResult.SourceTooLarge($"Content size exceeds limit");

            // Basic validation that it's valid JSON
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                    return OpenApiSourceResult.ParseFailed("Root element is not an object");
            }
            catch (JsonException ex)
                {
                return OpenApiSourceResult.ParseFailed($"Invalid JSON: {ex.Message}");
            }

            return OpenApiSourceResult.SuccessResult(json, safeUrl);
        }
        catch (OperationCanceledException)
            {
            return OpenApiSourceResult.FetchFailed("Request timeout (>30s)");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "HTTP request failed for OpenAPI source");
            return OpenApiSourceResult.FetchFailed($"Network error: {ex.Message}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error fetching OpenAPI");
            return OpenApiSourceResult.FetchFailed($"Unexpected error: {ex.GetType().Name}");
        }
    }

    private static bool IsLocalhost(Uri uri)
    {
        if (uri.IsLoopback) return true;
        return uri.Host == "localhost" || uri.Host == "127.0.0.1" || uri.Host == "::1";
    }

    private static bool IsPrivateIp(string host)
    {
        // Block private/reserved ranges
        var blockedPatterns = new[]
        {
            "127.", "169.254", "10.", "172.16.", "172.17.", "172.18.", "172.19.",
            "172.20.", "172.21.", "172.22.", "172.23.", "172.24.", "172.25.",
            "172.26.", "172.27.", "172.28.", "172.29.", "172.30.", "172.31.",
            "192.168.", "::", "fc", "fd"
        };

        return blockedPatterns.Any(pattern => host.StartsWith(pattern, StringComparison.OrdinalIgnoreCase));
    }

    private static string RedactSensitiveUrl(string url)
    {
        // Remove sensitive query parameters
        var sensitiveParams = new[] { "token", "key", "auth", "password", "secret", "bearer" };
        var uri = new Uri(url);
        var query = uri.Query;

        foreach (var param in sensitiveParams)
        {
            query = System.Text.RegularExpressions.Regex.Replace(
                query,
                $@"[?&]{param}=[^&]*",
                $"?{param}=***",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        return uri.Scheme + "://" + uri.Host + uri.AbsolutePath + query;
    }
}

public sealed class OpenApiSourceResult
{
    public bool IsSuccess { get; private set; }
    public string? Content { get; private set; }
    public string? SafeUrl { get; private set; }
    public OpenApiSourceFailureReason FailureReason { get; private set; }
    public string? FailureMessage { get; private set; }

    public bool Success => IsSuccess;

    public static OpenApiSourceResult SuccessResult(string content, string safeUrl) =>
        new() { IsSuccess = true, Content = content, SafeUrl = safeUrl };

    public static OpenApiSourceResult NotConfigured() =>
        new() { FailureReason = OpenApiSourceFailureReason.NotConfigured };

    public static OpenApiSourceResult InvalidUrl(string message) =>
        new() { FailureReason = OpenApiSourceFailureReason.InvalidUrl, FailureMessage = message };

    public static OpenApiSourceResult BlockedByPolicy(string message) =>
        new() { FailureReason = OpenApiSourceFailureReason.BlockedByPolicy, FailureMessage = message };

    public static OpenApiSourceResult FetchFailed(string message) =>
        new() { FailureReason = OpenApiSourceFailureReason.FetchFailed, FailureMessage = message };

    public static OpenApiSourceResult SourceTooLarge(string message) =>
        new() { FailureReason = OpenApiSourceFailureReason.SourceTooLarge, FailureMessage = message };

    public static OpenApiSourceResult ParseFailed(string message) =>
        new() { FailureReason = OpenApiSourceFailureReason.ParseFailed, FailureMessage = message };

    public static OpenApiSourceResult UnsupportedFormat(string message) =>
        new() { FailureReason = OpenApiSourceFailureReason.UnsupportedFormat, FailureMessage = message };
}

public enum OpenApiSourceFailureReason
{
    NotConfigured,
    InvalidUrl,
    BlockedByPolicy,
    FetchFailed,
    SourceTooLarge,
    ParseFailed,
    UnsupportedFormat,
    ExternalRefNotAllowed
}
