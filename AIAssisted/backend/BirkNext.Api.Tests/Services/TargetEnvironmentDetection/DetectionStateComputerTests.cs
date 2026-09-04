using BirkNext.Api.Models;
using BirkNext.Api.Services.TargetEnvironmentDetection;
using Xunit;

namespace BirkNext.Api.Tests.Services.TargetEnvironmentDetection;

public sealed class DetectionStateComputerTests
{
    private readonly DetectionStateComputer _computer = new();

    #region ComputeStateFromResponse Tests

    [Fact]
    public void ComputeStateFromResponse_FailedResponse_ReturnsFailed()
    {
        var response = new TargetEnvironmentDetectionResponse
        {
            Success = false,
            Message = "Detection failed",
            ErrorCode = "NETWORK_ERROR"
        };

        var state = _computer.ComputeStateFromResponse(response);

        Assert.Equal(TargetDetectionState.Failed, state);
    }

    [Fact]
    public void ComputeStateFromResponse_ReachableNoAuth_ReturnsComplete()
    {
        var response = new TargetEnvironmentDetectionResponse
        {
            Success = true,
            Reachability = TargetReachability.Reachable,
            AuthenticationRequired = false
        };

        var state = _computer.ComputeStateFromResponse(response);

        Assert.Equal(TargetDetectionState.Complete, state);
    }

    [Fact]
    public void ComputeStateFromResponse_AuthenticationRequired_ReturnsAuthenticationRequired()
    {
        var response = new TargetEnvironmentDetectionResponse
        {
            Success = true,
            Reachability = TargetReachability.AuthenticationRequired,
            AuthenticationRequired = true
        };

        var state = _computer.ComputeStateFromResponse(response);

        Assert.Equal(TargetDetectionState.AuthenticationRequired, state);
    }

    [Fact]
    public void ComputeStateFromResponse_ReachableButAuthFlagSet_ReturnsAuthenticationRequired()
    {
        var response = new TargetEnvironmentDetectionResponse
        {
            Success = true,
            Reachability = TargetReachability.Reachable,
            AuthenticationRequired = true
        };

        var state = _computer.ComputeStateFromResponse(response);

        Assert.Equal(TargetDetectionState.AuthenticationRequired, state);
    }

    [Fact]
    public void ComputeStateFromResponse_Timeout_ReturnsFailed()
    {
        var response = new TargetEnvironmentDetectionResponse
        {
            Success = false,
            Reachability = TargetReachability.Timeout
        };

        var state = _computer.ComputeStateFromResponse(response);

        Assert.Equal(TargetDetectionState.Failed, state);
    }

    [Fact]
    public void ComputeStateFromResponse_TlsError_ReturnsFailed()
    {
        var response = new TargetEnvironmentDetectionResponse
        {
            Success = false,
            Reachability = TargetReachability.TlsError
        };

        var state = _computer.ComputeStateFromResponse(response);

        Assert.Equal(TargetDetectionState.Failed, state);
    }

    [Fact]
    public void ComputeStateFromResponse_DnsError_ReturnsFailed()
    {
        var response = new TargetEnvironmentDetectionResponse
        {
            Success = false,
            Reachability = TargetReachability.DnsError
        };

        var state = _computer.ComputeStateFromResponse(response);

        Assert.Equal(TargetDetectionState.Failed, state);
    }

    [Fact]
    public void ComputeStateFromResponse_Unreachable_ReturnsFailed()
    {
        var response = new TargetEnvironmentDetectionResponse
        {
            Success = false,
            Reachability = TargetReachability.Unreachable
        };

        var state = _computer.ComputeStateFromResponse(response);

        Assert.Equal(TargetDetectionState.Failed, state);
    }

    [Fact]
    public void ComputeStateFromResponse_TooManyRedirects_ReturnsFailed()
    {
        var response = new TargetEnvironmentDetectionResponse
        {
            Success = false,
            Reachability = TargetReachability.TooManyRedirects
        };

        var state = _computer.ComputeStateFromResponse(response);

        Assert.Equal(TargetDetectionState.Failed, state);
    }

