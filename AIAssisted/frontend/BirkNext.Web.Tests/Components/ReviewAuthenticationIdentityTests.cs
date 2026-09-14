using BirkNext.LocalHttpsProxy;
using BirkNext.Web.Models;

namespace BirkNext.Web.Tests.Components;

/// <summary>Reviews pass a non-secret identity (method + profile + fingerprint) so the backend can resolve the memory-only context.</summary>
public sealed class ReviewAuthenticationIdentityTests
{
    [Fact]
    public void ForProfileCarriesMethodProfileIdAndProxyFingerprint()
    {
        var profile = new FrontendAnalysisProfile { Id = "dev", TargetUrl = "https://m2lbdev.bufetat.no/" };
        profile.Authentication.AuthenticatedTestingMethod = AuthenticatedTestingMethod.LocalHttpsProxy;
        var identity = ReviewAuthenticationIdentity.For(profile);
        Assert.Equal(AuthenticatedTestingMethod.LocalHttpsProxy, identity.Method);
        Assert.Equal("dev", identity.ProfileId);
        Assert.Equal(LocalHttpsProxyScope.Fingerprint(profile), identity.ContextFingerprint);
        Assert.Equal(64, identity.ContextFingerprint!.Length);
        Assert.DoesNotContain("eyJ", System.Text.Json.JsonSerializer.Serialize(identity));
    }

    [Fact]
    public void ForNullProfileFallsBackToCdpWithNoIdentity()
    {
        var identity = ReviewAuthenticationIdentity.For(null);
        Assert.Equal(AuthenticatedTestingMethod.ManagedEdgeCdp, identity.Method);
        Assert.Null(identity.ProfileId);
        Assert.Null(identity.ContextFingerprint);
    }

    [Fact]
    public void FingerprintTracksTargetIdentityChanges()
    {
        var profile = new FrontendAnalysisProfile { Id = "dev", TargetUrl = "https://a.example.test/" };
        var before = ReviewAuthenticationIdentity.For(profile).ContextFingerprint;
        profile.TargetUrl = "https://b.example.test/";
        Assert.NotEqual(before, ReviewAuthenticationIdentity.For(profile).ContextFingerprint);
    }
}
