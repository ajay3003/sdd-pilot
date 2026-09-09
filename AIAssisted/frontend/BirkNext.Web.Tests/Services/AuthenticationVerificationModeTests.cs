using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;
using Xunit;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// Tests for authentication verification mode semantics.
/// Ensures ManualManagedEdge and Automated modes are properly distinguished.
/// </summary>
public sealed class AuthenticationVerificationModeTests
{
    private readonly FrontendAnalysisSettingsService _sut = new();

    [Fact]
    public void CreateProfile_DefaultVerificationMode_IsNotManualManagedEdge()
    {
        var profile = _sut.CreateProfile("Test", FrontendEnvironmentType.Development);

        // Default should be Automated, not ManualManagedEdge
        profile.Authentication.VerificationMode.Should().Be(AuthenticationVerificationMode.Automated);
    }

    [Fact]
    public void CreateProfile_ConfiguredAuthenticationType_StartsAsNone()
    {
        var profile = _sut.CreateProfile("Test", FrontendEnvironmentType.Development);

        profile.Authentication.AuthenticationType.Should().Be(FrontendAuthenticationType.None);
    }

    [Fact]
    public void UpdateProfile_PreservesVerificationMode()
    {
        var profile = _sut.CreateProfile("Test", FrontendEnvironmentType.Development);
        profile.Authentication.VerificationMode = AuthenticationVerificationMode.ManualManagedEdge;
        profile.Authentication.AuthenticationType = FrontendAuthenticationType.MicrosoftEntraId;

        _sut.UpdateProfile(profile);
        var retrieved = _sut.Settings.Profiles.First();

        retrieved.Authentication.VerificationMode.Should().Be(AuthenticationVerificationMode.ManualManagedEdge);
        retrieved.Authentication.AuthenticationType.Should().Be(FrontendAuthenticationType.MicrosoftEntraId);
    }

    [Fact]
    public void CreateProfile_InitialAutomationFlags_AllFalse()
    {
        var profile = _sut.CreateProfile("Test", FrontendEnvironmentType.Development);

        profile.Authentication.RequiresAuthentication.Should().BeFalse();
        profile.Authentication.UseExistingBrowserSession.Should().BeFalse();
        profile.Authentication.AutomaticallyOpenLoginPage.Should().BeFalse();
    }

    [Fact]
    public void ConfiguredAuthentication_ManualVerificationRemainsSeparate()
    {
        var profile = _sut.CreateProfile("Test", FrontendEnvironmentType.Development);
        profile.ManualVerification = new ManualAuthenticationVerificationEvidence
        {
            Result = ManualAuthenticationVerificationStatus.Passed,
            VerifiedAt = System.DateTime.UtcNow,
            Origin = "Manual verification passed by user"
        };

        _sut.UpdateProfile(profile);
        var retrieved = _sut.Settings.Profiles.First();

        // Manual verification should be independent of configured authentication
        retrieved.ManualVerification.Should().NotBeNull();
        retrieved.Authentication.AuthenticationType.Should().Be(FrontendAuthenticationType.None);
    }
}
