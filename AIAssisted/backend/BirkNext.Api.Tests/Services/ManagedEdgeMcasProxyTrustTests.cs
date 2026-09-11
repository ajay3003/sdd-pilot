using BirkNext.Api.Services.ManagedEdge;
using BirkNext.Api.Services.AuthenticatedReview;
using BirkNext.ManagedEdge;
using Microsoft.Extensions.Options;
using Moq;

namespace BirkNext.Api.Tests.Services;

public sealed class ManagedEdgeMcasProxyTrustTests
{
    private const string Origin = "https://m2lbdev.bufetat.no";
    private const string ProxyOrigin = "https://m2lbdev-bufetat-no.access.mcas.ms";
    private readonly Mock<IManagedEdgeConnector> _connector = new(MockBehavior.Strict);
    private readonly Mock<IManagedEdgeBrowser> _browser = new();
    private readonly Mock<IManagedEdgePage> _exact = new();
    private readonly Mock<IManagedEdgePage> _proxy = new();
    private readonly ManagedEdgeTargetRule _rule = new() { Origin = Origin, AuthenticatedOnlySelector = "#signed-in-only", ProtectedGetPath = "/api/me", SafeGetPaths = ["/api/me"] };

    public ManagedEdgeMcasProxyTrustTests()
    {
        Configure(_exact, Origin + "/home");
        Configure(_proxy, ProxyOrigin + "/aad_login");
        _proxy.Setup(p => p.GetLocationOriginAsync()).ReturnsAsync(ProxyOrigin);
        _proxy.Setup(p => p.GetNavigationOriginsAsync()).ReturnsAsync(new[] { Origin, "https://login.microsoftonline.com", ProxyOrigin });
        _proxy.Setup(p => p.HasAuthenticatedElementAsync(ProxyOrigin, "#signed-in-only")).ReturnsAsync(true);
        _proxy.Setup(p => p.FetchAsync(ProxyOrigin, "/api/me", null)).ReturnsAsync(new ManagedEdgeProbeResult(200, "application/json", 5));
        _browser.SetupGet(b => b.IsConnected).Returns(true);
        _browser.SetupGet(b => b.ContextCount).Returns(1);
        _browser.SetupGet(b => b.DiscoveredPageUrls).Returns(Array.Empty<string>());
        _connector.Setup(c => c.ConnectAsync("http://127.0.0.1:9222", It.IsAny<CancellationToken>())).ReturnsAsync(_browser.Object);
    }

    private static void Configure(Mock<IManagedEdgePage> page, string url)
    {
        page.SetupGet(p => p.Url).Returns(url);
        page.SetupGet(p => p.IsClosed).Returns(false);
        page.SetupGet(p => p.NavigationChanged).Returns(false);
    }

    private ManagedEdgeCdpService Service() => new(_connector.Object, Options.Create(new ManagedEdgeOptions { Targets = [_rule] }),
        Options.Create(new AuthenticatedReviewOptions { Enabled = true, Runtime = "LocalWorkstation" }));
    private static ManagedEdgeConnectRequest Request(ManagedEdgeTrustModel trust) => new("dev", Origin + "/", new string('A', 64), trust);
    private ManagedEdgeSessionRequest Owner(ManagedEdgeStatus s) => new(s.SessionId!, "dev", new string('A', 64));

    [Fact]
    public void CorrelationRequiresApplicationIdentityNotJustSuffix()
    {
        Assert.True(ManagedEdgePolicy.IsCorrelatedMcasProxyOrigin(ProxyOrigin + "/x", Origin));
        Assert.True(ManagedEdgePolicy.IsCorrelatedMcasProxyOrigin("https://m2lbdev-bufetat-no-eu2.access.mcas.ms/x", Origin));
        Assert.False(ManagedEdgePolicy.IsCorrelatedMcasProxyOrigin("https://evil.access.mcas.ms/x", Origin));
        Assert.False(ManagedEdgePolicy.IsCorrelatedMcasProxyOrigin("https://other-tenant.access.mcas.ms/x", Origin));
        Assert.False(ManagedEdgePolicy.IsCorrelatedMcasProxyOrigin("http://m2lbdev-bufetat-no.access.mcas.ms/x", Origin));
        Assert.False(ManagedEdgePolicy.IsCorrelatedMcasProxyOrigin("https://m2lbdev-bufetat-no.access.mcas.ms.evil.test/x", Origin));
        Assert.False(ManagedEdgePolicy.IsCorrelatedMcasProxyOrigin("https://m2lbdev.bufetat.no/", Origin));
    }

    [Fact]
    public async Task ExactOriginTrustNeverConnectsToProxyEvenWhenPresent()
    {
        _browser.SetupGet(b => b.Pages).Returns(new[] { _proxy.Object });
        using var service = Service();
        var status = await service.ConnectAsync(Request(ManagedEdgeTrustModel.ExactOrigin));
        Assert.Equal(ManagedEdgeState.TargetTabNotFound, status.State);
        Assert.Null(status.SessionId);
        Assert.Contains("enable approved MCAS proxy trust", status.Evidence);
        _browser.Verify(b => b.Dispose(), Times.Once);
    }

