using System.Text.RegularExpressions;
using System.Web;
using BirkNext.Api.Models;
using BirkNext.Api.Services.FrontendBrowserRuntime;

namespace BirkNext.Api.Services.TargetEnvironmentDetection;

/// <summary>
/// Backend service for target environment configuration detection.
/// Safely inspects target URLs to extract authentication metadata.
/// All requests pass through SSRF validation via BrowserTargetValidator.
/// Response contains only safe, non-sensitive metadata.
/// </summary>
public interface ITargetEnvironmentDetectionService
{
    Task<TargetEnvironmentDetectionResponse> DetectFromUrlAsync(string targetUrl, CancellationToken cancellationToken = default);
}

public sealed class TargetEnvironmentDetectionService : ITargetEnvironmentDetectionService
{
    private readonly BrowserTargetValidator _validator;
    private readonly HttpClient _httpClient;
    private readonly ITargetHostResolver _resolver;
    private readonly ILogger<TargetEnvironmentDetectionService> _logger;

    private static readonly HashSet<string> ApprovedEntraHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "login.microsoftonline.com",
        "login.microsoft.com",
        "login.windows.net"
    };

    private const int MaxRedirectCount = 5;
    private const int TimeoutSeconds = 10;

    public TargetEnvironmentDetectionService(
        BrowserTargetValidator validator,
        HttpClient httpClient,
        ITargetHostResolver resolver,
        ILogger<TargetEnvironmentDetectionService> logger)
    {
        _validator = validator;
        _httpClient = httpClient;
        _resolver = resolver;
        _logger = logger;
    }

    public async Task<TargetEnvironmentDetectionResponse> DetectFromUrlAsync(
        string targetUrl,
        CancellationToken cancellationToken = default)
    {
        // Validate URL format first
        if (string.IsNullOrWhiteSpace(targetUrl))
            return ErrorResponse(targetUrl, "URL is empty", "EMPTY_URL");

        try
        {
            if (!Uri.TryCreate(targetUrl, UriKind.Absolute, out var uri))
                return ErrorResponse(targetUrl, "Invalid URL format", "INVALID_URL");

            // SSRF validation: reject unsupported schemes, metadata endpoints, etc
            var validation = _validator.ValidateTarget(uri.AbsoluteUri, "Public");
            if (!validation.IsValid)
                return ErrorResponse(targetUrl, $"Target blocked: {validation.BlockReason}", "TARGET_BLOCKED");

            // Perform preflight check with redirect following
            var preflightResult = await CheckTargetWithRedirectAsync(uri, cancellationToken);

            // If preflight check failed (e.g., hostname validation blocked), return error with correct reachability
            if (!preflightResult.Success)
            {
                var sanitizedUrl = GetSanitizedUrlForResponse(targetUrl);
                return new TargetEnvironmentDetectionResponse
                {
                    OriginalUrl = sanitizedUrl,
                    Success = false,
                    Reachability = preflightResult.Reachability,
                    Message = $"Target validation failed: {preflightResult.BlockReason}",
                    ErrorCode = "TARGET_BLOCKED",
                    Confidence = DetectionConfidence.Low
                };
            }

            // NormalizedTargetUrl represents the normalized application target (user's input),
            // not the authentication redirect. Extract only scheme + host + path from original URI.
            var normalizedUrl = GetNormalizedApplicationTarget(uri);

            var result = new TargetEnvironmentDetectionResponse
            {
                OriginalUrl = GetNormalizedApplicationTarget(uri),
                NormalizedTargetUrl = normalizedUrl,
                Success = true,
                Message = "Detection completed successfully"
            };

            result.Reachability = preflightResult.Reachability;
            result.AuthenticationRequired = preflightResult.AuthenticationRequired;
            result.RedirectCount = preflightResult.RedirectCount;

            // If authentication is required, extract metadata from final URL
            if (result.AuthenticationRequired && !string.IsNullOrWhiteSpace(preflightResult.FinalUrl))
            {
                await ExtractAuthenticationMetadataAsync(preflightResult.FinalUrl, result, cancellationToken);
            }

            // Suggest environment and profile name from hostname
            SuggestEnvironmentType(uri.Host, result);
            result.SuggestedProfileName = SuggestProfileName(uri.Host);

            result.Confidence = CalculateConfidence(result);

            return result;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "HTTP error during target detection for {Url}", targetUrl);
            return ErrorResponse(targetUrl, "Network error", "NETWORK_ERROR");
        }
        catch (TaskCanceledException)
        {
            return ErrorResponse(targetUrl, "Detection timeout exceeded", "TIMEOUT");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during target detection for {Url}", targetUrl);
            return ErrorResponse(targetUrl, "Detection failed", "INTERNAL_ERROR");
        }
    }

    private async Task<PreflightCheckResult> CheckTargetWithRedirectAsync(
        Uri targetUri,
        CancellationToken cancellationToken)
    {
        var result = new PreflightCheckResult
        {
            FinalUrl = targetUri.AbsoluteUri,
            Reachability = TargetReachability.Unknown,
            AuthenticationRequired = false,
            RedirectCount = 0
        };

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(TimeoutSeconds));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, cts.Token);

            // Validate initial target hostname addresses before any HTTP request
            var initialValidation = await ValidateHostAddressesAsync(targetUri.Host, "Public", linkedCts.Token);
            if (!initialValidation.IsValid)
            {
                _logger.LogWarning("Initial target {Url} hostname {Host} validation failed: {Reason}",
                    targetUri.AbsoluteUri, targetUri.Host, initialValidation.BlockReason);
                result.Success = false;
                result.BlockReason = initialValidation.BlockReason;
                result.Reachability = TargetReachability.UntrustedRedirect;
                return result;
            }

            // Use HEAD request to avoid downloading full content
            var requestUri = targetUri;
            var redirectCount = 0;

            while (redirectCount < MaxRedirectCount)
            {
                using var request = new HttpRequestMessage(HttpMethod.Head, requestUri);
                request.Headers.Add("User-Agent", "BirkNext/1.0");

                var response = await _httpClient.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, linkedCts.Token);

                var statusCode = (int)response.StatusCode;

                // Check for HTTP redirect (3xx status codes)
                // AllowAutoRedirect is false, so we handle redirects manually here
                if (statusCode >= 300 && statusCode < 400)
                {
                    redirectCount++;
                    result.RedirectCount = redirectCount;

                    // Check if we've exceeded maximum redirects
                    if (redirectCount >= MaxRedirectCount)
                    {
                        _logger.LogWarning("Redirect limit exceeded ({Count} >= {Max})", redirectCount, MaxRedirectCount);
                        result.Reachability = TargetReachability.TooManyRedirects;
                        return result;
                    }

                    if (response.Headers.Location == null)
                    {
                        _logger.LogWarning("Redirect response from {Url} missing Location header", requestUri.AbsoluteUri);
                        result.Reachability = TargetReachability.UntrustedRedirect;
                        return result;
                    }

                    var locationStr = response.Headers.Location.IsAbsoluteUri
                        ? response.Headers.Location.AbsoluteUri
                        : new Uri(requestUri, response.Headers.Location).AbsoluteUri;

                    // Validate redirect target URL structure BEFORE DNS/address validation
                    var redirectValidation = _validator.ValidateRedirectTarget(
                        locationStr, requestUri.Host, "Public");

                    if (!redirectValidation.IsValid)
                    {
                        _logger.LogWarning("Redirect to {RedirectUrl} blocked: {Reason}",
                            locationStr, redirectValidation.BlockReason);
                        result.Reachability = TargetReachability.UntrustedRedirect;
                        return result;
                    }

                    if (!Uri.TryCreate(locationStr, UriKind.Absolute, out var redirectUri))
                    {
                        _logger.LogWarning("Invalid redirect URI from {Url}: {Location}", requestUri.AbsoluteUri, locationStr);
                        result.Reachability = TargetReachability.UntrustedRedirect;
                        return result;
                    }

                    // Validate redirect target hostname addresses BEFORE following the redirect
                    var redirectAddressValidation = await ValidateHostAddressesAsync(redirectUri.Host, "Public", linkedCts.Token);
                    if (!redirectAddressValidation.IsValid)
                    {
                        _logger.LogWarning("Redirect target {Url} hostname {Host} validation failed: {Reason}",
                            locationStr, redirectUri.Host, redirectAddressValidation.BlockReason);
                        result.Success = false;
                        result.BlockReason = redirectAddressValidation.BlockReason;
                        result.Reachability = TargetReachability.UntrustedRedirect;
                        return result;
                    }

                    result.FinalUrl = locationStr;
                    requestUri = redirectUri;
                    continue; // Follow the validated redirect
                }

                // Non-redirect response
                result.FinalUrl = requestUri.AbsoluteUri;

                // Handle response status
                if (!response.IsSuccessStatusCode)
                {
                    if (statusCode == 401 || statusCode == 403)
                    {
                        result.AuthenticationRequired = true;
                        result.Reachability = TargetReachability.AuthenticationRequired;
                    }
                    else if (statusCode >= 400 && statusCode < 500)
                    {
                        result.Reachability = TargetReachability.Unreachable;
                    }
                    else if (statusCode >= 500)
                    {
                        result.Reachability = TargetReachability.Reachable; // Server error but reachable
                    }
                }
                else
                {
                    result.Reachability = TargetReachability.Reachable;
                }

                // Check if this looks like a login page (even if 200)
                if (IsLikelyLoginPage(result.FinalUrl))
                {
                    result.AuthenticationRequired = true;
                    result.Reachability = TargetReachability.AuthenticationRequired;
                }

                break; // Exit loop after receiving non-redirect response
            }

            if (redirectCount >= MaxRedirectCount)
            {
                result.Reachability = TargetReachability.TooManyRedirects;
            }

            return result;
        }
        catch (TaskCanceledException)
        {
            result.Reachability = TargetReachability.Timeout;
            return result;
        }
    }

    private async Task ExtractAuthenticationMetadataAsync(
        string finalUrl,
        TargetEnvironmentDetectionResponse result,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!Uri.TryCreate(finalUrl, UriKind.Absolute, out var finalUri))
                return;

            var host = finalUri.Host;

            if (ApprovedEntraHosts.Contains(host))
            {
                result.DetectedAuthenticationType = FrontendAuthenticationType.MicrosoftEntraId;
                result.DetectedAuthority = $"{finalUri.Scheme}://{host}";

                // Extract tenant and client from URL
                var query = HttpUtility.ParseQueryString(finalUri.Query);
                var tenantFromPath = ExtractTenantFromPath(finalUri.AbsolutePath);

                if (!string.IsNullOrEmpty(tenantFromPath))
                {
                    if (IsConcreteTenanId(tenantFromPath))
                        result.DetectedTenantId = tenantFromPath;
                    else
                        result.TenantMode = tenantFromPath;
                }

                var clientId = query["client_id"];
                if (!string.IsNullOrEmpty(clientId))
                    result.DetectedClientId = clientId;

                result.Confidence = DetectionConfidence.VeryHigh;
            }
            else if (host.Contains("oauth", StringComparison.OrdinalIgnoreCase) ||
                     host.Contains("auth", StringComparison.OrdinalIgnoreCase))
            {
                result.DetectedAuthenticationType = FrontendAuthenticationType.OpenIdConnect;
                result.DetectedAuthority = $"{finalUri.Scheme}://{host}";
                result.Confidence = DetectionConfidence.High;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error extracting auth metadata from {Url}", finalUrl);
            result.Warnings.Add("Could not extract full authentication metadata");
        }
    }

    private string? ExtractTenantFromPath(string path)
    {
        var match = Regex.Match(path, @"^/([^/]+)/(?:oauth2|openid)", RegexOptions.IgnoreCase);
        return match.Success && match.Groups.Count > 1 ? match.Groups[1].Value : null;
    }

    private bool IsConcreteTenanId(string value)
    {
        if (Guid.TryParse(value, out _))
            return true;

        return !new[] { "common", "organizations", "consumers" }
            .Contains(value, StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsLikelyLoginPage(string url)
    {
        return url.Contains("login", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("signin", StringComparison.OrdinalIgnoreCase);
    }

    private void SuggestEnvironmentType(string hostname, TargetEnvironmentDetectionResponse result)
    {
        var lower = hostname.ToLowerInvariant();

        if (lower.Contains("prod") || lower.Contains("production"))
            result.SuggestedEnvironmentType = FrontendEnvironmentType.Production;
        else if (lower.Contains("dev") || lower.Contains("development"))
            result.SuggestedEnvironmentType = FrontendEnvironmentType.Development;
        else if (lower.Contains("qa") || lower.Contains("test"))
            result.SuggestedEnvironmentType = FrontendEnvironmentType.QA;
        else if (lower.Contains("rc") || lower.Contains("staging"))
            result.SuggestedEnvironmentType = FrontendEnvironmentType.RC;
        else if (lower.Contains("local") || lower.Contains("localhost"))
            result.SuggestedEnvironmentType = FrontendEnvironmentType.Local;
    }

    private string? SuggestProfileName(string hostname)
    {
        var parts = hostname.Split('.');
        if (parts.Length == 0)
            return null;

        var mainPart = parts[0];
        if (mainPart.Length < 2)
            return null;

        var formatted = Regex.Replace(mainPart, @"([a-z])([A-Z])", "$1 $2", RegexOptions.IgnoreCase);
        formatted = Regex.Replace(formatted, @"([a-zA-Z])(\d)", "$1 $2", RegexOptions.IgnoreCase);

        return formatted.ToUpperInvariant().Trim();
    }

    private DetectionConfidence CalculateConfidence(TargetEnvironmentDetectionResponse result)
    {
        var score = 0;

        if (result.Reachability == TargetReachability.Reachable)
            score += 2;
        else if (result.Reachability == TargetReachability.AuthenticationRequired)
            score += 1;

        if (result.AuthenticationRequired && result.DetectedAuthenticationType != FrontendAuthenticationType.None)
            score += 1;

        if (!string.IsNullOrEmpty(result.DetectedTenantId))
            score += 1;

        if (!string.IsNullOrEmpty(result.DetectedClientId))
            score += 1;

        if (result.SuggestedEnvironmentType.HasValue)
            score += 1;

        return score switch
        {
            >= 4 => DetectionConfidence.VeryHigh,
            >= 3 => DetectionConfidence.High,
            >= 2 => DetectionConfidence.Medium,
            _ => DetectionConfidence.Low
        };
    }

    private TargetEnvironmentDetectionResponse ErrorResponse(string originalUrl, string message, string errorCode)
    {
        _logger.LogWarning("Detection failed for {Url}: {Message} ({ErrorCode})", originalUrl, message, errorCode);

        var sanitizedUrl = GetSanitizedUrlForResponse(originalUrl);
        return new TargetEnvironmentDetectionResponse
        {
            OriginalUrl = sanitizedUrl,
            Success = false,
            Message = message,
            ErrorCode = errorCode,
            Confidence = DetectionConfidence.Low
        };
    }

    private string GetNormalizedApplicationTarget(Uri applicationUri)
    {
        // Return the normalized application target (user-provided URI).
        // Never return authentication redirect URLs with query parameters.
        // Format: scheme://host/path (excludes query and fragment)
        var pathWithoutQuery = applicationUri.AbsolutePath == "/" ? "" : applicationUri.AbsolutePath;
        return $"{applicationUri.Scheme}://{applicationUri.Host}{pathWithoutQuery}";
    }

    private string GetSanitizedUrlForResponse(string urlString)
    {
        // Sanitize URL by removing query parameters and fragments.
        // Prevents accidental leakage of sensitive data (auth codes, tokens, state, etc.)
        if (!Uri.TryCreate(urlString, UriKind.Absolute, out var uri))
            return urlString;

        return GetNormalizedApplicationTarget(uri);
    }

    /// <summary>
    /// Validates that a hostname (if it requires resolution) contains no blocked addresses.
    /// If host is a literal IP, validates directly.
    /// If host is a hostname, resolves and validates all addresses.
    /// Fails closed if any resolved address is blocked.
    /// </summary>
    private async Task<BrowserTargetValidator.ValidationResult> ValidateHostAddressesAsync(
        string host,
        string environmentType,
        CancellationToken cancellationToken)
    {
        // If hostname is empty or the host is already a known valid address format
        if (string.IsNullOrWhiteSpace(host))
            return new BrowserTargetValidator.ValidationResult(false, "Hostname is empty");

        // Try to parse as literal IP first
        if (System.Net.IPAddress.TryParse(host, out var literalAddress))
        {
            // Literal IP - validate directly
            return _validator.ValidateResolvedAddress(literalAddress.ToString(), environmentType);
        }

        // Hostname requires DNS resolution
        var addresses = await _resolver.ResolveHostAsync(host, cancellationToken);

        // No addresses resolved
        if (addresses.Count == 0)
        {
            _logger.LogWarning("Hostname {Host} could not be resolved or returned no addresses", host);
            return new BrowserTargetValidator.ValidationResult(false, "Hostname resolution failed or returned no addresses");
        }

        // Validate ALL resolved addresses
        // Fail closed: if ANY address is blocked, reject the whole hostname
        foreach (var address in addresses)
        {
            var addressValidation = _validator.ValidateResolvedAddress(address.ToString(), environmentType);
            if (!addressValidation.IsValid)
            {
                _logger.LogWarning("Hostname {Host} resolved to blocked address {Address}: {Reason}",
                    host, address, addressValidation.BlockReason);
                return addressValidation;
            }
        }

        // All addresses passed validation
        return new BrowserTargetValidator.ValidationResult(true);
    }

    private sealed class PreflightCheckResult
    {
        public string FinalUrl { get; set; } = "";
        public bool AuthenticationRequired { get; set; }
        public TargetReachability Reachability { get; set; } = TargetReachability.Unknown;
        public int RedirectCount { get; set; }
        public bool Success { get; set; } = true;
        public string? BlockReason { get; set; }
    }
}