    [Fact]
    public void ComputeStateFromResponse_UntrustedRedirect_ReturnsFailed()
    {
        var response = new TargetEnvironmentDetectionResponse
        {
            Success = false,
            Reachability = TargetReachability.UntrustedRedirect
        };

        var state = _computer.ComputeStateFromResponse(response);

        Assert.Equal(TargetDetectionState.Failed, state);
    }

    [Fact]
    public void ComputeStateFromResponse_Unknown_ReturnsFailed()
    {
        var response = new TargetEnvironmentDetectionResponse
        {
            Success = false,
            Reachability = TargetReachability.Unknown
        };

        var state = _computer.ComputeStateFromResponse(response);

        Assert.Equal(TargetDetectionState.Failed, state);
    }

    #endregion

    #region IsUrlStale Tests

    [Fact]
    public void IsUrlStale_SameUrl_ReturnsFalse()
    {
        var detected = "https://example.com/app";
        var current = "https://example.com/app";

        var isStale = _computer.IsUrlStale(detected, current);

        Assert.False(isStale);
    }

    [Fact]
    public void IsUrlStale_DifferentUrl_ReturnsTrue()
    {
        var detected = "https://example.com/app";
        var current = "https://different.com/app";

        var isStale = _computer.IsUrlStale(detected, current);

        Assert.True(isStale);
    }

    [Fact]
    public void IsUrlStale_CaseInsensitive_ReturnsFalse()
    {
        var detected = "https://EXAMPLE.COM/app";
        var current = "https://example.com/app";

        var isStale = _computer.IsUrlStale(detected, current);

        Assert.False(isStale);
    }

    [Fact]
    public void IsUrlStale_DifferentPath_ReturnsTrue()
    {
        var detected = "https://example.com/app1";
        var current = "https://example.com/app2";

        var isStale = _computer.IsUrlStale(detected, current);

        Assert.True(isStale);
    }

    [Fact]
    public void IsUrlStale_QueryParameterIgnored_ReturnsFalse()
    {
        var detected = "https://example.com/app";
        var current = "https://example.com/app?param=value";

        var isStale = _computer.IsUrlStale(detected, current);

        Assert.False(isStale);
    }

    [Fact]
    public void IsUrlStale_BothNull_ReturnsFalse()
    {
        var isStale = _computer.IsUrlStale(null, null);

        Assert.False(isStale);
    }

    [Fact]
    public void IsUrlStale_BothEmpty_ReturnsFalse()
    {
        var isStale = _computer.IsUrlStale("", "");

        Assert.False(isStale);
    }

    [Fact]
    public void IsUrlStale_OneNull_ReturnsTrue()
    {
        var isStale = _computer.IsUrlStale("https://example.com/app", null);

        Assert.True(isStale);
    }

    [Fact]
    public void IsUrlStale_OneEmpty_ReturnsTrue()
    {
        var isStale = _computer.IsUrlStale("https://example.com/app", "");

        Assert.True(isStale);
    }

    [Fact]
    public void IsUrlStale_InvalidUrlFormat_ComparesAsString()
    {
        var detected = "not a valid url";
        var current = "not a valid url";

        var isStale = _computer.IsUrlStale(detected, current);

        Assert.False(isStale);
    }

    [Fact]
    public void IsUrlStale_TrailingSlash_ReturnsFalse()
    {
        var detected = "https://example.com/app/";
        var current = "https://example.com/app";

        var isStale = _computer.IsUrlStale(detected, current);

        // Note: These are technically different URLs, but the normalization might handle this
        // depending on implementation. This test documents the current behavior.
        Assert.False(isStale);
    }

    #endregion

    #region IsReadyForActivation Tests