    [Fact]
    public async Task ApprovedProxyTrustConnectsWhenCorrelatedAndKeepsTargetSeparateFromDelivery()
    {
        _browser.SetupGet(b => b.Pages).Returns(new[] { _proxy.Object });
        using var service = Service();
        var status = await service.ConnectAsync(Request(ManagedEdgeTrustModel.ApprovedMcasProxyOrigin));
        Assert.Equal(ManagedEdgeState.ConnectedUnproven, status.State);
        Assert.True(status.OriginMatched);
        Assert.Equal(Origin, status.TargetOrigin);          // Frontend URL / target unchanged
        Assert.Equal(ProxyOrigin, status.DeliveryOrigin);   // proxy is only the delivery origin
        Assert.True(status.ProxiedDelivery);
        Assert.Equal(ManagedEdgeTrustModel.ApprovedMcasProxyOrigin, status.TrustModel);
        Assert.Equal("Microsoft Defender for Cloud Apps proxy", status.BrowserDelivery);
        Assert.NotNull(status.CorrelationEvidence);
        Assert.Contains("navigation history includes", status.CorrelationEvidence);
    }

    [Fact]
    public async Task ExactOriginTabWinsOverProxyEvenUnderProxyTrust()
    {
        _browser.SetupGet(b => b.Pages).Returns(new[] { _proxy.Object, _exact.Object });
        using var service = Service();
        var status = await service.ConnectAsync(Request(ManagedEdgeTrustModel.ApprovedMcasProxyOrigin));
        Assert.Equal(ManagedEdgeState.ConnectedUnproven, status.State);
        Assert.Equal(Origin, status.DeliveryOrigin);
        Assert.False(status.ProxiedDelivery);
        Assert.Equal(ManagedEdgeTrustModel.ExactOrigin, status.TrustModel);
    }

    [Theory]
    [InlineData("no-live")]
    [InlineData("wrong-live")]
    [InlineData("no-flow")]
    public async Task UncorrelatedProxyIsRefusedAndNeverAuthenticated(string defect)
    {
        if (defect == "no-live") _proxy.Setup(p => p.GetLocationOriginAsync()).ReturnsAsync((string?)null);
        if (defect == "wrong-live") _proxy.Setup(p => p.GetLocationOriginAsync()).ReturnsAsync("https://someone-else.access.mcas.ms");
        if (defect == "no-flow") _proxy.Setup(p => p.GetNavigationOriginsAsync()).ReturnsAsync(new[] { ProxyOrigin });
        _browser.SetupGet(b => b.Pages).Returns(new[] { _proxy.Object });
        using var service = Service();
        var status = await service.ConnectAsync(Request(ManagedEdgeTrustModel.ApprovedMcasProxyOrigin));
        Assert.Equal(ManagedEdgeState.ProxiedDeliveryUncorrelated, status.State);
        Assert.Null(status.SessionId);
        Assert.False(status.AuthenticatedBrowserAvailable);
        Assert.Contains("could not correlate", status.Evidence);
        _browser.Verify(b => b.Dispose(), Times.Once);
    }

    [Fact]
    public async Task ArbitraryMcasOriginIsNeverCorrelatedEvenWithProxyTrust()
    {
        Configure(_proxy, "https://unrelated-app.access.mcas.ms/home");
        _browser.SetupGet(b => b.Pages).Returns(new[] { _proxy.Object });
        using var service = Service();
        var status = await service.ConnectAsync(Request(ManagedEdgeTrustModel.ApprovedMcasProxyOrigin));
        // Not application-identity correlated at all, so it is not even a proxy candidate: falls through to not-found.
        Assert.Equal(ManagedEdgeState.TargetTabNotFound, status.State);
        Assert.Null(status.DeliveryOrigin);
    }

    [Fact]
    public async Task ProxyVerificationProbesDeliveryOriginAndProvesAuthenticated()
    {
        _browser.SetupGet(b => b.Pages).Returns(new[] { _proxy.Object });
        using var service = Service();
        var status = await service.StatusAsync(Owner(await service.ConnectAsync(Request(ManagedEdgeTrustModel.ApprovedMcasProxyOrigin))), true);
        Assert.Equal(ManagedEdgeState.ConnectedAuthenticated, status.State);
        Assert.True(status.RestAvailable);
        Assert.Equal(ProxyOrigin, status.DeliveryOrigin);
        Assert.Contains("Approved proxied delivery matched", status.Evidence);
        _proxy.Verify(p => p.FetchAsync(ProxyOrigin, "/api/me", null), Times.Once);
        _proxy.Verify(p => p.FetchAsync(Origin, It.IsAny<string>(), It.IsAny<string?>()), Times.Never);
    }
}
