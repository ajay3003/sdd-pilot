using BirkNext.LocalHttpsProxy;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// The capability-aware default review scope: one resolver, fed by the same access decisions and capability rows that scope a run.
/// Authenticated readiness is the testing context plus an engine that can use it — never "authentication is configured".
/// </summary>
public sealed class FrontendQualityScopeRecommendationTests
{
    private const string Url = "https://m2lbdev.example.test/";

    [Theory]
    [InlineData(true, true, FrontendReviewAccessScope.PublicAndAuthenticated)]
    [InlineData(true, false, FrontendReviewAccessScope.PublicOnly)]
    [InlineData(false, true, FrontendReviewAccessScope.AuthenticatedOnly)]
    [InlineData(false, false, null)]
    public void DefaultMatrix(bool publicAvailable, bool authenticatedReady, FrontendReviewAccessScope? expected) =>
        FrontendQualityReviewScopes.Recommended(publicAvailable, authenticatedReady).Should().Be(expected);

    private static FrontendAnalysisContext Context(
        AuthenticatedTestingMethod method = AuthenticatedTestingMethod.ManagedEdgeCdp,
        FrontendAuthenticationType auth = FrontendAuthenticationType.MicrosoftEntraId,
        bool requiresAuthentication = false,
        string? url = Url,
        string id = "dev")
    {
        var profile = new FrontendAnalysisProfile { Id = id, Name = "M2LB DEV", EnvironmentType = FrontendEnvironmentType.Development, TargetUrl = url };
        profile.Authentication.AuthenticationType = auth;
        profile.Authentication.RequiresAuthentication = requiresAuthentication;
        profile.Authentication.AuthenticatedTestingMethod = method;
        profile.Features.EnableBrowserRuntimeEngine = false;
        profile.Features.EnableLighthouseEngine = false;
        profile.Features.EnablePassiveSecurityEngine = false;
        profile.Features.EnableBrowserQualityEngine = false;
        profile.Features.EnablePerformanceQualityEngine = false;
        return new FrontendAnalysisContext
        {
            ActiveProfile = profile, TargetUrl = url ?? "", FeatureToggles = profile.Features,
            EngineRequirements = profile.EngineRequirements, ReviewEngineSelection = profile.ReviewEngineSelection,
            RequiresAuthentication = requiresAuthentication, AuthenticationType = auth,
        };
    }

    private static readonly FrontendQualityEngineStatusReportDto Status = new()
    {
        Engines =
        [
            new FrontendQualityEngineStatusDto
            {
                EngineId = FrontendQualityEngineIdDto.Accessibility, DisplayName = "Accessibility", Layer1Allowed = true, Layer2Enabled = true,
                AuthModeSupported = true, Available = true,
                Layer3Readiness = new FrontendQualityEngineReadinessDto { EngineId = FrontendQualityEngineIdDto.Accessibility, IsAvailable = true },
            },
        ],
    };

    private static IReadOnlyList<FrontendQualityCapabilityRow> Rows(FrontendAnalysisContext context, FrontendQualityTargetAccessContext access) =>
        FrontendQualityLandingPresentation.Capabilities(context, FrontendQualityActiveEngines.Resolve(context), Status, false, false, access, null, null);

    private static FrontendQualityScopeRecommendation Recommend(FrontendAnalysisContext context, FrontendQualityTargetAccessContext access) =>
        FrontendQualityReviewScopes.Recommend(context, access, Rows);

    [Fact]
    public void ConfiguredButTestingContextNotReady_RecommendsPublicOnly()
    {
        var context = Context();
        var recommendation = Recommend(context, FrontendQualityTargetAccess.Build(context, null, authenticatedBrowserSessionAvailable: false, null, null));

        recommendation.PublicAvailable.Should().BeTrue();
        recommendation.AuthenticatedReady.Should().BeFalse("a configured provider without a ready testing context is not authenticated access");
        recommendation.Scope.Should().Be(FrontendReviewAccessScope.PublicOnly);
    }

    [Fact]
    public void ConfiguredAndBrowserTestingContextReady_RecommendsPublicAndAuthenticated()
    {
        var context = Context();
        var recommendation = Recommend(context, FrontendQualityTargetAccess.Build(context, null, authenticatedBrowserSessionAvailable: true, null, null));

        recommendation.AuthenticatedReady.Should().BeTrue();
        recommendation.Scope.Should().Be(FrontendReviewAccessScope.PublicAndAuthenticated);
    }

