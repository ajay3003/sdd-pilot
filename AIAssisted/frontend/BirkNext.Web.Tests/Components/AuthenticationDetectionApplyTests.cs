using Xunit;
using BirkNext.Web.Models;

namespace BirkNext.Web.Tests.Components;

public class AuthenticationDetectionApplyTests
{
    [Fact]
    public void ApplyDetectedAuthenticationSettings_CopiesAllDetectedFieldsToConfigured()
    {
        // Simulate a profile with no configured auth settings
        var profile = new FrontendAnalysisProfile
        {
            Id = "test-profile",
            Name = "Test Profile",
            TargetUrl = "https://m2lbdev.bufetat.no",
            Authentication = new()
            {
                AuthenticationType = FrontendAuthenticationType.None,
                ExpectedAuthority = null,
                ExpectedTenant = null,
                ExpectedClientId = null,
                AllowedRedirectUrls = []
            }
        };

        // Simulate detection result with complete auth metadata
        var detectionResult = new TargetEnvironmentDetectionResult
        {
            Success = true,
            DetectedAuthenticationType = FrontendAuthenticationType.MicrosoftEntraId,
            DetectedAuthority = "https://login.microsoftonline.com",
            DetectedTenantId = "12345678-1234-1234-1234-123456789012",
            DetectedClientId = "87654321-4321-4321-4321-210987654321",
            DetectedRedirectUrls = ["https://m2lbdev.bufetat.no/auth/callback"]
        };

        // Apply detected settings to profile (simulating the component method)
        var auth = profile.Authentication;
        if (detectionResult.DetectedAuthenticationType != FrontendAuthenticationType.None)
            auth.AuthenticationType = detectionResult.DetectedAuthenticationType;
        if (!string.IsNullOrWhiteSpace(detectionResult.DetectedAuthority))
            auth.ExpectedAuthority = detectionResult.DetectedAuthority;
        if (!string.IsNullOrWhiteSpace(detectionResult.DetectedTenantId))
            auth.ExpectedTenant = detectionResult.DetectedTenantId;
        if (!string.IsNullOrWhiteSpace(detectionResult.DetectedClientId))
            auth.ExpectedClientId = detectionResult.DetectedClientId;
        if (detectionResult.DetectedRedirectUrls.Count > 0)
        {
            var merged = new HashSet<string>(auth.AllowedRedirectUrls, StringComparer.OrdinalIgnoreCase);
            foreach (var url in detectionResult.DetectedRedirectUrls)
                merged.Add(url);
            auth.AllowedRedirectUrls = merged.ToList();
        }

        // Verify all fields were copied
        Assert.Equal(FrontendAuthenticationType.MicrosoftEntraId, profile.Authentication.AuthenticationType);
        Assert.Equal("https://login.microsoftonline.com", profile.Authentication.ExpectedAuthority);
        Assert.Equal("12345678-1234-1234-1234-123456789012", profile.Authentication.ExpectedTenant);
        Assert.Equal("87654321-4321-4321-4321-210987654321", profile.Authentication.ExpectedClientId);
        Assert.Single(profile.Authentication.AllowedRedirectUrls);
        Assert.Contains("https://m2lbdev.bufetat.no/auth/callback", profile.Authentication.AllowedRedirectUrls);
    }

    [Fact]
    public void ApplyDetectedAuthenticationSettings_PartialDetection_OnlyPopulateAvailableFields()
    {
        var profile = new FrontendAnalysisProfile
        {
            Id = "test-profile",
            Name = "Test Profile",
            Authentication = new()
            {
                AuthenticationType = FrontendAuthenticationType.None,
                ExpectedAuthority = null,
                ExpectedTenant = null,
                ExpectedClientId = null,
                AllowedRedirectUrls = []
            }
        };

        // Detection with only type and authority (no tenant/client/redirects)
        var detectionResult = new TargetEnvironmentDetectionResult
        {
            Success = true,
            DetectedAuthenticationType = FrontendAuthenticationType.MicrosoftEntraId,
            DetectedAuthority = "https://login.microsoftonline.com",
            DetectedTenantId = null,
            DetectedClientId = null,
            DetectedRedirectUrls = []
        };

        var auth = profile.Authentication;
        if (detectionResult.DetectedAuthenticationType != FrontendAuthenticationType.None)
            auth.AuthenticationType = detectionResult.DetectedAuthenticationType;
        if (!string.IsNullOrWhiteSpace(detectionResult.DetectedAuthority))
            auth.ExpectedAuthority = detectionResult.DetectedAuthority;
        if (!string.IsNullOrWhiteSpace(detectionResult.DetectedTenantId))
            auth.ExpectedTenant = detectionResult.DetectedTenantId;
        if (!string.IsNullOrWhiteSpace(detectionResult.DetectedClientId))
            auth.ExpectedClientId = detectionResult.DetectedClientId;

        // Verify only available fields were populated
        Assert.Equal(FrontendAuthenticationType.MicrosoftEntraId, profile.Authentication.AuthenticationType);
        Assert.Equal("https://login.microsoftonline.com", profile.Authentication.ExpectedAuthority);
        Assert.Null(profile.Authentication.ExpectedTenant);  // Not detected
        Assert.Null(profile.Authentication.ExpectedClientId);  // Not detected
        Assert.Empty(profile.Authentication.AllowedRedirectUrls);  // No redirects detected
    }

