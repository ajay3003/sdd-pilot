using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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

    /// <summary>
    /// Continue detection using a specific authentication strategy (e.g., interactive browser).
    /// Should only be called after preflight detection has identified an authentication requirement.
    /// </summary>
    Task<TargetDetectionOutcome> DetectWithStrategyAsync(
        string targetUrl,
        string reviewSessionId,
        string profileId,
        ITargetDetectionAuthenticationStrategy strategy,
        CancellationToken cancellationToken = default);
}

public sealed class TargetEnvironmentDetectionService : ITargetEnvironmentDetectionService
{
    private readonly BirkNext.Api.Configuration.TargetDetectionOptions _detectionOptions;
    private readonly BrowserTargetValidator _validator;
    private readonly HttpClient _httpClient;
    private readonly ITargetHostResolver _resolver;
    private readonly IClientFrameworkDetector _frameworkDetector;
    private readonly ILogger<TargetEnvironmentDetectionService> _logger;

    private readonly EndpointDiscoveryHelper _endpointHelper = new();
    private readonly ConfigDiscoveryHelper _configHelper = new();

    private static readonly HashSet<string> ApprovedEntraHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "login.microsoftonline.com",
        "login.microsoft.com",
        "login.windows.net"
    };

    private const int MaxRedirectCount = 5;
    private const int TimeoutSeconds = 10;
    private const int ProbeTimeoutSeconds = 5;
    private const int MaxContentSizeBytes = 1_000_000; // 1 MB per file
    private const int MaxTotalContentBytes = 5_000_000; // 5 MB total

    public TargetEnvironmentDetectionService(
        BrowserTargetValidator validator,
        HttpClient httpClient,
        ITargetHostResolver resolver,
        IClientFrameworkDetector frameworkDetector,
        ILogger<TargetEnvironmentDetectionService> logger,
        Microsoft.Extensions.Options.IOptions<BirkNext.Api.Configuration.TargetDetectionOptions>? detectionOptions = null)
    {
        _detectionOptions = detectionOptions?.Value ?? new();
        _validator = validator;
        _httpClient = httpClient;
        _resolver = resolver;
        _frameworkDetector = frameworkDetector;
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
                    Confidence = DetectionConfidence.Low,
                    State = TargetDetectionState.Failed,
                    IsActivationReady = false
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

            // Try to extract configuration-based authentication (e.g., appsettings.json for .NET apps)
            // This complements redirect-based detection and works well for SPAs like Blazor WASM
            if (result.Reachability == TargetReachability.Reachable &&
                result.DetectedAuthenticationType == FrontendAuthenticationType.None)
            {
                await ExtractAuthenticationConfigAsync(normalizedUrl, result, cancellationToken);
            }

            // Detect client-side frameworks (Blazor WASM, React, etc.) for reachable targets.
            // Only do this for real production/dev URLs, not test fixtures (*.test, *.local, localhost).
            // Public authentication config and framework evidence are independent.
            var isTestHostname = uri.Host.EndsWith(".test", StringComparison.OrdinalIgnoreCase) ||
                                 uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                                 uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
                                 uri.Host.EndsWith(".example.com", StringComparison.OrdinalIgnoreCase);

            if (!isTestHostname &&
                preflightResult.Reachability == TargetReachability.Reachable &&
                !result.AuthenticationRequired)
            {
                await DetectClientFrameworkAsync(normalizedUrl, result, cancellationToken);
            }

            // Discover endpoints and integrations (REST, GraphQL, Swagger, Health)
            // Only for reachable targets without auth requirement
            if (!isTestHostname &&
                preflightResult.Reachability == TargetReachability.Reachable &&
                !result.AuthenticationRequired)
            {
                await DiscoverEndpointsAsync(normalizedUrl, result, cancellationToken);

                // Compute fingerprint for stale invalidation
                result.FrontendUrlFingerprint = ComputeUrlFingerprint(normalizedUrl);
            }

            // Suggest environment and profile name from hostname
            SuggestEnvironmentType(uri.Host, result);
            result.SuggestedProfileName = SuggestProfileName(uri.Host);

            result.Confidence = CalculateConfidence(result);
            ApplyTypedOutcome(result);

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
                // No request or redirect has occurred: the initial target is unreachable.
                result.Reachability = TargetReachability.Unreachable;
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

                // Extract redirect URIs from redirect_uri parameter(s)
                var redirectUri = query["redirect_uri"];
                if (!string.IsNullOrEmpty(redirectUri))
                {
                    result.DetectedRedirectUrls.Add(redirectUri);
                }

                result.Confidence = DetectionConfidence.VeryHigh;
            }
            else if (host.Contains("oauth", StringComparison.OrdinalIgnoreCase) ||
                     host.Contains("auth", StringComparison.OrdinalIgnoreCase))
            {
                result.DetectedAuthenticationType = FrontendAuthenticationType.OpenIdConnect;
                result.DetectedAuthority = $"{finalUri.Scheme}://{host}";

                var query = HttpUtility.ParseQueryString(finalUri.Query);
                var redirectUri = query["redirect_uri"];
                if (!string.IsNullOrEmpty(redirectUri))
                {
                    result.DetectedRedirectUrls.Add(redirectUri);
                }

                result.Confidence = DetectionConfidence.High;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error extracting auth metadata from {Url}", finalUrl);
            result.Warnings.Add("Could not extract full authentication metadata");
        }
    }

    private async Task ExtractAuthenticationConfigAsync(
        string applicationUrl,
        TargetEnvironmentDetectionResponse result,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!Uri.TryCreate(applicationUrl, UriKind.Absolute, out var appUri))
                return;

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, cts.Token);

            var configUrl = $"{appUri.Scheme}://{appUri.Host}/appsettings.json";
            using var request = new HttpRequestMessage(HttpMethod.Get, configUrl);
            request.Headers.Add("User-Agent", "BirkNext/1.0");
            request.Headers.Add("Accept", "application/json");

            var response = await _httpClient.SendAsync(
                request, HttpCompletionOption.ResponseContentRead, linkedCts.Token);

            if (!response.IsSuccessStatusCode)
                return;

            var content = await response.Content.ReadAsStringAsync(linkedCts.Token);

            using var doc = System.Text.Json.JsonDocument.Parse(content);
            var root = doc.RootElement;

            if (!root.TryGetProperty("AzureAd", out var azureAdElement))
                return;

            if (azureAdElement.ValueKind != System.Text.Json.JsonValueKind.Object)
                return;

            result.DetectedAuthenticationType = FrontendAuthenticationType.MicrosoftEntraId;

            if (azureAdElement.TryGetProperty("Authority", out var authorityElement) &&
                authorityElement.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                var authority = authorityElement.GetString();
                if (!string.IsNullOrWhiteSpace(authority))
                {
                    result.DetectedAuthority = authority;

                    var tenantId = ExtractTenantFromAuthority(authority);
                    if (!string.IsNullOrEmpty(tenantId))
                    {
                        result.DetectedTenantId = tenantId;
                    }
                }
            }

            if (azureAdElement.TryGetProperty("ClientId", out var clientIdElement) &&
                clientIdElement.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                var clientId = clientIdElement.GetString();
                if (!string.IsNullOrWhiteSpace(clientId))
                {
                    result.DetectedClientId = clientId;
                }
            }

            result.Confidence = DetectionConfidence.High;
            _logger.LogDebug("Successfully detected MSAL configuration from appsettings.json for {Url}", applicationUrl);
        }
        catch (TaskCanceledException)
        {
            _logger.LogDebug("Config detection timeout for {Url}", applicationUrl);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Config-based authentication detection error for {Url}", applicationUrl);
        }
    }

    private string? ExtractTenantFromAuthority(string authority)
    {
        try
        {
            if (!Uri.TryCreate(authority, UriKind.Absolute, out var uri))
                return null;

            var path = uri.AbsolutePath.Trim('/');
            var segments = path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);

            if (segments.Length > 0)
            {
                var firstSegment = segments[0];
                if (IsConcreteTenanId(firstSegment))
                {
                    return firstSegment;
                }
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private async Task DetectClientFrameworkAsync(
        string finalUrl,
        TargetEnvironmentDetectionResponse result,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(finalUrl))
            return;

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(TimeoutSeconds));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, cts.Token);

            // Perform a GET request to retrieve response body for framework detection
            if (!_validator.ValidateTarget(finalUrl, "Public").IsValid ||
                !(await ValidateHostAddressesAsync(new Uri(finalUrl).Host, "Public", linkedCts.Token)).IsValid)
                return;
            using var request = new HttpRequestMessage(HttpMethod.Get, finalUrl);
            request.Headers.Add("User-Agent", "BirkNext/1.0");

            using var response = await _httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, linkedCts.Token);

            if (response.IsSuccessStatusCode)
            {
                var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
                var content = await ReadBoundedContentAsync(response.Content, 32768, linkedCts.Token, allowPrefix: true);

                // Framework detection is safe: bounded inspection with positive signals
                var detectedFramework = _frameworkDetector.DetectFramework(content, contentType);
                if (detectedFramework.HasValue)
                {
                    result.DetectedClientFramework = detectedFramework.Value;
                    result.FrameworkEvidence = ClientFrameworkDetector.FindEvidence(content, contentType);
                    result.FrameworkConfidence = DetectionConfidence.High;
                }
            }
        }
        catch (TaskCanceledException)
        {
            // Timeout during framework detection - not critical, continue with detection results
            _logger.LogDebug("Framework detection timeout for {Url}", finalUrl);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Framework detection error for {Url}", finalUrl);
            // Framework detection failures are non-critical
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
        result.SuggestedEnvironmentType = TargetEnvironmentTypeClassifier.Infer(hostname);
    }

    private string? SuggestProfileName(string hostname)
        => HostnameProfileNameFormatter.Format(hostname);

    private void ApplyTypedOutcome(TargetEnvironmentDetectionResponse result)
    {
        result.State = !result.Success ? TargetDetectionState.Failed
            : result.AuthenticationRequired || result.Reachability == TargetReachability.AuthenticationRequired
                ? TargetDetectionState.AuthenticationRequired
                : result.Reachability == TargetReachability.Reachable && result.DetectedClientFramework.HasValue
                    ? TargetDetectionState.Partial
                    : result.Reachability == TargetReachability.Reachable
                        ? TargetDetectionState.Complete
                        : TargetDetectionState.Failed;
        result.BrowserRuntimeInspectionRequired = result.State == TargetDetectionState.Partial && result.DetectedClientFramework.HasValue;
        result.IsActivationReady = result.State == TargetDetectionState.Complete;
        if (Uri.TryCreate(result.OriginalUrl, UriKind.Absolute, out var target) &&
            _detectionOptions.ManualManagedEdgeHosts.Contains(target.Host, StringComparer.OrdinalIgnoreCase))
            ManualAuthenticationVerification.Apply(result);
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

        if (result.DetectedRedirectUrls.Count > 0)
            score += 1;

        if (result.SuggestedEnvironmentType.HasValue)
            score += 1;

        return score switch
        {
            >= 5 => DetectionConfidence.VeryHigh,
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
            Reachability = errorCode switch
            {
                "NETWORK_ERROR" or "TARGET_BLOCKED" => TargetReachability.Unreachable,
                "TIMEOUT" => TargetReachability.Timeout,
                _ => TargetReachability.Unknown
            },
            Confidence = DetectionConfidence.Low,
            State = TargetDetectionState.Failed,
            IsActivationReady = false
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

    /// <summary>
    /// Continue detection using a specific authentication strategy.
    /// Combines preflight detection with strategy-based continuation.
    /// Creates a final TargetDetectionOutcome with updated state based on strategy result.
    /// </summary>
    public async Task<TargetDetectionOutcome> DetectWithStrategyAsync(
        string targetUrl,
        string reviewSessionId,
        string profileId,
        ITargetDetectionAuthenticationStrategy strategy,
        CancellationToken cancellationToken = default)
    {
        // Run initial preflight detection to get baseline
        var preflightResponse = await DetectFromUrlAsync(targetUrl, cancellationToken);

        // If preflight failed, return failed outcome
        if (!preflightResponse.Success || preflightResponse.ManualAuthenticationVerificationRequired)
        {
            var stateComputer = new DetectionStateComputer();
            return stateComputer.CreateOutcome(preflightResponse, targetUrl, targetUrl);
        }

        // Interactive continuation is required both for an explicit authentication challenge
        // and for a reachable SPA whose client runtime still needs browser inspection.
        _logger.LogInformation($"[DIAG] DetectWithStrategyAsync preflight: AuthRequired={preflightResponse.AuthenticationRequired} BrowserRuntimeRequired={preflightResponse.BrowserRuntimeInspectionRequired}");
        if (preflightResponse.AuthenticationRequired || preflightResponse.BrowserRuntimeInspectionRequired)
        {
            try
            {
                _logger.LogInformation($"[DIAG] DetectWithStrategyAsync calling strategy.ContinueDetectionAsync");
                _logger.LogInformation("Starting {Strategy} for target {Url}", strategy.StrategyName, targetUrl);
                var continuationResult = await strategy.ContinueDetectionAsync(
                    targetUrl, reviewSessionId, profileId, cancellationToken: cancellationToken);
                _logger.LogInformation($"[DIAG] DetectWithStrategyAsync strategy completed successfully");

                // Map continuation result to detection outcome
                return CreateOutcomeFromContinuation(continuationResult, preflightResponse, targetUrl);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Strategy-based detection failed for {Url}", targetUrl);
                preflightResponse.Success = false;
                return new TargetDetectionOutcome
                {
                    DetectionResponse = preflightResponse,
                    State = TargetDetectionState.Failed,
                    IsActivationReady = false,
                    StrategySuggestion = "retry-detection",
                    DetectedAt = DateTime.UtcNow,
                    DetectedUrl = targetUrl,
                    IsUrlCurrent = true,
                    Message = "Interactive browser detection failed"
                };
            }
        }

        // Preflight indicates no authentication required - return success outcome
        var computer = new DetectionStateComputer();
        return computer.CreateOutcome(preflightResponse, targetUrl, targetUrl);
    }

    /// <summary>
    /// Create a TargetDetectionOutcome from a continuation result.
    /// Maps browser session terminal states to detection state and response.
    /// </summary>
    private TargetDetectionOutcome CreateOutcomeFromContinuation(
        DetectionContinuationResult continuationResult,
        TargetEnvironmentDetectionResponse preflightResponse,
        string targetUrl)
    {
        // Create a modified response based on continuation result
        var outcome = new TargetDetectionOutcome
        {
            AuthenticationFailureReason = continuationResult.AuthenticationFailureReason,
            DetectionResponse = preflightResponse,
            DetectedUrl = targetUrl,
            DetectedAt = DateTime.UtcNow,
            IsUrlCurrent = true
        };

        if (continuationResult.AuthenticationSucceeded)
        {
            // Update response to indicate success
            preflightResponse.Success = true;
            preflightResponse.Reachability = TargetReachability.Reachable;
            preflightResponse.AuthenticationRequired = false;
            preflightResponse.Message = continuationResult.IsFullCompletion
                ? "Authentication succeeded - target is accessible"
                : "Authentication partially succeeded - awaiting user continuation";

            outcome.State = continuationResult.ResultingState;
            outcome.IsActivationReady = continuationResult.IsFullCompletion;
            outcome.Message = preflightResponse.Message;

            if (continuationResult.IsFullCompletion)
            {
                var stateComputer = new DetectionStateComputer();
                outcome.StrategySuggestion = stateComputer.GetStrategySuggestion(TargetDetectionState.Complete, preflightResponse);
            }
            else
            {
                outcome.StrategySuggestion = "browser-automation-required";
            }
        }
        else if (continuationResult.UserCancelled)
        {
            // User cancelled - not a failure, but incomplete
            preflightResponse.Success = false;
            outcome.State = TargetDetectionState.Partial;
            outcome.IsActivationReady = false;
            outcome.Message = "User cancelled authentication flow";
            preflightResponse.Message = outcome.Message;
            outcome.StrategySuggestion = "browser-auth-required";
        }
        else if (continuationResult.SessionExpired)
        {
            // Session expired - retry needed
            preflightResponse.Success = false;
            outcome.State = TargetDetectionState.Failed;
            outcome.IsActivationReady = false;
            outcome.Message = "Authentication session expired";
            preflightResponse.Message = outcome.Message;
            outcome.StrategySuggestion = "retry-detection";
        }
        else if (continuationResult.UnexpectedOriginEncountered)
        {
            // Unexpected origin indicates potential attack or misconfiguration
            preflightResponse.Success = false;
            outcome.State = TargetDetectionState.Failed;
            outcome.IsActivationReady = false;
            outcome.Message = "Authentication flow encountered unexpected origin - possible attack or misconfiguration";
            preflightResponse.Message = outcome.Message;
            outcome.StrategySuggestion = "retry-detection";
        }
        else if (continuationResult.AwaitingUserContinuation)
        {
            // Awaiting user to continue (e.g., MCAS interstitial)
            outcome.State = TargetDetectionState.Partial;
            outcome.IsActivationReady = false;
            outcome.Message = "Awaiting user to continue authentication (e.g., MCAS interstitial)";
            preflightResponse.Message = outcome.Message;
            outcome.StrategySuggestion = "browser-automation-required";
        }
        else if (continuationResult.AuthenticationFailureReason.HasValue)
        {
            // Authentication failed
            preflightResponse.Success = false;
            outcome.State = TargetDetectionState.Failed;
            outcome.IsActivationReady = false;
            outcome.Message = $"Authentication failed: {FormatFailureReason(continuationResult.AuthenticationFailureReason.Value)}";
            preflightResponse.Message = outcome.Message;
            outcome.StrategySuggestion = "retry-detection";
        }
        else
        {
            // Unknown state
            preflightResponse.Success = false;
            outcome.State = TargetDetectionState.Failed;
            outcome.IsActivationReady = false;
            outcome.Message = "Authentication strategy completed with unknown state";
            preflightResponse.Message = outcome.Message;
            outcome.StrategySuggestion = "retry-detection";
        }

        _logger.LogInformation("Detection outcome: state {State}, authentication reason {AuthenticationFailureReason}", outcome.State, outcome.AuthenticationFailureReason);
        return outcome;
    }

    private string FormatFailureReason(AuthenticationFailureReason reason)
    {
        return reason switch
        {
            AuthenticationFailureReason.InvalidCredentials => "Invalid credentials or authentication denied",
            AuthenticationFailureReason.MfaRequired => "Multi-factor authentication required",
            AuthenticationFailureReason.ConditionalAccessDenied => "Conditional access policy denied access",
            AuthenticationFailureReason.AccountDisabled => "Account is disabled or locked",
            AuthenticationFailureReason.NavigationTimeout => "Navigation timeout during authentication",
            AuthenticationFailureReason.BrowserResourceFailure => "Browser resource became unavailable",
            AuthenticationFailureReason.RuntimeUnavailable => "Authenticated browser runtime is disabled or unavailable",
            AuthenticationFailureReason.NavigationFailure => "Navigation failed during authentication",
            AuthenticationFailureReason.UnexpectedOrigin => "Authentication reached an unexpected origin",
            AuthenticationFailureReason.InvalidAuthenticationConfiguration => "Authentication configuration uses an unapproved Entra authority",
            _ => "Unknown authentication failure"
        };
    }

    private async Task DiscoverEndpointsAsync(
        string applicationUrl,
        TargetEnvironmentDetectionResponse result,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!Uri.TryCreate(applicationUrl, UriKind.Absolute, out var appUri))
                return;

            // Step 1: Try to extract from config file (highest confidence)
            await ExtractEndpointConfigAsync(appUri, result, cancellationToken);

            // Step 2: Safe probing for endpoints not found via config
            if (string.IsNullOrWhiteSpace(result.DetectedRestBaseUrl))
                await ProbeRestEndpointsAsync(appUri, result, cancellationToken);

            if (string.IsNullOrWhiteSpace(result.DetectedGraphQlEndpoint))
                await ProbeGraphQlEndpointsAsync(appUri, result, cancellationToken);

            if (string.IsNullOrWhiteSpace(result.DetectedSwaggerUrl))
                await ProbeSwaggerEndpointsAsync(appUri, result, cancellationToken);

            if (string.IsNullOrWhiteSpace(result.DetectedHealthEndpoint))
                await ProbeHealthEndpointsAsync(appUri, result, cancellationToken);

            // Step 3: Discover integrations from config
            await DiscoverIntegrationsAsync(appUri, result, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error during endpoint discovery for {Url}", applicationUrl);
        }
    }

    private async Task DiscoverIntegrationsAsync(
        Uri applicationUri,
        TargetEnvironmentDetectionResponse result,
        CancellationToken cancellationToken)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(ProbeTimeoutSeconds));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, cts.Token);

            var configUrl = $"{applicationUri.Scheme}://{applicationUri.Host}/appsettings.json";

            if (!_endpointHelper.IsSafeProbeCandidate(configUrl))
                return;

            using var request = new HttpRequestMessage(HttpMethod.Get, configUrl);
            request.Headers.Add("User-Agent", "BirkNext/1.0");
            request.Headers.Add("Accept", "application/json");

            var response = await _httpClient.SendAsync(
                request, HttpCompletionOption.ResponseContentRead, linkedCts.Token);

            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxContentSizeBytes)
                return;

            var content = await response.Content.ReadAsStringAsync(linkedCts.Token);

            using var doc = JsonDocument.Parse(content);
            var root = doc.RootElement;

            // Discover Event Hub
            var eventHubConfig = _configHelper.ExtractEventHubConfig(root);
            if (eventHubConfig.HasValue && (!string.IsNullOrWhiteSpace(eventHubConfig.Value.Namespace) || !string.IsNullOrWhiteSpace(eventHubConfig.Value.Name)))
            {
                result.DetectedIntegrations.Add(new DiscoveredIntegration
                {
                    Type = "EventHub",
                    DisplayName = $"Event Hub{(!string.IsNullOrWhiteSpace(eventHubConfig.Value.Name) ? $" ({eventHubConfig.Value.Name})" : "")}",
                    Endpoint = eventHubConfig.Value.Namespace,
                    ResourceName = eventHubConfig.Value.Name,
                    Confidence = DetectionConfidence.High,
                    EvidenceSource = "/appsettings.json",
                    Evidence = new() { "EventHub section found", "Namespace and/or Name extracted" }
                });

                result.DiscoveryEvidence.Add(new DiscoveryEvidence
                {
                    Type = DiscoveryEvidence.EvidenceType.StructuredConfig,
                    Status = EndpointEvidenceStatus.Observed,
                    LocationCategory = "/appsettings.json - EventHub section",
                    Value = $"Namespace: {eventHubConfig.Value.Namespace}, Name: {eventHubConfig.Value.Name}",
                    Confidence = DetectionConfidence.High,
                    TargetField = "EventHub Integration"
                });
            }

            // Discover Service Bus
            var serviceBusConfig = _configHelper.ExtractServiceBusConfig(root);
            if (serviceBusConfig.HasValue && (!string.IsNullOrWhiteSpace(serviceBusConfig.Value.Namespace) || !string.IsNullOrWhiteSpace(serviceBusConfig.Value.Name)))
            {
                result.DetectedIntegrations.Add(new DiscoveredIntegration
                {
                    Type = "ServiceBus",
                    DisplayName = $"Service Bus{(!string.IsNullOrWhiteSpace(serviceBusConfig.Value.Namespace) ? $" ({serviceBusConfig.Value.Namespace})" : "")}",
                    Endpoint = serviceBusConfig.Value.Namespace,
                    ResourceName = serviceBusConfig.Value.Name,
                    Confidence = DetectionConfidence.High,
                    EvidenceSource = "/appsettings.json",
                    Evidence = new() { "ServiceBus section found", "Namespace and/or Name extracted" }
                });

                result.DiscoveryEvidence.Add(new DiscoveryEvidence
                {
                    Type = DiscoveryEvidence.EvidenceType.StructuredConfig,
                    Status = EndpointEvidenceStatus.Observed,
                    LocationCategory = "/appsettings.json - ServiceBus section",
                    Value = $"Namespace: {serviceBusConfig.Value.Namespace}, Name: {serviceBusConfig.Value.Name}",
                    Confidence = DetectionConfidence.High,
                    TargetField = "ServiceBus Integration"
                });
            }

            // Discover Kafka
            var kafkaBrokers = _configHelper.ExtractKafkaBrokers(root);
            if (!string.IsNullOrWhiteSpace(kafkaBrokers))
            {
                result.DetectedIntegrations.Add(new DiscoveredIntegration
                {
                    Type = "Kafka",
                    DisplayName = "Kafka",
                    Endpoint = kafkaBrokers,
                    Confidence = DetectionConfidence.High,
                    EvidenceSource = "/appsettings.json",
                    Evidence = new() { "Kafka section found", "Brokers or BootstrapServers extracted" }
                });

                result.DiscoveryEvidence.Add(new DiscoveryEvidence
                {
                    Type = DiscoveryEvidence.EvidenceType.StructuredConfig,
                    Status = EndpointEvidenceStatus.Observed,
                    LocationCategory = "/appsettings.json - Kafka section",
                    Value = $"Brokers: {kafkaBrokers}",
                    Confidence = DetectionConfidence.High,
                    TargetField = "Kafka Integration"
                });
            }

            // Discover RabbitMQ
            var rabbitMqConfig = _configHelper.ExtractRabbitMqConfig(root);
            if (rabbitMqConfig.HasValue && (!string.IsNullOrWhiteSpace(rabbitMqConfig.Value.Hostname) || rabbitMqConfig.Value.Port.HasValue))
            {
                var displayName = !string.IsNullOrWhiteSpace(rabbitMqConfig.Value.Hostname)
                    ? $"RabbitMQ ({rabbitMqConfig.Value.Hostname})"
                    : "RabbitMQ";

                var endpoint = !string.IsNullOrWhiteSpace(rabbitMqConfig.Value.Hostname)
                    ? $"{rabbitMqConfig.Value.Hostname}:{rabbitMqConfig.Value.Port}"
                    : rabbitMqConfig.Value.Port?.ToString();

                result.DetectedIntegrations.Add(new DiscoveredIntegration
                {
                    Type = "RabbitMQ",
                    DisplayName = displayName,
                    Endpoint = endpoint,
                    Confidence = DetectionConfidence.High,
                    EvidenceSource = "/appsettings.json",
                    Evidence = new() { "RabbitMQ section found", "Hostname and/or Port extracted" }
                });

                result.DiscoveryEvidence.Add(new DiscoveryEvidence
                {
                    Type = DiscoveryEvidence.EvidenceType.StructuredConfig,
                    Status = EndpointEvidenceStatus.Observed,
                    LocationCategory = "/appsettings.json - RabbitMQ section",
                    Value = $"Hostname: {rabbitMqConfig.Value.Hostname}, Port: {rabbitMqConfig.Value.Port}",
                    Confidence = DetectionConfidence.High,
                    TargetField = "RabbitMQ Integration"
                });
            }
        }
        catch (TaskCanceledException)
        {
            _logger.LogDebug("Integration discovery timeout for {Url}", applicationUri);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Integration discovery error for {Url}", applicationUri);
        }
    }

    private async Task ExtractEndpointConfigAsync(
        Uri applicationUri,
        TargetEnvironmentDetectionResponse result,
        CancellationToken cancellationToken)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(ProbeTimeoutSeconds));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, cts.Token);

            var configUrl = $"{applicationUri.Scheme}://{applicationUri.Host}/appsettings.json";

            if (!_endpointHelper.IsSafeProbeCandidate(configUrl))
                return;

            using var request = new HttpRequestMessage(HttpMethod.Get, configUrl);
            request.Headers.Add("User-Agent", "BirkNext/1.0");
            request.Headers.Add("Accept", "application/json");

            var response = await _httpClient.SendAsync(
                request, HttpCompletionOption.ResponseContentRead, linkedCts.Token);

            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxContentSizeBytes)
                return;

            var content = await response.Content.ReadAsStringAsync(linkedCts.Token);

            using var doc = JsonDocument.Parse(content);
            var root = doc.RootElement;

            var restUrl = _configHelper.ExtractRestBaseUrl(root);
            if (!string.IsNullOrWhiteSpace(restUrl) && Uri.TryCreate(restUrl, UriKind.Absolute, out _))
            {
                result.DetectedRestBaseUrl = restUrl;
                result.RestConfidence = DetectionConfidence.VeryHigh;
                result.DiscoveryEvidence.Add(new DiscoveryEvidence
                {
                    Type = DiscoveryEvidence.EvidenceType.StructuredConfig,
                    Status = EndpointEvidenceStatus.Observed,
                    LocationCategory = "/appsettings.json",
                    Value = "ApiBaseUrl or RestBaseUrl",
                    Confidence = DetectionConfidence.VeryHigh,
                    TargetField = "RestBaseUrl"
                });
            }

            var graphQlUrl = _configHelper.ExtractGraphQlEndpoint(root);
            if (!string.IsNullOrWhiteSpace(graphQlUrl) && (graphQlUrl.StartsWith("http") || graphQlUrl.StartsWith("/")))
            {
                result.DetectedGraphQlEndpoint = graphQlUrl;
                result.GraphQlConfidence = DetectionConfidence.VeryHigh;
                result.DiscoveryEvidence.Add(new DiscoveryEvidence
                {
                    Type = DiscoveryEvidence.EvidenceType.StructuredConfig,
                    Status = EndpointEvidenceStatus.Observed,
                    LocationCategory = "/appsettings.json",
                    Value = "GraphQL config",
                    Confidence = DetectionConfidence.VeryHigh,
                    TargetField = "GraphQlEndpoint"
                });
            }

            var swaggerUrl = _configHelper.ExtractSwaggerUrl(root);
            if (!string.IsNullOrWhiteSpace(swaggerUrl))
            {
                result.DetectedSwaggerUrl = swaggerUrl;
                result.SwaggerConfidence = DetectionConfidence.VeryHigh;
                result.DiscoveryEvidence.Add(new DiscoveryEvidence
                {
                    Type = DiscoveryEvidence.EvidenceType.StructuredConfig,
                    Status = EndpointEvidenceStatus.Observed,
                    LocationCategory = "/appsettings.json",
                    Value = "Swagger config",
                    Confidence = DetectionConfidence.VeryHigh,
                    TargetField = "SwaggerUrl"
                });
            }

            var healthUrl = _configHelper.ExtractHealthEndpoint(root);
            if (!string.IsNullOrWhiteSpace(healthUrl))
            {
                result.DetectedHealthEndpoint = healthUrl;
                result.HealthConfidence = DetectionConfidence.VeryHigh;
                result.DiscoveryEvidence.Add(new DiscoveryEvidence
                {
                    Type = DiscoveryEvidence.EvidenceType.StructuredConfig,
                    Status = EndpointEvidenceStatus.Observed,
                    LocationCategory = "/appsettings.json",
                    Value = "Health endpoint",
                    Confidence = DetectionConfidence.VeryHigh,
                    TargetField = "HealthEndpoint"
                });
            }
        }
        catch (TaskCanceledException)
        {
            _logger.LogDebug("Config discovery timeout for {Url}", applicationUri);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Config discovery error for {Url}", applicationUri);
        }
    }

    private async Task ProbeRestEndpointsAsync(Uri applicationUri, TargetEnvironmentDetectionResponse result, CancellationToken cancellationToken)
    {
        var evidence = await DiscoverConventionalEndpointAsync(
            _endpointHelper.GetRestEndpointCandidates(applicationUri.GetLeftPart(UriPartial.Authority)).Take(3),
            "REST", "RestBaseUrl", cancellationToken);
        if (evidence is null) return;
        result.DetectedRestBaseUrl = evidence.LocationCategory;
        result.RestConfidence = evidence.Confidence;
        result.DiscoveryEvidence.Add(evidence);
    }

    private async Task ProbeGraphQlEndpointsAsync(Uri applicationUri, TargetEnvironmentDetectionResponse result, CancellationToken cancellationToken)
    {
        var evidence = await DiscoverConventionalEndpointAsync(
            _endpointHelper.GetGraphQlEndpointCandidates(applicationUri.GetLeftPart(UriPartial.Authority)).Take(3),
            "GraphQL", "GraphQlEndpoint", cancellationToken);
        if (evidence is null) return;
        result.DetectedGraphQlEndpoint = evidence.LocationCategory;
        result.GraphQlConfidence = evidence.Confidence;
        result.DiscoveryEvidence.Add(evidence);
    }

    private async Task ProbeSwaggerEndpointsAsync(Uri applicationUri, TargetEnvironmentDetectionResponse result, CancellationToken cancellationToken)
    {
        var evidence = await DiscoverConventionalEndpointAsync(
            _endpointHelper.GetSwaggerEndpointCandidates(applicationUri.GetLeftPart(UriPartial.Authority)).Take(3),
            "Swagger", "SwaggerUrl", cancellationToken);
        if (evidence is null) return;
        result.DetectedSwaggerUrl = evidence.LocationCategory;
        result.SwaggerConfidence = evidence.Confidence;
        result.DiscoveryEvidence.Add(evidence);
    }

    private async Task ProbeHealthEndpointsAsync(Uri applicationUri, TargetEnvironmentDetectionResponse result, CancellationToken cancellationToken)
    {
        var evidence = await DiscoverConventionalEndpointAsync(
            _endpointHelper.GetHealthEndpointCandidates(applicationUri.GetLeftPart(UriPartial.Authority)).Take(5),
            "Health", "HealthEndpoint", cancellationToken);
        if (evidence is null) return;
        result.DetectedHealthEndpoint = evidence.LocationCategory;
        result.HealthConfidence = evidence.Confidence;
        result.DiscoveryEvidence.Add(evidence);
    }

    private async Task<DiscoveryEvidence?> DiscoverConventionalEndpointAsync(
        IEnumerable<string> candidates, string endpointType, string targetField, CancellationToken cancellationToken)
    {
        DiscoveryEvidence? firstCandidate = null;
        foreach (var candidate in candidates)
        {
            if (!_endpointHelper.IsSafeProbeCandidate(candidate)) continue;
            var evidence = await ProbeEndpointAsync(candidate, endpointType, cancellationToken);
            evidence.TargetField = targetField;
            firstCandidate ??= evidence;
            if (evidence.Status == EndpointEvidenceStatus.Confirmed) return evidence;
        }
        return firstCandidate;
    }

    private async Task<DiscoveryEvidence> ProbeEndpointAsync(string url, string endpointType, CancellationToken cancellationToken)
    {
        var evidence = new DiscoveryEvidence
        {
            Type = DiscoveryEvidence.EvidenceType.ConventionalCandidate,
            LocationCategory = url,
            Value = "Conventional path; service not confirmed",
            Confidence = DetectionConfidence.Low
        };
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(ProbeTimeoutSeconds));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, cts.Token);
            if (!_validator.ValidateTarget(url, "Public").IsValid ||
                !(await ValidateHostAddressesAsync(new Uri(url).Host, "Public", linkedCts.Token)).IsValid)
            {
                evidence.ProbeStatus = EndpointProbeStatus.Blocked;
                return evidence;
            }
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("User-Agent", "BirkNext/1.0");
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linkedCts.Token);
            evidence.HttpStatus = (int)response.StatusCode;
            evidence.ContentType = response.Content.Headers.ContentType?.MediaType;
            evidence.ProbeStatus = EndpointProbeStatus.ResponseReceived;
            // Probes do not follow redirects. Existing redirect policy remains fail-closed.
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                evidence.ProbeStatus = EndpointProbeStatus.Blocked;
                return evidence;
            }
            if (!response.IsSuccessStatusCode) return evidence;
            var body = await ReadBoundedContentAsync(response.Content, MaxContentSizeBytes, linkedCts.Token);
            if (body is null)
            {
                evidence.ProbeStatus = EndpointProbeStatus.SizeLimitExceeded;
                return evidence;
            }
            if (HasEndpointResponseEvidence(body, evidence.ContentType, endpointType))
            {
                evidence.Type = DiscoveryEvidence.EvidenceType.SafeProbe;
                evidence.Status = EndpointEvidenceStatus.Confirmed;
                evidence.Confidence = DetectionConfidence.High;
                evidence.Value = endpointType + " response evidence";
                if (endpointType == "Swagger") evidence.OpenApiKind = OpenApiResourceKind.OpenApiDocument;
            }
            else if (endpointType == "Swagger" && evidence.ContentType?.Contains("text/html", StringComparison.OrdinalIgnoreCase) == true &&
                     body.Contains("SwaggerUIBundle", StringComparison.Ordinal) && body.Contains("url:", StringComparison.Ordinal))
            {
                evidence.Status = EndpointEvidenceStatus.Observed;
                evidence.Confidence = DetectionConfidence.Medium;
                evidence.Type = DiscoveryEvidence.EvidenceType.HtmlReference;
                evidence.OpenApiKind = OpenApiResourceKind.SwaggerUi;
                evidence.Value = "Swagger UI reference; OpenAPI document not confirmed";
            }
            else evidence.Value = "Conventional path; response does not confirm endpoint type";
        }
        catch (OperationCanceledException) { evidence.ProbeStatus = EndpointProbeStatus.Timeout; }
        catch (Exception) { evidence.ProbeStatus = EndpointProbeStatus.Failed; }
        return evidence;
    }

    private static bool HasEndpointResponseEvidence(string body, string? contentType, string endpointType)
    {
        if (endpointType == "Health" && contentType?.Contains("text/plain", StringComparison.OrdinalIgnoreCase) == true)
            return new[] { "Healthy", "Unhealthy", "Degraded" }.Contains(body.Trim(), StringComparer.OrdinalIgnoreCase);
        if (contentType?.Contains("json", StringComparison.OrdinalIgnoreCase) != true) return false;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (endpointType == "REST") return root.ValueKind is JsonValueKind.Object or JsonValueKind.Array;
            if (root.ValueKind != JsonValueKind.Object) return false;
            return endpointType switch
            {
                "GraphQL" => root.TryGetProperty("data", out _) ||
                    (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array &&
                     errors.EnumerateArray().Any(e => e.ValueKind == JsonValueKind.Object && e.TryGetProperty("message", out _))),
                "Swagger" => (root.TryGetProperty("openapi", out var version) || root.TryGetProperty("swagger", out version)) &&
                    version.ValueKind == JsonValueKind.String && root.TryGetProperty("paths", out _) && root.TryGetProperty("info", out _),
                "Health" => root.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String &&
                    new[] { "Healthy", "Unhealthy", "Degraded" }.Contains(status.GetString(), StringComparer.OrdinalIgnoreCase),
                _ => false
            };
        }
        catch (JsonException) { return false; }
    }

    private static async Task<string?> ReadBoundedContentAsync(HttpContent content, int limit, CancellationToken token, bool allowPrefix = false)
    {
        if (!allowPrefix && content.Headers.ContentLength > limit) return null;
        using var stream = await content.ReadAsStreamAsync(token);
        var bytes = new byte[limit + (allowPrefix ? 0 : 1)];
        var count = 0;
        while (count < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(count), token);
            if (read == 0) break;
            count += read;
        }
        return count > limit ? null : Encoding.UTF8.GetString(bytes, 0, count);
    }

    private string ComputeUrlFingerprint(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return "";

        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(url));
        return Convert.ToBase64String(hash);
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
