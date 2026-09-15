using BirkNext.LocalHttpsProxy;
using BirkNext.ManagedEdge;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;
using Microsoft.JSInterop;
using Moq;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// The safe profile snapshot handed to review pages must preserve the full non-secret authentication configuration. The
/// authenticated-review identity (method + proxy fingerprint) and the manual-verification fingerprint are derived from it, so a
/// lossy copy made the backend look for a proxy context under the wrong fingerprint and reported the wrong testing method.
/// </summary>
public sealed class FrontendAnalysisContextSnapshotIdentityTests
{
    private static FrontendAnalysisProfile M2lbProfile()
    {
        var profile = new FrontendAnalysisProfile
        {
            Id = "dev", Name = "M2LB DEV", EnvironmentType = FrontendEnvironmentType.Development,
            TargetUrl = "https://m2lbdev.example.test/", RestBaseUrl = "https://api-dev.example.test/", GraphQlEndpoint = "https://api-dev.example.test/graphql",
            HealthEndpoint = "https://api-dev.example.test/health", Performance = new(), CoreWebVitals = new(), Security = new(), Features = new(),
        };
        profile.Authentication.RequiresAuthentication = true;
        profile.Authentication.AuthenticationType = FrontendAuthenticationType.MicrosoftEntraId;
        profile.Authentication.AuthenticatedTestingMethod = AuthenticatedTestingMethod.LocalHttpsProxy;
        profile.Authentication.ExpectedTenant = "11111111-1111-1111-1111-111111111111";
        profile.Authentication.ExpectedClientId = "FAKE-CLIENT-ID";
        profile.Authentication.ExpectedAuthority = "https://login.microsoftonline.com/11111111-1111-1111-1111-111111111111";
        profile.Authentication.VerificationMode = AuthenticationVerificationMode.ManualManagedEdge;
        profile.Authentication.BrowserDeliveryTrust = ManagedEdgeTrustModel.ApprovedMcasProxyOrigin;
        profile.ManualVerification = ManualAuthenticationVerificationEvidence.Record(profile, ManualAuthenticationVerificationStatus.Passed);
        return profile;
    }

    private static async Task<FrontendAnalysisContext> ContextFor(FrontendAnalysisProfile profile)
    {
        var settings = new Mock<IFrontendAnalysisSettingsService>();
        settings.Setup(s => s.ActiveProfile).Returns(profile);
        settings.Setup(s => s.Settings).Returns(new FrontendAnalysisSettings { Profiles = [profile], ActiveProfileId = profile.Id });
        settings.Setup(s => s.ValidateProfile(It.IsAny<FrontendAnalysisProfile>())).Returns(new ProfileValidationResult());
        settings.Setup(s => s.LoadAsync(It.IsAny<IJSRuntime>())).Returns(Task.CompletedTask);
        var factory = new FrontendAnalysisContextFactory(settings.Object, new PlaceholderAuthenticatedBrowserSessionService(settings.Object), new Mock<IJSRuntime>().Object);
        return await factory.GetActiveContextAsync();
    }

    [Fact]
    public async Task Snapshot_PreservesAuthenticatedTestingMethodAndProxyFingerprint()
    {
        var profile = M2lbProfile();
        var context = await ContextFor(profile);

        var fromSnapshot = ReviewAuthenticationIdentity.For(context.ActiveProfile);
        var fromProfile = ReviewAuthenticationIdentity.For(profile);

        fromSnapshot.Method.Should().Be(AuthenticatedTestingMethod.LocalHttpsProxy);
        fromSnapshot.ProfileId.Should().Be("dev");
        fromSnapshot.ContextFingerprint.Should().Be(fromProfile.ContextFingerprint,
            "the backend resolves the memory-only proxy context by this fingerprint; a mismatch hides an available authenticated context");
        LocalHttpsProxyScope.Fingerprint(context.ActiveProfile).Should().Be(LocalHttpsProxyScope.Fingerprint(profile));
    }

    [Fact]
    public async Task Snapshot_PreservesManualVerificationAndItsFingerprint()
    {
        var profile = M2lbProfile();
        var context = await ContextFor(profile);

        ManualAuthenticationVerificationEvidence.Fingerprint(context.ActiveProfile).Should().Be(ManualAuthenticationVerificationEvidence.Fingerprint(profile));
        context.ActiveProfile.ManualVerification.Should().NotBeNull();
        context.ActiveProfile.ManualVerification!.StatusFor(context.ActiveProfile).Should().Be(ManualAuthenticationVerificationStatus.Passed);
    }

    [Fact]
    public async Task Snapshot_PreservesEveryNonSecretAuthenticationField()
    {
        var profile = M2lbProfile();
        var context = await ContextFor(profile);
        var copy = context.ActiveProfile.Authentication;

        copy.VerificationMode.Should().Be(AuthenticationVerificationMode.ManualManagedEdge);
        copy.ExpectedTenant.Should().Be(profile.Authentication.ExpectedTenant);
        copy.ExpectedClientId.Should().Be(profile.Authentication.ExpectedClientId);
        copy.BrowserDeliveryTrust.Should().Be(ManagedEdgeTrustModel.ApprovedMcasProxyOrigin);
        copy.AuthenticatedTestingMethod.Should().Be(AuthenticatedTestingMethod.LocalHttpsProxy);
        copy.Should().NotBeSameAs(profile.Authentication, "the snapshot is a copy, not the live settings object");
    }

    [Fact]
    public async Task Snapshot_StillNeverCarriesApiSecrets()
    {
        var profile = M2lbProfile();
        profile.ApiAuth.BearerToken = "eyJhbGciOiJSUzI1NiJ9.FAKE.SIGNATURE";
        profile.ApiAuth.ApiKey = "FAKE-API-KEY";
        var context = await ContextFor(profile);

        var json = System.Text.Json.JsonSerializer.Serialize(context.ActiveProfile);
        json.Should().NotContain("eyJhbGciOiJSUzI1NiJ9").And.NotContain("FAKE-API-KEY");
    }

    [Fact]
    public async Task ResolvedAccess_FromSnapshot_ReportsProxyMethodNotCdpDefault()
    {
        var profile = M2lbProfile();
        var context = await ContextFor(profile);

        var access = FrontendQualityTargetAccess.FromContext(context);

        access.Method.Should().Be(AuthenticatedTestingMethod.LocalHttpsProxy);
        access.ManualVerificationStatus.Should().Be(ManualAuthenticationVerificationStatus.Passed);
        access.AuthenticatedBrowserDomAvailable.Should().BeFalse();
    }
}
