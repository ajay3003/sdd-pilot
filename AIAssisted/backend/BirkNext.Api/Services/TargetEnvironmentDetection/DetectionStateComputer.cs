using BirkNext.Api.Models;

namespace BirkNext.Api.Services.TargetEnvironmentDetection;

/// <summary>
/// Computes detection state and activation readiness from preflight response.
/// Handles state mapping, staleness checking, and activation decision logic.
/// </summary>
public interface IDetectionStateComputer
{
    /// <summary>
    /// Compute detection state from a preflight response.
    /// </summary>
    TargetDetectionState ComputeStateFromResponse(TargetEnvironmentDetectionResponse response);

    /// <summary>
    /// Determine if URL has changed since detection (staleness check).
    /// </summary>
    bool IsUrlStale(string? detectedUrl, string? currentUrl);

    /// <summary>
    /// Determine if the profile is ready for activation based on current state.
    /// </summary>
    bool IsReadyForActivation(TargetDetectionState state, bool isUrlCurrent);

    /// <summary>
    /// Suggest a detection strategy based on the current state.
    /// </summary>
    string GetStrategySuggestion(TargetDetectionState state, TargetEnvironmentDetectionResponse response);

    /// <summary>
    /// Generate a human-readable message explaining the current state.
    /// </summary>
    string GetStateMessage(TargetDetectionState state, TargetEnvironmentDetectionResponse response, bool isUrlCurrent);

    /// <summary>
    /// Create a complete detection outcome from a response, detected URL, and current profile URL.
    /// </summary>
    TargetDetectionOutcome CreateOutcome(
        TargetEnvironmentDetectionResponse response,
        string? detectedUrl,
        string? currentProfileUrl);
}

public sealed class DetectionStateComputer : IDetectionStateComputer
{
    public TargetDetectionState ComputeStateFromResponse(TargetEnvironmentDetectionResponse response)
    {
        // If detection failed, return Failed state
        if (!response.Success)
        {
            return TargetDetectionState.Failed;
        }

        // Detection succeeded, now determine specific state based on reachability
        return response.Reachability switch
        {
            // Reachable without auth = may be Complete OR Partial depending on app type
            // For client-side apps (SPA), server-side detection alone cannot determine auth.
            // Mark as Partial if likely SPA, otherwise Complete.
            TargetReachability.Reachable when !response.AuthenticationRequired =>
                LikelyClientSideApp(response) ? TargetDetectionState.Partial : TargetDetectionState.Complete,

            // Authentication boundary detected = AuthenticationRequired
            TargetReachability.AuthenticationRequired =>
                TargetDetectionState.AuthenticationRequired,

            // Auth detected but not explicitly marked as AuthenticationRequired
            // (e.g., 403 or login page heuristic) = AuthenticationRequired
            TargetReachability.Reachable when response.AuthenticationRequired =>
                TargetDetectionState.AuthenticationRequired,

            // Any network/security error = Failed
            TargetReachability.Timeout =>
                TargetDetectionState.Failed,
            TargetReachability.TlsError =>
                TargetDetectionState.Failed,
            TargetReachability.DnsError =>
                TargetDetectionState.Failed,
            TargetReachability.Unreachable =>
                TargetDetectionState.Failed,
            TargetReachability.TooManyRedirects =>
                TargetDetectionState.Failed,
            TargetReachability.UntrustedRedirect =>
                TargetDetectionState.Failed,

            // Unknown or unknown error = Failed
            TargetReachability.Unknown =>
                TargetDetectionState.Failed,

            // Default to Failed for safety
            _ => TargetDetectionState.Failed
        };
    }

    /// <summary>
    /// Heuristic: detect if target is likely a client-side app (SPA) that requires browser runtime.
    /// Returns true only if:
    /// 1. A client-side framework was explicitly detected (Blazor WASM, React, etc.)
    /// 2. AND server-side detection lacks sufficient auth/runtime completion evidence
    /// </summary>
    private static bool LikelyClientSideApp(TargetEnvironmentDetectionResponse response)
    {
        // Requires explicit positive framework detection
        if (response.DetectedClientFramework is null)
            return false;

        // If we detected a client-side framework but server-side preflight didn't detect auth,
        // then auth likely happens at runtime in the browser.
        // Mark as requiring browser inspection.
        return true;
    }

    public bool IsUrlStale(string? detectedUrl, string? currentUrl)
    {
        // Normalize URLs for comparison (scheme + host + path only)
        var normalized1 = NormalizeUrlForComparison(detectedUrl);
        var normalized2 = NormalizeUrlForComparison(currentUrl);

        // If both are null/empty, consider them equal (not stale)
        if (string.IsNullOrWhiteSpace(normalized1) && string.IsNullOrWhiteSpace(normalized2))
            return false;

        // If detected URL is null/empty but current URL exists, not stale (no historical data)
        if (string.IsNullOrWhiteSpace(normalized1) && !string.IsNullOrWhiteSpace(normalized2))
            return false;

        // If current URL is null/empty but detected URL exists, it's stale (URL was removed)
        if (!string.IsNullOrWhiteSpace(normalized1) && string.IsNullOrWhiteSpace(normalized2))
            return true;

        // Both have values, compare them (case-insensitive)
        return !string.Equals(normalized1, normalized2, StringComparison.OrdinalIgnoreCase);
    }

