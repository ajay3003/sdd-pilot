using BirkNext.Api.Models;
using BirkNext.Api.Services.FrontendBrowserRuntime;
using BirkNext.Api.Services.TargetEnvironmentDetection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BirkNext.Api.Tests.Services.TargetEnvironmentDetection;

/// <summary>
/// Integration tests verifying DetectionStateComputer works correctly with detection responses.
/// Tests realistic scenarios combining response data with state computation.
/// </summary>
public sealed class DetectionStateComputerIntegrationTests
{
    private readonly DetectionStateComputer _computer = new();

    [Fact]
    public void PublicWebsite_Success_ComputesCompleteState()
    {
        var response = new TargetEnvironmentDetectionResponse
        {
            Success = true,
            Reachability = TargetReachability.Reachable,
            AuthenticationRequired = false,
            OriginalUrl = "https://example.com",
            NormalizedTargetUrl = "https://example.com",
            SuggestedEnvironmentType = FrontendEnvironmentType.Production,
            Message = "Detection completed successfully"
        };

        var outcome = _computer.CreateOutcome(response, "https://example.com", "https://example.com");

        Assert.NotNull(outcome);
        Assert.Equal(TargetDetectionState.Complete, outcome.State);
        Assert.True(outcome.IsActivationReady);
        Assert.Equal("direct-access", outcome.StrategySuggestion);
        Assert.True(outcome.IsUrlCurrent);
    }

    [Fact]
    public void EntraIdProtectedApp_Success_ComputesAuthRequiredState()
    {
        var response = new TargetEnvironmentDetectionResponse
        {
            Success = true,
            Reachability = TargetReachability.AuthenticationRequired,
            AuthenticationRequired = true,
            OriginalUrl = "https://myapp.example.com",
            NormalizedTargetUrl = "https://myapp.example.com",
            DetectedAuthenticationType = FrontendAuthenticationType.MicrosoftEntraId,
            DetectedAuthority = "https://login.microsoftonline.com",
            DetectedTenantId = "00000000-0000-0000-0000-000000000001",
            DetectedClientId = "client-guid-here",
            Confidence = DetectionConfidence.VeryHigh,
            Message = "Azure Entra ID detected"
        };

        var outcome = _computer.CreateOutcome(response, "https://myapp.example.com", "https://myapp.example.com");

        Assert.NotNull(outcome);
        Assert.Equal(TargetDetectionState.AuthenticationRequired, outcome.State);
        Assert.False(outcome.IsActivationReady);
        Assert.Equal("entra-id-browser-auth", outcome.StrategySuggestion);
        Assert.Contains("Entra ID", outcome.Message);
    }

    [Fact]
    public void FailedDnsResolution_ComputesFailedState()
    {
        var response = new TargetEnvironmentDetectionResponse
        {
            Success = false,
            Reachability = TargetReachability.DnsError,
            OriginalUrl = "https://nonexistent.example.com",
            Message = "DNS resolution failed",
            ErrorCode = "DNS_ERROR"
        };

        var outcome = _computer.CreateOutcome(response, null, "https://nonexistent.example.com");

        Assert.NotNull(outcome);
        Assert.Equal(TargetDetectionState.Failed, outcome.State);
        Assert.False(outcome.IsActivationReady);
        Assert.Equal("retry-detection", outcome.StrategySuggestion);
    }

    [Fact]
    public void UrlChanged_ComputesStaleState()
    {
        var response = new TargetEnvironmentDetectionResponse
        {
            Success = true,
            Reachability = TargetReachability.Reachable,
            AuthenticationRequired = false,
            OriginalUrl = "https://old-url.example.com"
        };

        var outcome = _computer.CreateOutcome(response, "https://old-url.example.com", "https://new-url.example.com");

        Assert.NotNull(outcome);
        Assert.Equal(TargetDetectionState.Stale, outcome.State);
        Assert.False(outcome.IsActivationReady);
        Assert.False(outcome.IsUrlCurrent);
        Assert.Equal("re-run-detection", outcome.StrategySuggestion);
    }

    [Fact]
    public void Timeout_ComputesFailedState()
    {
        var response = new TargetEnvironmentDetectionResponse
        {
            Success = false,
            Reachability = TargetReachability.Timeout,
            OriginalUrl = "https://slow.example.com",
            Message = "Detection timeout exceeded",
            ErrorCode = "TIMEOUT"
        };

        var outcome = _computer.CreateOutcome(response, null, "https://slow.example.com");

        Assert.Equal(TargetDetectionState.Failed, outcome.State);
        Assert.False(outcome.IsActivationReady);
    }

    [Fact]
    public void UnknownAuthProvider_ComputesAuthRequiredState()
    {
        var response = new TargetEnvironmentDetectionResponse
        {
            Success = true,
            Reachability = TargetReachability.Reachable,
            AuthenticationRequired = true,
            OriginalUrl = "https://custom-auth.example.com",
            DetectedAuthenticationType = FrontendAuthenticationType.Unknown,
            Message = "Custom authentication detected"
        };

        var outcome = _computer.CreateOutcome(response, "https://custom-auth.example.com", "https://custom-auth.example.com");

        Assert.Equal(TargetDetectionState.AuthenticationRequired, outcome.State);
        Assert.False(outcome.IsActivationReady);
        Assert.Equal("browser-auth-required", outcome.StrategySuggestion);
    }