    [Fact]
    public void IsReadyForActivation_CompleteAndUrlCurrent_ReturnsTrue()
    {
        var isReady = _computer.IsReadyForActivation(TargetDetectionState.Complete, isUrlCurrent: true);

        Assert.True(isReady);
    }

    [Fact]
    public void IsReadyForActivation_AuthenticationRequiredAndUrlCurrent_ReturnsTrue()
    {
        var isReady = _computer.IsReadyForActivation(TargetDetectionState.AuthenticationRequired, isUrlCurrent: true);

        Assert.True(isReady);
    }

    [Fact]
    public void IsReadyForActivation_CompleteButUrlStale_ReturnsFalse()
    {
        var isReady = _computer.IsReadyForActivation(TargetDetectionState.Complete, isUrlCurrent: false);

        Assert.False(isReady);
    }

    [Fact]
    public void IsReadyForActivation_FailedState_ReturnsFalse()
    {
        var isReady = _computer.IsReadyForActivation(TargetDetectionState.Failed, isUrlCurrent: true);

        Assert.False(isReady);
    }

    [Fact]
    public void IsReadyForActivation_NotCheckedState_ReturnsFalse()
    {
        var isReady = _computer.IsReadyForActivation(TargetDetectionState.NotChecked, isUrlCurrent: true);

        Assert.False(isReady);
    }

    [Fact]
    public void IsReadyForActivation_StaleState_ReturnsFalse()
    {
        var isReady = _computer.IsReadyForActivation(TargetDetectionState.Stale, isUrlCurrent: false);

        Assert.False(isReady);
    }

    [Fact]
    public void IsReadyForActivation_CheckingState_ReturnsFalse()
    {
        var isReady = _computer.IsReadyForActivation(TargetDetectionState.Checking, isUrlCurrent: true);

        Assert.False(isReady);
    }

    [Fact]
    public void IsReadyForActivation_PartialState_ReturnsFalse()
    {
        var isReady = _computer.IsReadyForActivation(TargetDetectionState.Partial, isUrlCurrent: true);

        Assert.False(isReady);
    }

    #endregion

    #region GetStrategySuggestion Tests

    [Fact]
    public void GetStrategySuggestion_Complete_ReturnsDirectAccess()
    {
        var response = new TargetEnvironmentDetectionResponse { Success = true };

        var suggestion = _computer.GetStrategySuggestion(TargetDetectionState.Complete, response);

        Assert.Equal("direct-access", suggestion);
    }

    [Fact]
    public void GetStrategySuggestion_AuthenticationRequiredEntraId_ReturnsEntraStrategy()
    {
        var response = new TargetEnvironmentDetectionResponse
        {
            Success = true,
            DetectedAuthenticationType = FrontendAuthenticationType.MicrosoftEntraId
        };

        var suggestion = _computer.GetStrategySuggestion(TargetDetectionState.AuthenticationRequired, response);

        Assert.Equal("entra-id-browser-auth", suggestion);
    }

    [Fact]
    public void GetStrategySuggestion_AuthenticationRequiredOidc_ReturnsOidcStrategy()
    {
        var response = new TargetEnvironmentDetectionResponse
        {
            Success = true,
            DetectedAuthenticationType = FrontendAuthenticationType.OpenIdConnect
        };

        var suggestion = _computer.GetStrategySuggestion(TargetDetectionState.AuthenticationRequired, response);

        Assert.Equal("oidc-browser-auth", suggestion);
    }

    [Fact]
    public void GetStrategySuggestion_AuthenticationRequiredOAuth2_ReturnsOAuth2Strategy()
    {
        var response = new TargetEnvironmentDetectionResponse
        {
            Success = true,
            DetectedAuthenticationType = FrontendAuthenticationType.OAuth2
        };

        var suggestion = _computer.GetStrategySuggestion(TargetDetectionState.AuthenticationRequired, response);

        Assert.Equal("oauth2-browser-auth", suggestion);
    }

