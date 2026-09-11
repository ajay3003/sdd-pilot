using BirkNext.Api.Services.ManagedEdge;
using BirkNext.Api.Services.AuthenticatedReview;
using BirkNext.ManagedEdge;
using Microsoft.Extensions.Options;
using Moq;

namespace BirkNext.Api.Tests.Services;

/// <summary>
/// Trust-model lifecycle: the saved Browser delivery trust policy is applied to whatever the browser actually delivers.
/// ApprovedMcasProxyOrigin means "exact origin OR approved correlated MCAS proxy" - exact origin is always preferred and
/// never requires MCAS; ExactOrigin never trusts a proxy. Removing or introducing MCAS at the tenant must not require any
/// profile edit for an environment that already carries the right policy.
/// </summary>
public sealed class ManagedEdgeTrustLifecycleTests
{
    private const string Origin = "https://m2lbdev.bufetat.no";
    private const string ProxyOrigin = "https://m2lbdev-bufetat-no.access.mcas.ms";
    private const string Fingerprint = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";
    private readonly Mock<IManagedEdgeConnector> _connector = new(MockBehavior.Strict);
    private readonly ManagedEdgeTargetRule _rule = new() { Origin = Origin, AuthenticatedOnlySelector = "#signed-in-only", ProtectedGetPath = "/api/me", SafeGetPaths = ["/api/me"] };

    /// <summary>One "runtime": a browser as the connector sees it on a given connect.</summary>
    private sealed class Runtime
    {
        public Mock<IManagedEdgeBrowser> Browser { get; } = new();
        public List<IManagedEdgePage> Pages { get; } = [];
        public List<string> Discovered { get; } = [];
        public Runtime()
        {
            Browser.SetupGet(b => b.IsConnected).Returns(true);
            Browser.SetupGet(b => b.ContextCount).Returns(1);
            Browser.SetupGet(b => b.Pages).Returns(() => Pages.ToArray());
            Browser.SetupGet(b => b.DiscoveredPageUrls).Returns(() => Discovered.ToArray());
        }
        public void Disconnect() => Browser.SetupGet(b => b.IsConnected).Returns(false);
    }

    private static Mock<IManagedEdgePage> Page(string url, bool authenticated = true)
    {
        var page = new Mock<IManagedEdgePage>();
        page.SetupGet(p => p.Url).Returns(url);
        page.SetupGet(p => p.IsClosed).Returns(false);
        page.SetupGet(p => p.NavigationChanged).Returns(false);
        var origin = ManagedEdgePolicy.Origin(url);
        page.Setup(p => p.HasAuthenticatedElementAsync(origin, "#signed-in-only")).ReturnsAsync(authenticated);
        page.Setup(p => p.FetchAsync(origin, "/api/me", null)).ReturnsAsync(new ManagedEdgeProbeResult(authenticated ? 200 : 401, "application/json", 3));
        page.Setup(p => p.GetLocationOriginAsync()).ReturnsAsync(origin);
        page.Setup(p => p.GetNavigationOriginsAsync()).ReturnsAsync(new[] { origin });
        return page;
    }

    private static Mock<IManagedEdgePage> ExactPage() => Page(Origin + "/dashboard");

    /// <summary>A fully correlated MCAS delivery: live origin equals delivery origin; history shows target + Entra + Defender intermediary.</summary>
    private static Mock<IManagedEdgePage> CorrelatedProxyPage()
    {
        var page = Page(ProxyOrigin + "/dashboard");
        page.Setup(p => p.GetNavigationOriginsAsync()).ReturnsAsync(new[] { Origin, "https://login.microsoftonline.com", "https://bufdir-no.access.mcas.ms", ProxyOrigin });
        return page;
    }

