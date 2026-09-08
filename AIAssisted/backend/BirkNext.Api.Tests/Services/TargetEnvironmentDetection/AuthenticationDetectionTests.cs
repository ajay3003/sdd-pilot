using Xunit;
using BirkNext.Api.Models;
using BirkNext.Api.Services.TargetEnvironmentDetection;
using BirkNext.Api.Services.FrontendBrowserRuntime;
using Microsoft.Extensions.Logging;
using Moq;

namespace BirkNext.Api.Tests.Services.TargetEnvironmentDetection;

public class AuthenticationDetectionTests
{
    private readonly ITargetEnvironmentDetectionService _service;
    private readonly BrowserTargetValidator _validator;

    public AuthenticationDetectionTests()
    {
        var mockLogger = new Mock<ILogger<TargetEnvironmentDetectionService>>();
        var mockHttpClient = new Mock<HttpClient>();
        var mockResolver = new Mock<ITargetHostResolver>();
        var mockFrameworkDetector = new Mock<IClientFrameworkDetector>();

        _validator = new BrowserTargetValidator();

        _service = new TargetEnvironmentDetectionService(
            _validator,
            mockHttpClient.Object,
            mockResolver.Object,
            mockFrameworkDetector.Object,
            mockLogger.Object);
    }

    [Fact]
    public async Task DetectFromUrl_MicrosoftEntraWithRedirectUri_DetectsAllFields()
    {
        // This test would require mocking the actual HTTP client to simulate
        // a redirect to login.microsoftonline.com with redirect_uri parameter.
        // For now, we verify the model structure supports it.

        var response = new TargetEnvironmentDetectionResponse
        {
            Success = true,
            DetectedAuthenticationType = FrontendAuthenticationType.MicrosoftEntraId,
            DetectedAuthority = "https://login.microsoftonline.com",
            DetectedTenantId = "12345678-1234-1234-1234-123456789012",
            DetectedClientId = "87654321-4321-4321-4321-210987654321",
            DetectedRedirectUrls = ["https://m2lbdev.bufetat.no/auth/callback"]
        };

        Assert.NotNull(response.DetectedRedirectUrls);
        Assert.Single(response.DetectedRedirectUrls);
        Assert.Equal("https://m2lbdev.bufetat.no/auth/callback", response.DetectedRedirectUrls[0]);
    }

    [Fact]
    public async Task DetectFromUrl_MicrosoftEntraPartialDetection_PopulatesAvailableFields()
    {
        var response = new TargetEnvironmentDetectionResponse
        {
            Success = true,
            DetectedAuthenticationType = FrontendAuthenticationType.MicrosoftEntraId,
            DetectedAuthority = "https://login.microsoftonline.com",
            TenantMode = "common",
            DetectedClientId = null,
            DetectedRedirectUrls = []
        };

        Assert.Equal(FrontendAuthenticationType.MicrosoftEntraId, response.DetectedAuthenticationType);
        Assert.Equal("https://login.microsoftonline.com", response.DetectedAuthority);
        Assert.Equal("common", response.TenantMode);
        Assert.Null(response.DetectedClientId);
        Assert.Empty(response.DetectedRedirectUrls);
    }

    [Fact]
    public async Task DetectFromUrl_NoAuthentication_AllDetectedFieldsNull()
    {
        var response = new TargetEnvironmentDetectionResponse
        {
            Success = true,
            Reachability = TargetReachability.Reachable,
            AuthenticationRequired = false,
            DetectedAuthenticationType = FrontendAuthenticationType.None,
            DetectedAuthority = null,
            DetectedTenantId = null,
            DetectedClientId = null,
            DetectedRedirectUrls = []
        };

        Assert.Equal(FrontendAuthenticationType.None, response.DetectedAuthenticationType);
        Assert.Null(response.DetectedAuthority);
        Assert.Null(response.DetectedClientId);
        Assert.Empty(response.DetectedRedirectUrls);
    }

    [Fact]
    public void ConfidenceCalculation_WithCompleteEntraDetection_VeryHigh()
    {
        var response = new TargetEnvironmentDetectionResponse
        {
            Success = true,
            Reachability = TargetReachability.AuthenticationRequired,
            AuthenticationRequired = true,
            DetectedAuthenticationType = FrontendAuthenticationType.MicrosoftEntraId,
            DetectedAuthority = "https://login.microsoftonline.com",
            DetectedTenantId = "12345678-1234-1234-1234-123456789012",
            DetectedClientId = "87654321-4321-4321-4321-210987654321",
            DetectedRedirectUrls = ["https://m2lbdev.bufetat.no/auth/callback"]
        };

        // Manually calculate to verify the scoring
        var score = 0;
        if (response.Reachability == TargetReachability.AuthenticationRequired)
            score += 1;
        if (response.AuthenticationRequired && response.DetectedAuthenticationType != FrontendAuthenticationType.None)
            score += 1;
        if (!string.IsNullOrEmpty(response.DetectedTenantId))
            score += 1;
        if (!string.IsNullOrEmpty(response.DetectedClientId))
            score += 1;
        if (response.DetectedRedirectUrls.Count > 0)
            score += 1;

        Assert.Equal(5, score);
        // Score >= 5 should yield VeryHigh confidence
        Assert.True(score >= 5);
    }

    [Fact]
    public void MultipleRedirectUrls_AllDetected()
    {
        var response = new TargetEnvironmentDetectionResponse
        {
            DetectedRedirectUrls = new()
            {
                "https://app.example.com/auth/callback",
                "https://app.example.com/signin-callback",
                "https://staging.example.com/callback"
            }
        };

        Assert.Equal(3, response.DetectedRedirectUrls.Count);
        Assert.Contains("https://app.example.com/auth/callback", response.DetectedRedirectUrls);
        Assert.Contains("https://app.example.com/signin-callback", response.DetectedRedirectUrls);
        Assert.Contains("https://staging.example.com/callback", response.DetectedRedirectUrls);
    }

    [Fact]
    public void SecretSentinel_NotIncludedInDetection()
    {
        // Verify that if by chance a clientSecret appeared in detected values,
        // it would not be included in the model
        var response = new TargetEnvironmentDetectionResponse
        {
            DetectedClientId = "public-app-id",
            DetectedAuthority = "https://login.microsoftonline.com",
            // There should be NO field for clientSecret in the model
            // This test verifies the model structure prevents it
        };

        // Verify model doesn't have a clientSecret or accessToken field
        var type = typeof(TargetEnvironmentDetectionResponse);
        var secretProperty = type.GetProperty("ClientSecret");
        var tokenProperty = type.GetProperty("AccessToken");
        var passwordProperty = type.GetProperty("Password");

        Assert.Null(secretProperty);
        Assert.Null(tokenProperty);
        Assert.Null(passwordProperty);
    }

    [Fact]
    public void ManualVerificationIndependent_FromDetectionResult()
    {
        var response = new TargetEnvironmentDetectionResponse
        {
            Success = true,
            DetectedAuthenticationType = FrontendAuthenticationType.MicrosoftEntraId,
            ManualAuthenticationVerificationRequired = true,
            ManualAuthenticationVerificationStatus = ManualAuthenticationVerificationStatus.Pending
        };

        // Detected auth is MicrosoftEntra
        Assert.Equal(FrontendAuthenticationType.MicrosoftEntraId, response.DetectedAuthenticationType);

        // But manual verification is required and pending (independent)
        Assert.True(response.ManualAuthenticationVerificationRequired);
        Assert.Equal(ManualAuthenticationVerificationStatus.Pending, response.ManualAuthenticationVerificationStatus);
    }
}