    public bool IsReadyForActivation(TargetDetectionState state, bool isUrlCurrent)
    {
        // Not ready if URL is stale
        if (!isUrlCurrent)
            return false;

        return state == TargetDetectionState.Complete;
    }

    public string GetStrategySuggestion(TargetDetectionState state, TargetEnvironmentDetectionResponse response)
    {
        return state switch
        {
            TargetDetectionState.Complete =>
                "direct-access",

            TargetDetectionState.AuthenticationRequired =>
                response.DetectedAuthenticationType switch
                {
                    FrontendAuthenticationType.MicrosoftEntraId =>
                        "entra-id-browser-auth",
                    FrontendAuthenticationType.OpenIdConnect =>
                        "oidc-browser-auth",
                    FrontendAuthenticationType.OAuth2 =>
                        "oauth2-browser-auth",
                    _ => "browser-auth-required"
                },

            TargetDetectionState.Failed =>
                "retry-detection",

            TargetDetectionState.Stale =>
                "re-run-detection",

            TargetDetectionState.NotChecked =>
                "run-detection",

            TargetDetectionState.Checking =>
                "detection-in-progress",

            TargetDetectionState.Partial =>
                "browser-automation-required",

            _ => "unknown"
        };
    }

    public string GetStateMessage(TargetDetectionState state, TargetEnvironmentDetectionResponse response, bool isUrlCurrent)
    {
        return state switch
        {
            TargetDetectionState.Complete =>
                "Target is reachable and accessible without authentication. Profile is ready for activation.",

            TargetDetectionState.AuthenticationRequired =>
                response.DetectedAuthenticationType switch
                {
                    FrontendAuthenticationType.MicrosoftEntraId =>
                        $"Authentication required via Microsoft Entra ID. Authority: {response.DetectedAuthority}",
                    FrontendAuthenticationType.OpenIdConnect =>
                        $"Authentication required via OpenID Connect. Authority: {response.DetectedAuthority}",
                    FrontendAuthenticationType.OAuth2 =>
                        $"Authentication required via OAuth 2.0. Authority: {response.DetectedAuthority}",
                    _ => "Target requires authentication before access."
                },

            TargetDetectionState.Failed =>
                $"Detection failed: {response.Message ?? "Unknown error"}",

            TargetDetectionState.Stale when !isUrlCurrent =>
                "Detection result is stale - target URL has changed. Please re-run detection.",

            TargetDetectionState.NotChecked =>
                "No detection has been performed for this target.",

            TargetDetectionState.Checking =>
                "Detection is currently in progress.",

            TargetDetectionState.Partial =>
                "Partial detection completed. Browser automation may be needed for complete analysis.",

            _ => "Unknown detection state."
        };
    }

    public TargetDetectionOutcome CreateOutcome(
        TargetEnvironmentDetectionResponse response,
        string? detectedUrl,
        string? currentProfileUrl)
    {
        var state = ComputeStateFromResponse(response);
        var isUrlStale = IsUrlStale(detectedUrl, currentProfileUrl);

        // If URL is stale, mark state as Stale
        if (isUrlStale && state != TargetDetectionState.NotChecked && state != TargetDetectionState.Failed)
        {
            state = TargetDetectionState.Stale;
        }

        var isUrlCurrent = !isUrlStale;
        var isActivationReady = IsReadyForActivation(state, isUrlCurrent);

        // Determine if this Partial state specifically requires browser runtime inspection
        var browserRuntimeRequired = state == TargetDetectionState.Partial &&
                                     response.Reachability == TargetReachability.Reachable &&
                                     !response.AuthenticationRequired &&
                                     LikelyClientSideApp(response);

        return new TargetDetectionOutcome
        {
            DetectionResponse = response,
            State = state,
            IsActivationReady = isActivationReady,
            StrategySuggestion = GetStrategySuggestion(state, response),
            DetectedAt = DateTime.UtcNow,
            DetectedUrl = detectedUrl,
            IsUrlCurrent = isUrlCurrent,
            Message = GetStateMessage(state, response, isUrlCurrent),
            BrowserRuntimeInspectionRequired = browserRuntimeRequired ? true : null
        };
    }

    private static string? NormalizeUrlForComparison(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return url;

        // Return scheme + host + normalized path (no query, no fragment)
        // Normalize paths: "/app/" becomes "/app", "/" stays as ""
        var path = uri.AbsolutePath;
        if (path.EndsWith("/") && path.Length > 1)
            path = path.TrimEnd('/');
        if (path == "/")
            path = "";

        return $"{uri.Scheme}://{uri.Host}{path}";
    }
}