    /// <summary>Connector returns the given runtimes in order: one per ConnectAsync.</summary>
    private ManagedEdgeCdpService Service(params Runtime[] runtimes)
    {
        var queue = new Queue<Runtime>(runtimes);
        _connector.Setup(c => c.ConnectAsync("http://127.0.0.1:9222", It.IsAny<CancellationToken>())).ReturnsAsync(() => queue.Dequeue().Browser.Object);
        return new(_connector.Object, Options.Create(new ManagedEdgeOptions { Targets = [_rule] }),
            Options.Create(new AuthenticatedReviewOptions { Enabled = true, Runtime = "LocalWorkstation" }));
    }

    private static ManagedEdgeConnectRequest Saved(ManagedEdgeTrustModel policy) => new("dev", Origin + "/", Fingerprint, policy);
    private static ManagedEdgeSessionRequest Owner(ManagedEdgeStatus s) => new(s.SessionId!, "dev", Fingerprint);

    private static void AssertExactTrusted(ManagedEdgeStatus status, ManagedEdgeTrustModel savedPolicy)
    {
        Assert.Equal(ManagedEdgeState.ConnectedUnproven, status.State);
        Assert.True(status.OriginMatched);
        Assert.Equal(savedPolicy, status.TrustModel);                      // configured trust is echoed, not rewritten
        Assert.Equal(ManagedEdgeTrustDecision.ExactOriginTrusted, status.TrustDecision);
        Assert.Equal(Origin, status.TargetOrigin);
        Assert.Equal(Origin, status.DeliveryOrigin);
        Assert.False(status.ProxiedDelivery);
        Assert.Equal("Direct", status.BrowserDelivery);
        Assert.Null(status.CorrelationEvidence);
    }

    private static void AssertProxyTrusted(ManagedEdgeStatus status)
    {
        Assert.Equal(ManagedEdgeState.ConnectedUnproven, status.State);
        Assert.True(status.OriginMatched);
        Assert.Equal(ManagedEdgeTrustModel.ApprovedMcasProxyOrigin, status.TrustModel);
        Assert.Equal(ManagedEdgeTrustDecision.ApprovedProxyTrusted, status.TrustDecision);
        Assert.Equal(Origin, status.TargetOrigin);                          // never rewritten to the proxy
        Assert.Equal(ProxyOrigin, status.DeliveryOrigin);
        Assert.True(status.ProxiedDelivery);
        Assert.Equal("Microsoft Defender for Cloud Apps proxy", status.BrowserDelivery);
        Assert.NotNull(status.CorrelationEvidence);
    }

    // A. Saved ExactOrigin + direct target -> accepted
    [Fact]
    public async Task A_ExactOriginPolicy_DirectTarget_Accepted()
    {
        var runtime = new Runtime(); runtime.Pages.Add(ExactPage().Object); runtime.Discovered.Add(Origin + "/dashboard");
        using var service = Service(runtime);
        AssertExactTrusted(await service.ConnectAsync(Saved(ManagedEdgeTrustModel.ExactOrigin)), ManagedEdgeTrustModel.ExactOrigin);
    }

    // B. Saved ExactOrigin + correlated MCAS only -> rejected (strict), explained, never trusted
    [Fact]
    public async Task B_ExactOriginPolicy_CorrelatedProxyOnly_Rejected()
    {
        var proxy = CorrelatedProxyPage();
        var runtime = new Runtime(); runtime.Pages.Add(proxy.Object);
        using var service = Service(runtime);
        var status = await service.ConnectAsync(Saved(ManagedEdgeTrustModel.ExactOrigin));
        Assert.Equal(ManagedEdgeState.ProxiedDeliveryNotPermitted, status.State);
        Assert.Equal(ManagedEdgeTrustDecision.ProxyNotPermitted, status.TrustDecision);
        Assert.Null(status.SessionId);
        Assert.False(status.OriginMatched);
        Assert.False(status.AuthenticatedBrowserAvailable);
        Assert.Equal(Origin, status.TargetOrigin);
        Assert.Equal(ProxyOrigin, status.DeliveryOrigin);      // observed only, for the explanation
        Assert.Contains("proxied by Microsoft Defender for Cloud Apps, but this environment permits exact-origin delivery only", status.Evidence);
        // Correlation is not even attempted: the policy decides first.
        proxy.Verify(p => p.GetLocationOriginAsync(), Times.Never);
        proxy.Verify(p => p.GetNavigationOriginsAsync(), Times.Never);
        runtime.Browser.Verify(b => b.Dispose(), Times.Once);
    }

