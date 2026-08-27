using BirkNext.Web.Models;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using System.Web;

namespace BirkNext.Web.Services;

/// <summary>
/// Detects target environment configuration from URL inspection.
/// Reuses TargetPreflightService for safety validation.
/// Extracts authentication type, tenant ID, client ID from redirect chains.
/// Returns only safe, non-sensitive metadata for configuration draft.
/// </summary>
public interface ITargetEnvironmentDetectionService
{
    Task<TargetEnvironmentDetectionResult> DetectFromUrlAsync(string targetUrl, CancellationToken cancellationToken = default);
}

public sealed class TargetEnvironmentDetectionService : ITargetEnvironmentDetectionService
{
    private readonly ITargetPreflightService _preflight;
    private readonly HttpClient _httpClient;
    private readonly ILogger<TargetEnvironmentDetectionService> _logger;

    // Microsoft Entra login hosts
    private static readonly HashSet<string> ApprovedEntraHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "login.microsoftonline.com",
        "login.microsoft.com",
        "login.windows.net"
    };

    public TargetEnvironmentDetectionService(
        ITargetPreflightService preflight,
        HttpClient httpClient,
        ILogger<TargetEnvironmentDetectionService> logger)
    {
        _preflight = preflight;
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<TargetEnvironmentDetectionResult> DetectFromUrlAsync(
        string targetUrl,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(targetUrl))
            return ErrorResult(targetUrl, "URL is empty", "EMPTY_URL");

        try
        {
            if (!Uri.TryCreate(targetUrl, UriKind.Absolute, out var uri))
                return ErrorResult(targetUrl, "Invalid URL format", "INVALID_URL");

            if (uri.Scheme != "https" && uri.Scheme != "http")
                return ErrorResult(targetUrl, "Only HTTP and HTTPS schemes are supported", "UNSUPPORTED_SCHEME");

            // Use preflight service to safely check target
            var preflightResult = await _preflight.CheckTargetAsync(targetUrl);

            var result = new TargetEnvironmentDetectionResult
            {
                OriginalUrl = targetUrl,
                NormalizedTargetUrl = preflightResult.FinalUrl,
                Success = true,
                Message = "Detection completed successfully"
            };

            // Map preflight reachability to our enum
            result.Reachability = MapPreflightStatus(preflightResult.Status);
            result.AuthenticationRequired = preflightResult.Status == PreflightStatus.AuthenticationRequired
                || preflightResult.IsLikelyLoginPage;

            // If authentication is required, inspect the redirect chain
            if (result.AuthenticationRequired && !string.IsNullOrWhiteSpace(preflightResult.FinalUrl))
            {
                await DetectAuthenticationMetadataAsync(preflightResult.FinalUrl, result, cancellationToken);
            }

            // Suggest environment type from hostname
            SuggestEnvironmentType(uri.Host, result);

            // Suggest profile name from hostname
            result.SuggestedProfileName = SuggestProfileName(uri.Host);

            result.Confidence = CalculateConfidence(result);

            return result;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "HTTP error during target detection for {Url}", targetUrl);
            return ErrorResult(targetUrl, $"Network error: {ex.Message}", "NETWORK_ERROR");
        }
        catch (TaskCanceledException)
        {
            return ErrorResult(targetUrl, "Detection timeout exceeded", "TIMEOUT");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during target detection for {Url}", targetUrl);
            return ErrorResult(targetUrl, "Detection failed with internal error", "INTERNAL_ERROR");
        }
    }

    private TargetReachability MapPreflightStatus(PreflightStatus status) => status switch
    {
        PreflightStatus.Ready => TargetReachability.Reachable,
        PreflightStatus.ReadyWithWarnings => TargetReachability.Reachable,
        PreflightStatus.AuthenticationRequired => TargetReachability.AuthenticationRequired,
        PreflightStatus.Unreachable => TargetReachability.Unreachable,
        PreflightStatus.InvalidTarget => TargetReachability.Unreachable,
        PreflightStatus.ScannerUnavailable => TargetReachability.Unknown,
        _ => TargetReachability.Unknown
    };

    private Task DetectAuthenticationMetadataAsync(
        string finalUrl,
        TargetEnvironmentDetectionResult result,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!Uri.TryCreate(finalUrl, UriKind.Absolute, out var finalUri))
                return Task.CompletedTask;

            var host = finalUri.Host;

            // Check for Microsoft Entra authentication
            if (ApprovedEntraHosts.Contains(host))
            {
                result.DetectedAuthenticationType = FrontendAuthenticationType.MicrosoftEntraId;
                result.DetectedAuthority = $"{finalUri.Scheme}://{host}";

                // Extract tenant and client IDs from URL
                var query = HttpUtility.ParseQueryString(finalUri.Query);

                // Extract tenant from path (e.g., /tenant/oauth2/v2.0/authorize)
                var tenantFromPath = ExtractTenantFromPath(finalUri.AbsolutePath);
                if (!string.IsNullOrEmpty(tenantFromPath))
                {
                    if (IsConcreteTenanId(tenantFromPath))
                        result.DetectedTenantId = tenantFromPath;
                    else
                        result.TenantMode = tenantFromPath;
                }

                // Extract client_id from query
                var clientId = query["client_id"];
                if (!string.IsNullOrEmpty(clientId))
                    result.DetectedClientId = clientId;

                result.Confidence = DetectionConfidence.VeryHigh;
            }
            else if (host.Contains("oauth", StringComparison.OrdinalIgnoreCase) ||
                     host.Contains("auth", StringComparison.OrdinalIgnoreCase))
            {
                // Generic OAuth/OIDC detected
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

        return Task.CompletedTask;
    }

    private string? ExtractTenantFromPath(string path)
    {
        // Pattern: /tenant/oauth2/v2.0/authorize or /tenant_id/...
        var match = Regex.Match(path, @"^/([^/]+)/(?:oauth2|openid)", RegexOptions.IgnoreCase);
        if (match.Success && match.Groups.Count > 1)
            return match.Groups[1].Value;

        return null;
    }

    private bool IsConcreteTenanId(string value)
    {
        // GUID format: 00000000-0000-0000-0000-000000000000
        if (Guid.TryParse(value, out _))
            return true;

        // Explicit tenant IDs (not common/organizations/consumers)
        return !new[] { "common", "organizations", "consumers" }
            .Contains(value, StringComparer.OrdinalIgnoreCase);
    }

    private void SuggestEnvironmentType(string hostname, TargetEnvironmentDetectionResult result)
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

        if (result.SuggestedEnvironmentType.HasValue)
            result.Warnings.Add($"Environment type suggested from hostname: {result.SuggestedEnvironmentType}");
    }

    private string? SuggestProfileName(string hostname)
    {
        // Extract meaningful parts of hostname
        // Example: m2lbdev.bufetat.no → M2LB DEV
        var parts = hostname.Split('.');
        if (parts.Length == 0)
            return null;

        var mainPart = parts[0];
        if (mainPart.Length < 2)
            return null;

        // Insert spaces before capital letters and digits
        var formatted = Regex.Replace(mainPart, @"([a-z])([A-Z])", "$1 $2", RegexOptions.IgnoreCase);
        formatted = Regex.Replace(formatted, @"([a-zA-Z])(\d)", "$1 $2", RegexOptions.IgnoreCase);

        return formatted.ToUpperInvariant().Trim();
    }

    private DetectionConfidence CalculateConfidence(TargetEnvironmentDetectionResult result)
    {
        var score = 0;

        // Reachability confidence
        if (result.Reachability == TargetReachability.Reachable)
            score += 2;
        else if (result.Reachability == TargetReachability.AuthenticationRequired)
            score += 1;

        // Authentication detection confidence
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

    private TargetEnvironmentDetectionResult ErrorResult(
        string originalUrl,
        string message,
        string errorCode)
    {
        _logger.LogWarning("Detection failed for {Url}: {Message} ({ErrorCode})", originalUrl, message, errorCode);

        return new TargetEnvironmentDetectionResult
        {
            OriginalUrl = originalUrl,
            Success = false,
            Message = message,
            ErrorCode = errorCode,
            Confidence = DetectionConfidence.Low
        };
    }
}
