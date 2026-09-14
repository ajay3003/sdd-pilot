using BirkNext.LocalHttpsProxy;
using BirkNext.Web.Components;
using BirkNext.Web.Models;
using Bunit;

namespace BirkNext.Web.Tests.Components;

/// <summary>The shared review auth-status block shows non-secret capability flags and provenance, and never a token.</summary>
public sealed class AuthenticatedReviewStatusTests : BunitContext
{
    private string Row(IRenderedComponent<AuthenticatedReviewStatus> cut, string id) => cut.Find($"[data-testid='{id}']").TextContent.Trim();

    private IRenderedComponent<AuthenticatedReviewStatus> Render(AuthenticatedReviewCapabilities caps, bool browser = false) =>
        Render<AuthenticatedReviewStatus>(p => p.Add(x => x.Capabilities, caps).Add(x => x.ShowBrowserSurfaces, browser));

    [Fact]
    public void ProxyAvailableShowsRestAndGraphQlAvailableApiOnly()
    {
        var cut = Render(new AuthenticatedReviewCapabilities
        {
            Method = AuthenticatedTestingMethod.LocalHttpsProxy, ContextStatus = AuthenticatedApiContextStatus.Available,
            PublicApi = true, AuthenticatedApi = true, AuthenticatedRest = true, AuthenticatedGraphQlQuery = true,
            ObservedHost = "api.example.test", ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(20), Reason = "Authenticated via Local HTTPS Proxy"
        }, browser: true);
        Assert.Equal(AuthenticatedTestingMethodLabels.ProxyOption, Row(cut, "ars-method"));
        Assert.Equal("Available — memory only", Row(cut, "ars-context-status"));
        Assert.Equal("Available", Row(cut, "ars-rest"));
        Assert.Equal("Available", Row(cut, "ars-graphql"));
        Assert.Equal("Unavailable", Row(cut, "ars-dom"));
        Assert.Equal("Unavailable", Row(cut, "ars-runtime"));
        Assert.Equal("api.example.test", Row(cut, "ars-host"));
        Assert.Contains("Local HTTPS Proxy", Row(cut, "ars-reason"));
        // Never a token anywhere in the markup.
        foreach (var forbidden in new[] { "eyJ", "Bearer ", "Authorization" }) Assert.DoesNotContain(forbidden, cut.Markup);
    }

    [Fact]
    public void ProxyExpiredShowsExpiredAndUnavailableWithRefreshGuidance()
    {
        var cut = Render(new AuthenticatedReviewCapabilities
        {
            Method = AuthenticatedTestingMethod.LocalHttpsProxy, ContextStatus = AuthenticatedApiContextStatus.Expired,
            PublicApi = true, AuthenticatedApi = false, AuthenticatedRest = false, AuthenticatedGraphQlQuery = false,
            Reason = "Authenticated API session expired. Continue using the target application in the proxy-configured browser to refresh the session."
        });
        Assert.Equal("Expired", Row(cut, "ars-context-status"));
        Assert.Equal("Unavailable", Row(cut, "ars-rest"));
        Assert.Equal("Unavailable", Row(cut, "ars-graphql"));
        Assert.Contains("refresh", Row(cut, "ars-reason"), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProxyWaitingPromptsToStartProxy()
    {
        var cut = Render(new AuthenticatedReviewCapabilities
        {
            Method = AuthenticatedTestingMethod.LocalHttpsProxy, ContextStatus = AuthenticatedApiContextStatus.WaitingForAuthenticatedTraffic,
            PublicApi = true, Reason = "Start the local proxy, sign in to the target application in the proxy-configured browser, and perform an authenticated action."
        });
        Assert.Equal("Waiting for authenticated traffic", Row(cut, "ars-context-status"));
        Assert.Contains("Start the local proxy", Row(cut, "ars-reason"));
    }

    [Fact]
    public void CdpAndManualNeverClaimAuthenticatedApi()
    {
        foreach (var method in new[] { AuthenticatedTestingMethod.ManagedEdgeCdp, AuthenticatedTestingMethod.ManualOnly })
        {
            var cut = Render(new AuthenticatedReviewCapabilities { Method = method, ContextStatus = AuthenticatedApiContextStatus.NotApplicable, PublicApi = true, Reason = "n/a" });
            Assert.Equal("Unavailable", Row(cut, "ars-rest"));
            Assert.Equal("Unavailable", Row(cut, "ars-graphql"));
            Assert.Equal("Not available", Row(cut, "ars-session"));
        }
    }

    [Fact]
    public void ManageAuthenticationLinkPointsToTargetEnvironments()
    {
        var cut = Render(new AuthenticatedReviewCapabilities());
        Assert.Contains("system-settings", cut.Find("[data-testid='ars-manage']").GetAttribute("href"));
    }
}