    // C. Saved ApprovedMcasProxyOrigin + direct target only -> accepted (the "MCAS removed in future" case)
    [Fact]
    public async Task C_ProxyPermittedPolicy_DirectTargetOnly_AcceptedAutomatically()
    {
        var runtime = new Runtime(); runtime.Pages.Add(ExactPage().Object); runtime.Discovered.Add(Origin + "/dashboard");
        using var service = Service(runtime);
        var status = await service.ConnectAsync(Saved(ManagedEdgeTrustModel.ApprovedMcasProxyOrigin));
        AssertExactTrusted(status, ManagedEdgeTrustModel.ApprovedMcasProxyOrigin);
        Assert.Contains("Expected origin matched", status.Evidence);
        Assert.DoesNotContain("proxy", status.Evidence, StringComparison.OrdinalIgnoreCase);
        var verified = await service.StatusAsync(Owner(status), true);
        Assert.Equal(ManagedEdgeState.ConnectedAuthenticated, verified.State);
        Assert.Equal(ManagedEdgeTrustDecision.ExactOriginTrusted, verified.TrustDecision);
        Assert.Equal(ManagedEdgeTrustModel.ApprovedMcasProxyOrigin, verified.TrustModel);
        Assert.Equal("Direct", verified.BrowserDelivery);
    }

