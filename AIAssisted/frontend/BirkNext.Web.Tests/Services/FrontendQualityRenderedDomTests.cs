using BirkNext.BrowserCompanion;
using BirkNext.LocalHttpsProxy;
using BirkNext.ManagedEdge;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// Browser-rendered DOM as a semantic capability: any live provider can supply it (the authenticated review browser, a live approved
/// Browser Companion page in the user's managed Edge or the BirkNext-launched dedicated Edge, or — for public pages — the backend's
/// anonymous browser). The Local HTTPS proxy supplies authenticated API traffic and is never asked for a DOM. Recorded (historical)
/// Companion evidence never makes a DOM available now.
/// </summary>
public sealed class FrontendQualityRenderedDomTests
{
    private const string Origin = "https://m2lbdev.example.test";

    private static FrontendAnalysisContext Context(AuthenticatedTestingMethod method)
    {
        var profile = new FrontendAnalysisProfile { Id = "dev", Name = "M2LB DEV", EnvironmentType = FrontendEnvironmentType.Development, TargetUrl = Origin + "/" };
        profile.Authentication.AuthenticationType = FrontendAuthenticationType.MicrosoftEntraId;
        profile.Authentication.AuthenticatedTestingMethod = method;
        return new FrontendAnalysisContext
        {
            ActiveProfile = profile, TargetUrl = profile.TargetUrl!, FeatureToggles = profile.Features, EngineRequirements = profile.EngineRequirements,
            ReviewEngineSelection = profile.ReviewEngineSelection, AuthenticationType = FrontendAuthenticationType.MicrosoftEntraId,
        }.ForReviewScope(FrontendReviewAccessScope.PublicAndAuthenticated);
    }

    private static FrontendQualityTargetAccessContext ProxyReady(FrontendAnalysisContext context) =>
        FrontendQualityTargetAccess.Build(context, new AuthenticatedReviewCapabilities
        {
            Method = AuthenticatedTestingMethod.LocalHttpsProxy, ContextStatus = AuthenticatedApiContextStatus.Available, AuthenticatedApi = true, AuthenticatedRest = true,
        }, false, null, LocalHttpsProxyState.Ready);

    private static BrowserCompanionLiveSession LivePage(bool connected = true, string origin = Origin) => new()
    {
        ExtensionConnected = connected,
        LivePages = connected ? [new BrowserCompanionLivePage { Origin = origin, Route = "/skolenaervaer" }] : [],
    };

    private static FrontendQualityRenderedDom Dom(FrontendAnalysisContext context, FrontendQualityTargetAccessContext access, BrowserCompanionLiveSession? live) =>
        FrontendQualityCapabilityResolver.RenderedDom(
            FrontendQualityCapabilityResolver.Resolve(context, access, live, live is null ? null : [Origin]), includesAuthenticated: true);

    /// <summary>The reported state: proxy method, authenticated API ready, Dedicated Edge open on an approved page.</summary>
    [Fact]
    public void DedicatedEdgeLivePage_SuppliesTheDom_ThoughTheProxyMethodHasNone()
    {
        var context = Context(AuthenticatedTestingMethod.LocalHttpsProxy);
        var access = ProxyReady(context);
        var dom = Dom(context, access, LivePage());

        dom.Available.Should().BeTrue();
        dom.Provider.Should().Be(FrontendQualityCapabilityResolver.CompanionProvider);

        var rows = FrontendQualityLandingPresentation.Coverage(access, null, null,
            FrontendQualityCapabilityResolver.Resolve(context, access, LivePage(), [Origin]));
        rows.Single(r => r.Label == "Browser-rendered DOM").State.Should().Be(FrontendQualityCoverageState.Available);
        rows.Single(r => r.Label == "Browser-rendered DOM").Detail.Should().StartWith($"Via {FrontendQualityCapabilityResolver.CompanionProvider}");
        rows.Single(r => r.Label == "Authenticated API traffic").State.Should().Be(FrontendQualityCoverageState.Available);
        // The stale "requires a separate supported browser context" prerequisite is not repeated once a provider supplies pages.
        rows.Single(r => r.Label == "Authenticated frontend").Detail.Should().Contain("Rendered pages are available through the Browser Companion")
            .And.NotContain("require a separate supported browser context");
        rows.Should().OnlyContain(r => r.State == FrontendQualityCoverageState.Available);
        FrontendQualityLandingPresentation.CoverageSummary(rows).Headline.Should().Be("All configured review access paths are available");
    }

    [Fact]
    public void AuthenticatedReviewBrowser_IsPreferred_WhenBothProvidersAreLive_AndBothAreCandidates()
    {
        var context = Context(AuthenticatedTestingMethod.ManagedEdgeCdp);
        var access = FrontendQualityTargetAccess.Build(context, null, true, ManagedEdgeState.ConnectedAuthenticated, null);
        var dom = Dom(context, access, LivePage());

        dom.Provider.Should().Be(FrontendQualityCapabilityResolver.AuthenticatedBrowserProvider, "one deterministic provider, never two DOM rows");
        dom.Candidates.Where(c => c.State == FrontendQualityCapabilityAvailability.Available).Select(c => c.Kind)
            .Should().BeEquivalentTo([FrontendQualityCapabilityKind.AuthenticatedBrowserDom, FrontendQualityCapabilityKind.CompanionLiveDom]);
    }

    [Fact]
    public void Companion_SuppliesTheDom_WhenTheAuthenticatedBrowserIsNotSignedIn()
    {
        var context = Context(AuthenticatedTestingMethod.ManagedEdgeCdp);
        var dom = Dom(context, FrontendQualityTargetAccess.Build(context, null, false, null, null), LivePage());
        (dom.Available, dom.Provider).Should().Be((true, FrontendQualityCapabilityResolver.CompanionProvider));
    }

    [Fact]
    public void ProxyOnly_NoLiveBrowser_IsUnavailable_WithASemanticReason()
    {
        var context = Context(AuthenticatedTestingMethod.LocalHttpsProxy);
        var dom = Dom(context, ProxyReady(context), live: null);

        dom.Available.Should().BeFalse();
        dom.Detail.Should().StartWith(FrontendQualityCapabilityResolver.NoRenderedBrowserContext).And.Contain("Browser Companion is not connected");
        dom.Detail.Should().NotContain("Not available with the Local HTTPS proxy method");
    }

    /// <summary>A paired but disconnected Companion, or one showing an unapproved origin, is not a live DOM.</summary>
    [Fact]
    public void HistoricalOrUnapprovedCompanionPages_NeverCountAsALiveDom()
    {
        var context = Context(AuthenticatedTestingMethod.LocalHttpsProxy);
        Dom(context, ProxyReady(context), LivePage(connected: false)).Available.Should().BeFalse();
        Dom(context, ProxyReady(context), LivePage(origin: "https://other.example.test")).Available.Should().BeFalse();
    }

    /// <summary>Access capabilities never contribute to the engine summary: a missing DOM does not add an unavailable optional engine.</summary>
    [Fact]
    public void AccessRows_AreNeverEngines()
    {
        var context = Context(AuthenticatedTestingMethod.LocalHttpsProxy);
        var rows = FrontendQualityLandingPresentation.Coverage(ProxyReady(context), null);
        rows.Select(r => r.Label).Should().BeSubsetOf(["Public frontend", "Authenticated frontend", "Browser-rendered DOM", "Authenticated API traffic"]);
    }
}