    [Fact]
    public void GetStrategySuggestion_AuthenticationRequiredNoType_ReturnsGenericStrategy()
    {
        var response = new TargetEnvironmentDetectionResponse
        {
            Success = true,
            DetectedAuthenticationType = FrontendAuthenticationType.None
        };

        var suggestion = _computer.GetStrategySuggestion(TargetDetectionState.AuthenticationRequired, response);

        Assert.Equal("browser-auth-required", suggestion);
    }

    [Fact]
    public void GetStrategySuggestion_Failed_ReturnsRetryDetection()
    {
        var response = new TargetEnvironmentDetectionResponse { Success = false };

        var suggestion = _computer.GetStrategySuggestion(TargetDetectionState.Failed, response);

        Assert.Equal("retry-detection", suggestion);
    }

    [Fact]
    public void GetStrategySuggestion_Stale_ReturnsReRunDetection()
    {
        var response = new TargetEnvironmentDetectionResponse { Success = true };

        var suggestion = _computer.GetStrategySuggestion(TargetDetectionState.Stale, response);

        Assert.Equal("re-run-detection", suggestion);
    }

    [Fact]
    public void GetStrategySuggestion_NotChecked_ReturnsRunDetection()
    {
        var response = new TargetEnvironmentDetectionResponse { Success = false };

        var suggestion = _computer.GetStrategySuggestion(TargetDetectionState.NotChecked, response);

        Assert.Equal("run-detection", suggestion);
    }

    [Fact]
    public void GetStrategySuggestion_Checking_ReturnsDetectionInProgress()
    {
        var response = new TargetEnvironmentDetectionResponse { Success = false };

        var suggestion = _computer.GetStrategySuggestion(TargetDetectionState.Checking, response);

        Assert.Equal("detection-in-progress", suggestion);
    }

    [Fact]
    public void GetStrategySuggestion_Partial_ReturnsBrowserAutomationRequired()
    {
        var response = new TargetEnvironmentDetectionResponse { Success = true };

        var suggestion = _computer.GetStrategySuggestion(TargetDetectionState.Partial, response);

        Assert.Equal("browser-automation-required", suggestion);
    }

    #endregion

    #region GetStateMessage Tests