    /// <summary>
    /// The reported screen: Development, Microsoft Entra ID configured, testing context Ready (Local HTTPS proxy API context),
    /// verification not needed, public frontend available — and the sign-in rule off, which used to force Public only.
    /// </summary>
    [Fact]
    public void ReportedState_ProxyContextReady_RecommendsPublicAndAuthenticated()
    {
        var context = Context(AuthenticatedTestingMethod.LocalHttpsProxy);
        var access = FrontendQualityTargetAccess.Build(context, new AuthenticatedReviewCapabilities
        {
            Method = AuthenticatedTestingMethod.LocalHttpsProxy, ContextStatus = AuthenticatedApiContextStatus.Available, AuthenticatedApi = true,
        }, false, null, LocalHttpsProxyState.Ready);

        FrontendQualityReviewScopes.TestingContextLabel(access).Should().Be("Authenticated API context available");
        FrontendQualityReviewScopes.VerificationLabel(access).Should().NotContain("unavailable", "\"Verification: Not needed\" is not authentication unavailable");
        Recommend(context, access).Scope.Should().Be(FrontendReviewAccessScope.PublicAndAuthenticated);
    }

    [Fact]
    public void NoProviderConfigured_IsNeverAuthenticatedReady_EvenWithASession()
    {
        var context = Context(auth: FrontendAuthenticationType.None);
        var recommendation = Recommend(context, FrontendQualityTargetAccess.Build(context, null, authenticatedBrowserSessionAvailable: true, null, null));

        recommendation.AuthenticatedReady.Should().BeFalse();
        recommendation.Scope.Should().Be(FrontendReviewAccessScope.PublicOnly);
    }

    [Fact]
    public void NeitherPathUsable_RecommendsNoScope()
    {
        var context = Context(url: null);
        var recommendation = Recommend(context, FrontendQualityTargetAccess.Build(context, null, false, null, null));

        recommendation.PublicAvailable.Should().BeFalse("public availability is never inferred from a URL alone, and here there is none");
        recommendation.Scope.Should().BeNull();
        FrontendQualityReviewScopes.RecommendationSentence(recommendation).Should().Be(FrontendQualityReviewScopes.NoRunnableScope);
    }

    [Fact]
    public void Recommendation_DoesNotDependOnTheScopeTheAccessWasResolvedFor()
    {
        var context = Context();
        var publicAccess = FrontendQualityTargetAccess.Build(context.ForReviewScope(FrontendReviewAccessScope.PublicOnly), null, true, null, null);
        var combinedAccess = FrontendQualityTargetAccess.Build(context.ForReviewScope(FrontendReviewAccessScope.PublicAndAuthenticated), null, true, null, null);

        Recommend(context, publicAccess).Should().Be(Recommend(context, combinedAccess));
    }

    [Fact]
    public void ForScope_MatchesBuild_ForEveryScope()
    {
        var context = Context();
        var built = FrontendQualityTargetAccess.Build(context.ForReviewScope(FrontendReviewAccessScope.PublicOnly), null, true, null, null);
        foreach (var scope in Enum.GetValues<FrontendReviewAccessScope>())
            FrontendQualityTargetAccess.ForScope(built, scope).Should().BeEquivalentTo(
                FrontendQualityTargetAccess.Build(context.ForReviewScope(scope), null, true, null, null));
    }

    /// <summary>Changing Target Environment re-evaluates: the recommendation is per target and carries its profile id.</summary>
    [Fact]
    public void TargetChange_ReEvaluatesForTheNewTarget()
    {
        var a = Context(id: "a");
        var b = Context(id: "b");
        var ready = Recommend(a, FrontendQualityTargetAccess.Build(a, null, true, null, null));
        var publicOnly = Recommend(b, FrontendQualityTargetAccess.Build(b, null, false, null, null));

        (ready.ProfileId, ready.Scope).Should().Be(("a", FrontendReviewAccessScope.PublicAndAuthenticated));
        (publicOnly.ProfileId, publicOnly.Scope).Should().Be(("b", FrontendReviewAccessScope.PublicOnly));
    }

    [Theory]
    [InlineData(FrontendReviewAccessScope.PublicOnly, true, FrontendQualityReviewScopes.AuthenticatedNowAvailable)]
    [InlineData(FrontendReviewAccessScope.PublicOnly, false, null)]
    [InlineData(FrontendReviewAccessScope.PublicAndAuthenticated, false, FrontendQualityReviewScopes.ScopeNeedsReview)]
    [InlineData(FrontendReviewAccessScope.AuthenticatedOnly, false, FrontendQualityReviewScopes.ScopeNeedsReview)]
    [InlineData(FrontendReviewAccessScope.PublicAndAuthenticated, true, null)]
    public void ExplicitScope_GetsANoticeAndIsNeverChanged(FrontendReviewAccessScope chosen, bool authenticatedReady, string? notice)
    {
        var recommendation = new FrontendQualityScopeRecommendation("dev", true, authenticatedReady,
            FrontendQualityReviewScopes.Recommended(true, authenticatedReady));
        FrontendQualityReviewScopes.ExplicitScopeNotice(chosen, recommendation).Should().Be(notice);
        FrontendQualityReviewScopes.ExplicitScopeNotice(null, recommendation).Should().BeNull("a recommended scope follows the state; only a chosen one gets a notice");
    }
}