    // D. Saved ApprovedMcasProxyOrigin + exact + correlated MCAS -> exact wins, proxy never even inspected
    [Fact]
    public async Task D_ProxyPermittedPolicy_ExactAndProxyPresent_ExactWins()
    {
        var proxy = CorrelatedProxyPage();
        var exact = ExactPage();
        var runtime = new Runtime(); runtime.Pages.Add(proxy.Object); runtime.Pages.Add(exact.Object);
        using var service = Service(runtime);
        var status = await service.ConnectAsync(Saved(ManagedEdgeTrustModel.ApprovedMcasProxyOrigin));
        AssertExactTrusted(status, ManagedEdgeTrustModel.ApprovedMcasProxyOrigin);
        proxy.Verify(p => p.GetLocationOriginAsync(), Times.Never);
        proxy.Verify(p => p.GetNavigationOriginsAsync(), Times.Never);
        var verified = await service.StatusAsync(Owner(status), true);
        Assert.Equal(ManagedEdgeState.ConnectedAuthenticated, verified.State);
        exact.Verify(p => p.FetchAsync(Origin, "/api/me", null), Times.Once);
        proxy.Verify(p => p.FetchAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()), Times.Never);
    }

    // E. Saved ApprovedMcasProxyOrigin + correlated MCAS only + all evidence valid -> proxy accepted
    [Fact]
    public async Task E_ProxyPermittedPolicy_CorrelatedProxyOnly_Accepted()
    {
        var runtime = new Runtime(); runtime.Pages.Add(CorrelatedProxyPage().Object);
        using var service = Service(runtime);
        var status = await service.ConnectAsync(Saved(ManagedEdgeTrustModel.ApprovedMcasProxyOrigin));
        AssertProxyTrusted(status);
        Assert.Contains("configured target", status.CorrelationEvidence);
        Assert.Contains("Entra authority", status.CorrelationEvidence);
        Assert.Contains("Defender sign-in intermediary", status.CorrelationEvidence);
        Assert.DoesNotContain("/dashboard", status.CorrelationEvidence);   // no path leaves the browser
    }

    // F. Saved ApprovedMcasProxyOrigin + arbitrary access.mcas.ms -> rejected (not a candidate at all)
    [Theory]
    [InlineData("https://evil.access.mcas.ms/dashboard")]
    [InlineData("https://other-tenant-app.access.mcas.ms/dashboard")]
    [InlineData("https://m2lbdev-bufetat-no.access.mcas.ms.attacker.example/dashboard")]
    [InlineData("https://xm2lbdev-bufetat-no.access.mcas.ms/dashboard")]
    public async Task F_ProxyPermittedPolicy_ArbitraryMcasHost_Rejected(string url)
    {
        var page = Page(url);
        page.Setup(p => p.GetNavigationOriginsAsync()).ReturnsAsync(new[] { Origin, "https://login.microsoftonline.com" });
        var runtime = new Runtime(); runtime.Pages.Add(page.Object);
        using var service = Service(runtime);
        var status = await service.ConnectAsync(Saved(ManagedEdgeTrustModel.ApprovedMcasProxyOrigin));
        Assert.Equal(ManagedEdgeState.TargetTabNotFound, status.State);
        Assert.Equal(ManagedEdgeTrustDecision.NotTrusted, status.TrustDecision);
        Assert.Null(status.SessionId);
        Assert.Null(status.DeliveryOrigin);
        page.Verify(p => p.GetLocationOriginAsync(), Times.Never);
    }

    // G. Target-correlated hostname but navigation evidence missing -> rejected
    [Fact]
    public async Task G_ProxyPermittedPolicy_NavigationEvidenceMissing_Rejected()
    {
        var proxy = Page(ProxyOrigin + "/dashboard");
        proxy.Setup(p => p.GetNavigationOriginsAsync()).ReturnsAsync(Array.Empty<string>());
        var runtime = new Runtime(); runtime.Pages.Add(proxy.Object);
        using var service = Service(runtime);
        var status = await service.ConnectAsync(Saved(ManagedEdgeTrustModel.ApprovedMcasProxyOrigin));
        Assert.Equal(ManagedEdgeState.ProxiedDeliveryUncorrelated, status.State);
        Assert.Equal(ManagedEdgeTrustDecision.ProxyCorrelationFailed, status.TrustDecision);
        Assert.Null(status.SessionId);
        Assert.False(status.AuthenticatedBrowserAvailable);
        runtime.Browser.Verify(b => b.Dispose(), Times.Once);
    }

    // H. Correct history but live origin mismatch -> rejected
    [Fact]
    public async Task H_ProxyPermittedPolicy_LiveOriginMismatch_Rejected()
    {
        var proxy = CorrelatedProxyPage();
        proxy.Setup(p => p.GetLocationOriginAsync()).ReturnsAsync("https://someone-else.access.mcas.ms");
        var runtime = new Runtime(); runtime.Pages.Add(proxy.Object);
        using var service = Service(runtime);
        var status = await service.ConnectAsync(Saved(ManagedEdgeTrustModel.ApprovedMcasProxyOrigin));
        Assert.Equal(ManagedEdgeState.ProxiedDeliveryUncorrelated, status.State);
        Assert.Equal(ManagedEdgeTrustDecision.ProxyCorrelationFailed, status.TrustDecision);
        Assert.Null(status.SessionId);
    }

    // I. HTTP proxy origin -> rejected (not a candidate: HTTPS is required)
    [Fact]
    public async Task I_ProxyPermittedPolicy_HttpProxyOrigin_Rejected()
    {
        var proxy = Page("http://m2lbdev-bufetat-no.access.mcas.ms/dashboard");
        proxy.Setup(p => p.GetNavigationOriginsAsync()).ReturnsAsync(new[] { Origin, "https://login.microsoftonline.com" });
        var runtime = new Runtime(); runtime.Pages.Add(proxy.Object);
        using var service = Service(runtime);
        var status = await service.ConnectAsync(Saved(ManagedEdgeTrustModel.ApprovedMcasProxyOrigin));
        Assert.Equal(ManagedEdgeState.TargetTabNotFound, status.State);
        Assert.Null(status.SessionId);
        Assert.Null(status.DeliveryOrigin);
        Assert.False(ManagedEdgePolicy.IsCorrelatedMcasProxyOrigin("http://m2lbdev-bufetat-no.access.mcas.ms/", Origin));
    }

    // J. Legacy request without trust model -> ExactOrigin (contract default)
    [Fact]
    public void J_LegacyRequestWithoutTrustModel_DefaultsToExactOrigin()
    {
        var request = new ManagedEdgeConnectRequest("dev", Origin + "/", Fingerprint);
        Assert.Equal(ManagedEdgeTrustModel.ExactOrigin, request.TrustModel);
        var legacyJson = """{"ProfileId":"dev","TargetUrl":"https://m2lbdev.bufetat.no/","ContextFingerprint":"0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF"}""";
        var deserialized = System.Text.Json.JsonSerializer.Deserialize<ManagedEdgeConnectRequest>(legacyJson)!;
        Assert.Equal(ManagedEdgeTrustModel.ExactOrigin, deserialized.TrustModel);
        Assert.Equal(ManagedEdgeTrustDecision.NotEvaluated, new ManagedEdgeStatus().TrustDecision);
    }

    // K. MCAS session verified -> browser disconnect -> runtime stale, capabilities revoked
    [Fact]
    public async Task K_VerifiedProxySession_BrowserDisconnect_BecomesStale()
    {
        var runtime = new Runtime(); runtime.Pages.Add(CorrelatedProxyPage().Object);
        using var service = Service(runtime);
        var connected = await service.ConnectAsync(Saved(ManagedEdgeTrustModel.ApprovedMcasProxyOrigin));
        var verified = await service.StatusAsync(Owner(connected), true);
        Assert.Equal(ManagedEdgeState.ConnectedAuthenticated, verified.State);
        Assert.True(verified.RestAvailable);
        runtime.Disconnect();
        var stale = await service.StatusAsync(Owner(connected));
        Assert.Equal(ManagedEdgeState.Stale, stale.State);
        Assert.Equal(ManagedEdgeTrustDecision.NotTrusted, stale.TrustDecision);
        Assert.False(stale.AuthenticatedBrowserAvailable);
        Assert.False(stale.RestAvailable);
        Assert.False(stale.OriginMatched);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExecuteSameOriginFetchAsync(new(connected.SessionId!, "dev", Fingerprint, "/api/me")));
    }

    // L / 23. MCAS removed: proxy session in runtime 1, later direct exact-origin in runtime 2 -> trusted immediately, no config change
    [Fact]
    public async Task L_McasRemovedLater_ProxyThenDirect_DirectAcceptedWithSamePolicy()
    {
        var mcasEra = new Runtime(); mcasEra.Pages.Add(CorrelatedProxyPage().Object);
        var directEra = new Runtime(); directEra.Pages.Add(ExactPage().Object); directEra.Discovered.Add(Origin + "/dashboard");
        using var service = Service(mcasEra, directEra);
        var saved = Saved(ManagedEdgeTrustModel.ApprovedMcasProxyOrigin);   // the ONE saved profile, never edited

        var first = await service.ConnectAsync(saved);
        AssertProxyTrusted(first);
        Assert.Equal(ManagedEdgeState.ConnectedAuthenticated, (await service.StatusAsync(Owner(first), true)).State);
        await service.DisconnectAsync(Owner(first));                          // runtime 1 ends

        var second = await service.ConnectAsync(saved);                      // identical request: same profile, same fingerprint, same policy
        AssertExactTrusted(second, ManagedEdgeTrustModel.ApprovedMcasProxyOrigin);
        Assert.NotEqual(first.SessionId, second.SessionId);
        var verified = await service.StatusAsync(Owner(second), true);
        Assert.Equal(ManagedEdgeState.ConnectedAuthenticated, verified.State);
        Assert.Equal("Direct", verified.BrowserDelivery);
        Assert.Equal(ManagedEdgeTrustDecision.ExactOriginTrusted, verified.TrustDecision);
        Assert.DoesNotContain("stale", verified.Evidence, StringComparison.OrdinalIgnoreCase);
    }

    // 24. MCAS introduced: direct in runtime 1, later the exact page is uninspectable and a correlated proxy appears -> fallback
    [Fact]
    public async Task McasIntroducedLater_DirectThenUninspectableExactPlusProxy_ProxyFallbackAccepted()
    {
        var directEra = new Runtime(); directEra.Pages.Add(ExactPage().Object); directEra.Discovered.Add(Origin + "/dashboard");
        // Real-world shape: /json/list still advertises the exact tab, but attachToTarget is refused, so it is absent from Pages.
        var mcasEra = new Runtime(); mcasEra.Discovered.Add(Origin + "/dashboard"); mcasEra.Pages.Add(CorrelatedProxyPage().Object);
        using var service = Service(directEra, mcasEra);
        var saved = Saved(ManagedEdgeTrustModel.ApprovedMcasProxyOrigin);

        var first = await service.ConnectAsync(saved);
        AssertExactTrusted(first, ManagedEdgeTrustModel.ApprovedMcasProxyOrigin);
        await service.DisconnectAsync(Owner(first));

        var second = await service.ConnectAsync(saved);
        AssertProxyTrusted(second);
        Assert.Equal(1, second.DiscoveredTargetTabs);                        // uninspectable exact tab did not block the fallback
        Assert.Equal(ManagedEdgeState.ConnectedAuthenticated, (await service.StatusAsync(Owner(second), true)).State);
    }

    // 25. MCAS introduced for an ExactOrigin environment: stays strict, explained, nothing trusted
    [Fact]
    public async Task McasIntroducedLater_ExactOriginPolicy_StaysStrictAndExplains()
    {
        var directEra = new Runtime(); directEra.Pages.Add(ExactPage().Object);
        var mcasEra = new Runtime(); mcasEra.Discovered.Add(Origin + "/dashboard"); mcasEra.Pages.Add(CorrelatedProxyPage().Object);
        using var service = Service(directEra, mcasEra);
        var saved = Saved(ManagedEdgeTrustModel.ExactOrigin);

        var first = await service.ConnectAsync(saved);
        AssertExactTrusted(first, ManagedEdgeTrustModel.ExactOrigin);
        await service.DisconnectAsync(Owner(first));

        var second = await service.ConnectAsync(saved);
        Assert.Equal(ManagedEdgeState.ProxiedDeliveryNotPermitted, second.State);
        Assert.Equal(ManagedEdgeTrustDecision.ProxyNotPermitted, second.TrustDecision);
        Assert.Null(second.SessionId);
        Assert.False(second.AuthenticatedBrowserAvailable);
        Assert.Contains("permits exact-origin delivery only", second.Evidence);
        Assert.Contains("refuses debugger attachment", second.Evidence);   // the uninspectable exact tab is also explained
    }

    // 18. Uninspectable exact tab without any proxy candidate is still reported precisely (no regression)
    [Fact]
    public async Task UninspectableExactTab_NoProxy_ReportedAsNotInspectable_UnderEitherPolicy()
    {
        foreach (var policy in new[] { ManagedEdgeTrustModel.ExactOrigin, ManagedEdgeTrustModel.ApprovedMcasProxyOrigin })
        {
            var runtime = new Runtime(); runtime.Discovered.Add(Origin + "/dashboard");
            using var service = Service(runtime);
            var status = await service.ConnectAsync(Saved(policy));
            Assert.Equal(ManagedEdgeState.TargetTabNotInspectable, status.State);
            Assert.Equal(ManagedEdgeTrustDecision.TargetNotInspectable, status.TrustDecision);
            Assert.Equal(policy, status.TrustModel);
            Assert.Null(status.SessionId);
        }
    }

    // 26/27. Capabilities follow the verified session; probes use the delivery origin, target stays the configured environment
    [Fact]
    public async Task ProbeOriginFollowsDelivery_TargetOriginNeverRewritten()
    {
        var proxy = CorrelatedProxyPage();
        var runtime = new Runtime(); runtime.Pages.Add(proxy.Object);
        using var service = Service(runtime);
        var connected = await service.ConnectAsync(Saved(ManagedEdgeTrustModel.ApprovedMcasProxyOrigin));
        var verified = await service.StatusAsync(Owner(connected), true);
        Assert.Equal(ManagedEdgeState.ConnectedAuthenticated, verified.State);
        Assert.True(verified.RestAvailable);
        Assert.True(verified.SecurityBrowserAvailable);
        Assert.Equal(Origin, verified.TargetOrigin);
        Assert.Equal(ProxyOrigin, verified.DeliveryOrigin);
        // Browser-context probes run same-origin against the DELIVERY origin (Session.ProbeOrigin)...
        proxy.Verify(p => p.HasAuthenticatedElementAsync(ProxyOrigin, "#signed-in-only"), Times.Once);
        proxy.Verify(p => p.FetchAsync(ProxyOrigin, "/api/me", null), Times.Once);
        var probe = await service.ExecuteSameOriginFetchAsync(new(connected.SessionId!, "dev", Fingerprint, "/api/me"));
        Assert.Equal(200, probe.StatusCode);
        proxy.Verify(p => p.FetchAsync(ProxyOrigin, "/api/me", null), Times.Exactly(2));
        // ...and never against the target origin, while the rule/target stays keyed to the configured environment.
        proxy.Verify(p => p.FetchAsync(Origin, It.IsAny<string>(), It.IsAny<string?>()), Times.Never);
        Assert.Equal(Origin, _rule.Origin);
    }

    // 26. ExactOrigin saved and only MCAS available -> no authenticated capability at all
    [Fact]
    public async Task ExactOriginPolicy_ProxyOnly_NoAuthenticatedCapability()
    {
        var runtime = new Runtime(); runtime.Pages.Add(CorrelatedProxyPage().Object);
        using var service = Service(runtime);
        var status = await service.ConnectAsync(Saved(ManagedEdgeTrustModel.ExactOrigin));
        Assert.Null(status.SessionId);
        Assert.False(status.AuthenticatedBrowserAvailable);
        Assert.False(status.SecurityBrowserAvailable);
        Assert.False(status.RestAvailable);
        Assert.False(status.GraphQlAvailable);
        Assert.True(status.PublicCoverageAvailable);
    }

    // 15. Navigation evidence is reduced to origins: a page that reports full URLs is still correlated only by origin, and nothing else is surfaced
    [Fact]
    public async Task CorrelationEvidenceContainsNoPathQueryOrSecrets()
    {
        var proxy = Page(ProxyOrigin + "/dashboard?code=SECRET#token=SECRET");
        proxy.Setup(p => p.GetNavigationOriginsAsync()).ReturnsAsync(new[] { Origin, "https://login.microsoftonline.com", ProxyOrigin });
        var runtime = new Runtime(); runtime.Pages.Add(proxy.Object);
        using var service = Service(runtime);
        var status = await service.ConnectAsync(Saved(ManagedEdgeTrustModel.ApprovedMcasProxyOrigin));
        AssertProxyTrusted(status);
        Assert.DoesNotContain("SECRET", status.CorrelationEvidence);
        Assert.DoesNotContain("SECRET", status.Evidence);
        Assert.DoesNotContain("?", status.CorrelationEvidence);
        Assert.DoesNotContain("SECRET", status.DeliveryOrigin);
    }
}