    [Fact]
    public void GetStateMessage_Complete_ReturnsCompletionMessage()
    {
        var response = new TargetEnvironmentDetectionResponse
        {
            Success = true,
            Reachability = TargetReachability.Reachable
        };

        var message = _computer.GetStateMessage(TargetDetectionState.Complete, response, isUrlCurrent: true);

        Assert.Contains("reachable", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("accessible", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetStateMessage_AuthenticationRequiredEntra_IncludesAuthority()
    {
        var response = new TargetEnvironmentDetectionResponse
        {
            Success = true,
            DetectedAuthenticationType = FrontendAuthenticationType.MicrosoftEntraId,
            DetectedAuthority = "https://login.microsoftonline.com"
        };

        var message = _computer.GetStateMessage(TargetDetectionState.AuthenticationRequired, response, isUrlCurrent: true);

        Assert.Contains("Microsoft Entra ID", message);
        Assert.Contains("https://login.microsoftonline.com", message);
    }

    [Fact]
    public void GetStateMessage_Failed_IncludesErrorMessage()
    {
        var response = new TargetEnvironmentDetectionResponse
        {
            Success = false,
            Message = "Network timeout"
        };

        var message = _computer.GetStateMessage(TargetDetectionState.Failed, response, isUrlCurrent: true);

        Assert.Contains("failed", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Network timeout", message);
    }

    [Fact]
    public void GetStateMessage_Stale_IndicatesUrlChanged()
    {
        var response = new TargetEnvironmentDetectionResponse { Success = true };

        var message = _computer.GetStateMessage(TargetDetectionState.Stale, response, isUrlCurrent: false);

        Assert.Contains("stale", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("changed", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetStateMessage_NotChecked_IndicatesNoDetection()
    {
        var response = new TargetEnvironmentDetectionResponse { Success = false };

        var message = _computer.GetStateMessage(TargetDetectionState.NotChecked, response, isUrlCurrent: true);

        Assert.Contains("No detection", message);
    }

    #endregion

    #region CreateOutcome Tests

    [Fact]
    public void CreateOutcome_SuccessfulDetection_CreatesCompleteOutcome()
    {
        var response = new TargetEnvironmentDetectionResponse
        {
            Success = true,
            Reachability = TargetReachability.Reachable,
            AuthenticationRequired = false,
            OriginalUrl = "https://example.com/app"
        };

        var outcome = _computer.CreateOutcome(response, "https://example.com/app", "https://example.com/app");

        Assert.Equal(TargetDetectionState.Complete, outcome.State);
        Assert.True(outcome.IsActivationReady);
        Assert.True(outcome.IsUrlCurrent);
        Assert.NotNull(outcome.DetectedAt);
        Assert.Equal("direct-access", outcome.StrategySuggestion);
    }

    [Fact]
    public void CreateOutcome_AuthenticationRequired_CreatesAuthOutcome()
    {
        var response = new TargetEnvironmentDetectionResponse
        {
            Success = true,
            Reachability = TargetReachability.AuthenticationRequired,
            AuthenticationRequired = true,
            DetectedAuthenticationType = FrontendAuthenticationType.MicrosoftEntraId,
            DetectedAuthority = "https://login.microsoftonline.com"
        };

        var outcome = _computer.CreateOutcome(response, "https://example.com/app", "https://example.com/app");

        Assert.Equal(TargetDetectionState.AuthenticationRequired, outcome.State);
        Assert.True(outcome.IsActivationReady);
        Assert.Equal("entra-id-browser-auth", outcome.StrategySuggestion);
    }

    [Fact]
    public void CreateOutcome_FailedDetection_CreatesFailedOutcome()
    {
        var response = new TargetEnvironmentDetectionResponse
        {
            Success = false,
            Message = "Network error",
            ErrorCode = "NETWORK_ERROR"
        };

        var outcome = _computer.CreateOutcome(response, null, "https://example.com/app");

        Assert.Equal(TargetDetectionState.Failed, outcome.State);
        Assert.False(outcome.IsActivationReady);
        Assert.Equal("retry-detection", outcome.StrategySuggestion);
    }

    [Fact]
    public void CreateOutcome_UrlStale_CreatesStaleOutcome()
    {
        var response = new TargetEnvironmentDetectionResponse
        {
            Success = true,
            Reachability = TargetReachability.Reachable,
            AuthenticationRequired = false
        };

        var outcome = _computer.CreateOutcome(response, "https://old.com/app", "https://new.com/app");

        Assert.Equal(TargetDetectionState.Stale, outcome.State);
        Assert.False(outcome.IsActivationReady);
        Assert.False(outcome.IsUrlCurrent);
        Assert.Equal("re-run-detection", outcome.StrategySuggestion);
    }

    [Fact]
    public void CreateOutcome_NoHistoricalUrl_UsesCurrentUrl()
    {
        var response = new TargetEnvironmentDetectionResponse
        {
            Success = true,
            Reachability = TargetReachability.Reachable,
            AuthenticationRequired = false
        };

        var outcome = _computer.CreateOutcome(response, null, "https://example.com/app");

        Assert.Equal(TargetDetectionState.Complete, outcome.State);
        Assert.True(outcome.IsActivationReady);
        Assert.True(outcome.IsUrlCurrent);
    }

    [Fact]
    public void CreateOutcome_IncludesTimestamp()
    {
        var response = new TargetEnvironmentDetectionResponse { Success = true };
        var beforeCreation = DateTime.UtcNow;

        var outcome = _computer.CreateOutcome(response, null, null);

        var afterCreation = DateTime.UtcNow;
        Assert.NotNull(outcome.DetectedAt);
        Assert.True(outcome.DetectedAt >= beforeCreation);
        Assert.True(outcome.DetectedAt <= afterCreation);
    }

    #endregion
}
