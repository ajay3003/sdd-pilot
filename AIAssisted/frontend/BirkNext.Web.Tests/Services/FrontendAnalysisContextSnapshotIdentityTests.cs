using BirkNext.LocalHttpsProxy;
using BirkNext.ManagedEdge;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;
using Microsoft.JSInterop;
using Moq;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// Review pages need the authenticated-review identity (saved testing method + proxy context fingerprint) and the manual
/// verification status of the SAVED profile. The context's profile copy is data-minimized (no tenant/client identifiers), so
/// deriving these from the copy produced a different fingerprint and the wrong method: the backend could not find the memory-only
/// proxy context and the Frontend Quality Review reported the CDP reason for a Local HTTPS proxy environment.
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
        profile.Authentication.ExpectedClientId = "FAKE-CLIENT-ID-SENTINEL";
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
    public async Task ReviewIdentity_MatchesSavedProfileMethodAndProxyFingerprint()
    {
        var profile = M2lbProfile();
        var context = await ContextFor(profile);

        var identity = ReviewAuthenticationIdentity.ForContext(context);
        var expected = ReviewAuthenticationIdentity.For(profile);

        identity.Method.Should().Be(AuthenticatedTestingMethod.LocalHttpsProxy);
        identity.ProfileId.Should().Be("dev");
        identity.ContextFingerprint.Should().Be(expected.ContextFingerprint,
            "the backend resolves the memory-only proxy context by this fingerprint; a mismatch hides an available authenticated context");
        identity.ContextFingerprint.Should().HaveLength(64, "a SHA-256 digest, never configuration values");
    }

    [Fact]
    public async Task ProfileCopy_IsDataMinimized_SoIdentityMustNotBeDerivedFromIt()
    {
        var profile = M2lbProfile();
        var context = await ContextFor(profile);

        var json = System.Text.Json.JsonSerializer.Serialize(context);
        json.Should().NotContain("FAKE-CLIENT-ID-SENTINEL").And.NotContain(profile.Authentication.ExpectedTenant);
        context.ActiveProfile.Authentication.AuthenticatedTestingMethod.Should().Be(AuthenticatedTestingMethod.LocalHttpsProxy, "the saved method itself is preserved");
        context.ActiveProfile.Authentication.VerificationMode.Should().Be(AuthenticationVerificationMode.ManualManagedEdge);
        context.ActiveProfile.Authentication.BrowserDeliveryTrust.Should().Be(ManagedEdgeTrustModel.ApprovedMcasProxyOrigin);
        ReviewAuthenticationIdentity.For(context.ActiveProfile).ContextFingerprint.Should().NotBe(context.ReviewIdentity!.ContextFingerprint,
            "this is exactly why the identity is computed by the factory and not from the copy");
    }

    [Fact]
    public async Task ManualVerification_ResolvedAgainstSavedProfile()
    {
        var profile = M2lbProfile();
        var context = await ContextFor(profile);

        context.ManualVerificationStatus.Should().Be(ManualAuthenticationVerificationStatus.Passed);
        context.ManualVerificationFingerprint.Should().Be(ManualAuthenticationVerificationEvidence.Fingerprint(profile));
    }

    [Fact]
    public async Task ManualVerification_RequiredWhenMethodIsManualOnlyAndNothingRecorded()
    {
        var profile = M2lbProfile();
        profile.ManualVerification = null;
        profile.Authentication.AuthenticatedTestingMethod = AuthenticatedTestingMethod.ManualOnly;
        profile.Authentication.VerificationMode = AuthenticationVerificationMode.Automated;

        (await ContextFor(profile)).ManualVerificationStatus.Should().Be(ManualAuthenticationVerificationStatus.Required);
    }

    [Fact]
    public void IdentityForContext_FallsBackToProfileCopyWhenFactoryIdentityAbsent()
    {
        var profile = new FrontendAnalysisProfile { Id = "dev", TargetUrl = "https://x.example.test/" };
        profile.Authentication.AuthenticatedTestingMethod = AuthenticatedTestingMethod.ManualOnly;
        var context = new FrontendAnalysisContext { ActiveProfile = profile };

        ReviewAuthenticationIdentity.ForContext(context).Should().Be(ReviewAuthenticationIdentity.For(profile));
        ReviewAuthenticationIdentity.ForContext(null).Method.Should().Be(AuthenticatedTestingMethod.ManagedEdgeCdp);
    }

    [Fact]
    public async Task ResolvedAccess_FromContext_ReportsProxyMethodAndSavedManualStatus()
    {
        var profile = M2lbProfile();
        var context = await ContextFor(profile);

        var access = FrontendQualityTargetAccess.FromContext(context);

        access.Method.Should().Be(AuthenticatedTestingMethod.LocalHttpsProxy);
        access.ManualVerificationStatus.Should().Be(ManualAuthenticationVerificationStatus.Passed);
        access.AuthenticatedBrowserDomAvailable.Should().BeFalse();
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
}