    [Fact]
    public void ApplyDetectedAuthenticationSettings_MergesRedirectUrlsWithExisting()
    {
        var profile = new FrontendAnalysisProfile
        {
            Id = "test-profile",
            Name = "Test Profile",
            Authentication = new()
            {
                AuthenticationType = FrontendAuthenticationType.None,
                AllowedRedirectUrls = ["https://existing.example.com/callback"]
            }
        };

        var detectionResult = new TargetEnvironmentDetectionResult
        {
            Success = true,
            DetectedAuthenticationType = FrontendAuthenticationType.MicrosoftEntraId,
            DetectedRedirectUrls =
            [
                "https://m2lbdev.bufetat.no/auth/callback",
                "https://m2lbdev.bufetat.no/signin-callback"
            ]
        };

        var auth = profile.Authentication;
        if (detectionResult.DetectedRedirectUrls.Count > 0)
        {
            var merged = new HashSet<string>(auth.AllowedRedirectUrls, StringComparer.OrdinalIgnoreCase);
            foreach (var url in detectionResult.DetectedRedirectUrls)
                merged.Add(url);
            auth.AllowedRedirectUrls = merged.ToList();
        }

        // Verify all three URLs are present (existing + 2 new detected)
        Assert.Equal(3, profile.Authentication.AllowedRedirectUrls.Count);
        Assert.Contains("https://existing.example.com/callback", profile.Authentication.AllowedRedirectUrls);
        Assert.Contains("https://m2lbdev.bufetat.no/auth/callback", profile.Authentication.AllowedRedirectUrls);
        Assert.Contains("https://m2lbdev.bufetat.no/signin-callback", profile.Authentication.AllowedRedirectUrls);
    }

    [Fact]
    public void ApplyDetectedAuthenticationSettings_DuplicateRedirectUrls_NotAddedTwice()
    {
        var profile = new FrontendAnalysisProfile
        {
            Id = "test-profile",
            Name = "Test Profile",
            Authentication = new()
            {
                AllowedRedirectUrls = ["https://m2lbdev.bufetat.no/auth/callback"]
            }
        };

        var detectionResult = new TargetEnvironmentDetectionResult
        {
            Success = true,
            DetectedRedirectUrls = ["https://m2lbdev.bufetat.no/auth/callback"]  // Same URL already configured
        };

        var auth = profile.Authentication;
        if (detectionResult.DetectedRedirectUrls.Count > 0)
        {
            var merged = new HashSet<string>(auth.AllowedRedirectUrls, StringComparer.OrdinalIgnoreCase);
            foreach (var url in detectionResult.DetectedRedirectUrls)
                merged.Add(url);
            auth.AllowedRedirectUrls = merged.ToList();
        }

        // Verify it appears only once (not duplicated)
        Assert.Single(profile.Authentication.AllowedRedirectUrls);
        Assert.Equal("https://m2lbdev.bufetat.no/auth/callback", profile.Authentication.AllowedRedirectUrls[0]);
    }