    [Fact]
    public void FirstTimeDetection_CompletesSuccessfully()
    {
        var response = new TargetEnvironmentDetectionResponse
        {
            Success = true,
            Reachability = TargetReachability.Reachable,
            AuthenticationRequired = false,
            OriginalUrl = "https://example.com"
        };

        // First detection: no historical data
        var outcome = _computer.CreateOutcome(response, null, "https://example.com");

        Assert.Equal(TargetDetectionState.Complete, outcome.State);
        Assert.True(outcome.IsActivationReady);
        Assert.True(outcome.IsUrlCurrent);
    }

    [Fact]
    public void UrlNormalization_IgnoresQueryParameters()
    {
        var response = new TargetEnvironmentDetectionResponse
        {
            Success = true,
            Reachability = TargetReachability.Reachable,
            AuthenticationRequired = false
        };

        // Same URL with different query parameters should not be stale
        var outcome = _computer.CreateOutcome(
            response,
            "https://example.com/app?v=1",
            "https://example.com/app?v=2");

        Assert.Equal(TargetDetectionState.Complete, outcome.State);
        Assert.True(outcome.IsUrlCurrent);
    }

    [Fact]
    public void UrlNormalization_HandlesCaseDifferences()
    {
        var response = new TargetEnvironmentDetectionResponse
        {
            Success = true,
            Reachability = TargetReachability.Reachable,
            AuthenticationRequired = false
        };

        // Same URL with different case should not be stale
        var outcome = _computer.CreateOutcome(
            response,
            "https://EXAMPLE.COM/app",
            "https://example.com/app");

        Assert.Equal(TargetDetectionState.Complete, outcome.State);
        Assert.True(outcome.IsUrlCurrent);
    }

    [Fact]
    public void UrlNormalization_HandlesTrailingSlash()
    {
        var response = new TargetEnvironmentDetectionResponse
        {
            Success = true,
            Reachability = TargetReachability.Reachable,
            AuthenticationRequired = false
        };

        // Same URL with/without trailing slash should not be stale
        var outcome = _computer.CreateOutcome(
            response,
            "https://example.com/app/",
            "https://example.com/app");

        Assert.Equal(TargetDetectionState.Complete, outcome.State);
        Assert.True(outcome.IsUrlCurrent);
    }

    [Fact]
    public void OutcomeIncludesAllRequiredFields()
    {
        var response = new TargetEnvironmentDetectionResponse
        {
            Success = true,
            Reachability = TargetReachability.Reachable,
            AuthenticationRequired = false
        };

        var outcome = _computer.CreateOutcome(response, "https://example.com", "https://example.com");

        Assert.NotNull(outcome.DetectionResponse);
        Assert.NotNull(outcome.State);
        Assert.NotNull(outcome.DetectedAt);
        Assert.NotNull(outcome.StrategySuggestion);
        Assert.NotNull(outcome.Message);
        Assert.Equal("https://example.com", outcome.DetectedUrl);
    }

    [Fact]
    public void TlsCertificateError_ComputesFailedState()
    {
        var response = new TargetEnvironmentDetectionResponse
        {
            Success = false,
            Reachability = TargetReachability.TlsError,
            OriginalUrl = "https://untrusted.example.com",
            Message = "TLS certificate validation failed",
            ErrorCode = "TLS_ERROR"
        };

        var outcome = _computer.CreateOutcome(response, null, "https://untrusted.example.com");

        Assert.Equal(TargetDetectionState.Failed, outcome.State);
        Assert.False(outcome.IsActivationReady);
    }

    [Fact]
    public void TooManyRedirects_ComputesFailedState()
    {
        var response = new TargetEnvironmentDetectionResponse
        {
            Success = false,
            Reachability = TargetReachability.TooManyRedirects,
            OriginalUrl = "https://example.com",
            Message = "Too many redirects",
            ErrorCode = "TOO_MANY_REDIRECTS",
            RedirectCount = 5
        };

        var outcome = _computer.CreateOutcome(response, null, "https://example.com");

        Assert.Equal(TargetDetectionState.Failed, outcome.State);
        Assert.False(outcome.IsActivationReady);
    }

    [Fact]
    public void OAuthProvider_DetectsOAuth2Strategy()
    {
        var response = new TargetEnvironmentDetectionResponse
        {
            Success = true,
            Reachability = TargetReachability.AuthenticationRequired,
            AuthenticationRequired = true,
            DetectedAuthenticationType = FrontendAuthenticationType.OAuth2,
            DetectedAuthority = "https://oauth.example.com"
        };

        var outcome = _computer.CreateOutcome(response, "https://myapp.example.com", "https://myapp.example.com");

        Assert.Equal("oauth2-browser-auth", outcome.StrategySuggestion);
    }

    [Fact]
    public void OidcProvider_DetectsOidcStrategy()
    {
        var response = new TargetEnvironmentDetectionResponse
        {
            Success = true,
            Reachability = TargetReachability.AuthenticationRequired,
            AuthenticationRequired = true,
            DetectedAuthenticationType = FrontendAuthenticationType.OpenIdConnect,
            DetectedAuthority = "https://oidc.example.com"
        };

        var outcome = _computer.CreateOutcome(response, "https://myapp.example.com", "https://myapp.example.com");

        Assert.Equal("oidc-browser-auth", outcome.StrategySuggestion);
    }
}