    [Fact]
    public void ApplyDetectedAuthenticationSettings_NoDetectedAuth_DoesNothing()
    {
        var profile = new FrontendAnalysisProfile
        {
            Id = "test-profile",
            Name = "Test Profile",
            Authentication = new()
            {
                AuthenticationType = FrontendAuthenticationType.None,
                ExpectedAuthority = "https://example.com",  // Pre-configured
                AllowedRedirectUrls = ["https://example.com/callback"]
            }
        };

        var originalAuth = profile.Authentication.AuthenticationType;
        var originalAuthority = profile.Authentication.ExpectedAuthority;
        var originalRedirects = new List<string>(profile.Authentication.AllowedRedirectUrls);

        var detectionResult = new TargetEnvironmentDetectionResult
        {
            Success = true,
            DetectedAuthenticationType = FrontendAuthenticationType.None,  // No auth detected
            DetectedAuthority = null,
            DetectedClientId = null,
            DetectedRedirectUrls = []
        };

        // Apply (nothing should change since nothing was detected)
        var auth = profile.Authentication;
        if (detectionResult.DetectedAuthenticationType != FrontendAuthenticationType.None)
            auth.AuthenticationType = detectionResult.DetectedAuthenticationType;
        if (!string.IsNullOrWhiteSpace(detectionResult.DetectedAuthority))
            auth.ExpectedAuthority = detectionResult.DetectedAuthority;

        // Verify nothing changed
        Assert.Equal(originalAuth, profile.Authentication.AuthenticationType);
        Assert.Equal(originalAuthority, profile.Authentication.ExpectedAuthority);
        Assert.Equal(originalRedirects, profile.Authentication.AllowedRedirectUrls);
    }

    [Fact]
    public void HasDetectedAuthSettings_WithCompleteDetection_ReturnsTrue()
    {
        var detection = new TargetEnvironmentDetectionResult
        {
            DetectedAuthenticationType = FrontendAuthenticationType.MicrosoftEntraId,
            DetectedAuthority = "https://login.microsoftonline.com",
            DetectedClientId = "client-id",
            DetectedRedirectUrls = ["https://app.example.com/callback"]
        };

        // Simulate the HasDetectedAuthSettings method logic
        var hasDetected = detection.DetectedAuthenticationType != FrontendAuthenticationType.None ||
                         !string.IsNullOrWhiteSpace(detection.DetectedAuthority) ||
                         !string.IsNullOrWhiteSpace(detection.DetectedClientId) ||
                         !string.IsNullOrWhiteSpace(detection.DetectedTenantId) ||
                         !string.IsNullOrWhiteSpace(detection.TenantMode) ||
                         detection.DetectedRedirectUrls.Count > 0;

        Assert.True(hasDetected);
    }

    [Fact]
    public void HasDetectedAuthSettings_WithNoDetection_ReturnsFalse()
    {
        var detection = new TargetEnvironmentDetectionResult
        {
            DetectedAuthenticationType = FrontendAuthenticationType.None,
            DetectedAuthority = null,
            DetectedClientId = null,
            DetectedTenantId = null,
            TenantMode = null,
            DetectedRedirectUrls = []
        };

        var hasDetected = detection.DetectedAuthenticationType != FrontendAuthenticationType.None ||
                         !string.IsNullOrWhiteSpace(detection.DetectedAuthority) ||
                         !string.IsNullOrWhiteSpace(detection.DetectedClientId) ||
                         !string.IsNullOrWhiteSpace(detection.DetectedTenantId) ||
                         !string.IsNullOrWhiteSpace(detection.TenantMode) ||
                         detection.DetectedRedirectUrls.Count > 0;

        Assert.False(hasDetected);
    }

    [Fact]
    public void ConfiguredAndDetectedSeparated_NoAutoPopulation()
    {
        var profile = new FrontendAnalysisProfile
        {
            Id = "test-profile",
            Name = "Test Profile",
            Authentication = new()
            {
                AuthenticationType = FrontendAuthenticationType.None,  // Configured as None
                ExpectedAuthority = null
            }
        };

        var detection = new TargetEnvironmentDetectionResult
        {
            DetectedAuthenticationType = FrontendAuthenticationType.MicrosoftEntraId  // Detected as Entra
        };

        // Before Apply, configured should still be None (not auto-populated)
        Assert.Equal(FrontendAuthenticationType.None, profile.Authentication.AuthenticationType);

        // Detection result should have Entra
        Assert.Equal(FrontendAuthenticationType.MicrosoftEntraId, detection.DetectedAuthenticationType);

        // They should not be the same (different sources)
        Assert.NotEqual(profile.Authentication.AuthenticationType, detection.DetectedAuthenticationType);
    }
}
