using BirkNext.LocalHttpsProxy;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// The Dedicated Edge prerequisite reports layers that are never merged: the proxy path (configuration and traffic)
/// decides the prerequisite's state; the Browser Companion, the approved page and element picking are facts beside it.
/// A missing Companion limits Browser Discovery and attended automation, never proxy-based authenticated API testing.
/// </summary>
public sealed class AuthenticationCompanionLayeringTests
{
    private static LocalHttpsProxyStatus Proxy(DedicatedBrowserVerification edge, DedicatedCompanionReadiness companion,
        int port = 12345, int? edgePort = 12345, bool edgeRunning = true) => new()
    {
        State = LocalHttpsProxyState.Listening, RuntimeStatus = LocalHttpsProxyRuntimePhase.Running, ProxyListening = true,
        ExpectedProxyPort = port, EdgeProxyPort = edgePort, EdgeRunning = edgeRunning, EdgeVerification = edge,
        ProxyArgumentConfigured = edgePort is not null, Companion = companion,
    };

    private static AuthenticationPrerequisite Browser(LocalHttpsProxyStatus proxy) =>
        AuthenticationReadinessPresentation.Summarize(AuthConfigurationState.Configured, AuthenticatedTestingMethod.LocalHttpsProxy,
            proxy, new ProxyCertificateStatus { State = ProxyCertificateTrustState.Trusted }, loaded: true)
            .Prerequisites.Single(p => p.Id == "browser");

    private static string Fact(AuthenticationPrerequisite p, string label) => p.Facts!.Single(f => f.Item1 == label).Item2;

    private static readonly DedicatedCompanionReadiness Ready = new()
    {
        State = "Connected", Message = "Browser Companion connected in the dedicated profile.", Connected = true, VersionCompatible = true,
        ApprovedPageAvailable = true, ElementPickAvailable = true,
    };

    private static readonly DedicatedCompanionReadiness NotObserved = new() { State = "NotObserved", Message = "Companion not observed." };

    // A. Proxy confirmed, Companion connected, approved page: every layer available, and said as layers.
    [Fact]
    public void ConnectedCompanionWithAnApprovedPage_ReportsEachLayerAvailable()
    {
        var browser = Browser(Proxy(DedicatedBrowserVerification.Confirmed, Ready));
        browser.State.Should().Be(AuthPrerequisiteState.Ok);
        Fact(browser, "Browser Companion").Should().Be("Connected");
        Fact(browser, "Approved target page").Should().Be("Detected");
        Fact(browser, "Element picking").Should().Be("Available");
        browser.Explanation.Should().Contain("Browser Discovery has an approved live page.");
        browser.ActionId.Should().Be("edge-open", "nothing needs restarting");
    }

    // B. Proxy confirmed, Companion missing: the proxy path stays Ok; only browser automation is limited.
    [Fact]
    public void MissingCompanion_LeavesTheProxyPathOk_AndOffersTheCompanionRestart()
    {
        var browser = Browser(Proxy(DedicatedBrowserVerification.Confirmed, NotObserved));
        browser.State.Should().Be(AuthPrerequisiteState.Ok, "proxy-based authenticated API testing does not need the Companion");
        Fact(browser, "Browser Companion").Should().Be("Not observed in dedicated profile");
        browser.Explanation.Should().Contain("Browser Discovery and attended automation are not ready in this browser")
            .And.Contain("Proxy-based API testing remains independent.");
        browser.ActionLabel.Should().Be("Restart with Browser Companion");
        browser.ActionId.Should().Be("edge-restart");
    }

    // The fix: with the proxy misconfigured, the proxy fault stays the stated problem and names its own restart.
    [Theory]
    [InlineData(DedicatedBrowserVerification.Mismatch, 12000)]
    [InlineData(DedicatedBrowserVerification.Missing, 12345)]
    public void AProxyFaultKeepsItsOwnRestart_EvenWhenTheCompanionIsAlsoMissing(DedicatedBrowserVerification edge, int edgePort)
    {
        var browser = Browser(Proxy(edge, NotObserved, edgePort: edgePort));
        browser.State.Should().Be(AuthPrerequisiteState.NeedsAttention);
        browser.ActionLabel.Should().Be("Restart browser with proxy");
        browser.ActionId.Should().Be("edge-restart", "the same relaunch also loads the Companion");
        Fact(browser, "Browser Companion").Should().Be("Not observed in dedicated profile");
    }

    // A connected Companion never repairs a proxy fault.
    [Fact]
    public void AConnectedCompanionDoesNotHideAProxyMismatch()
    {
        var browser = Browser(Proxy(DedicatedBrowserVerification.Mismatch, Ready, edgePort: 12000));
        browser.State.Should().Be(AuthPrerequisiteState.NeedsAttention);
        browser.StatusLabel.Should().Be("Proxy configuration mismatch");
        browser.ActionLabel.Should().Be("Restart browser with proxy");
    }

    // C. Dedicated Edge not running: the browser prerequisite, with no Companion layer invented for a closed browser.
    [Fact]
    public void BrowserNotRunning_HasNoCompanionLayer()
    {
        var browser = Browser(Proxy(DedicatedBrowserVerification.NotRunning, NotObserved, edgePort: null, edgeRunning: false));
        browser.StatusLabel.Should().Be("Not running");
        browser.ActionLabel.Should().Be("Open browser");
        (browser.Facts ?? []).Should().NotContain(f => f.Item1 == "Browser Companion");
    }

    // D. Connected without an approved page: connected is not Browser Discovery ready.
    [Fact]
    public void ConnectedWithoutAnApprovedPage_IsNotBrowserDiscoveryReady()
    {
        var companion = Ready with { ApprovedPageAvailable = false, ElementPickAvailable = false };
        var browser = Browser(Proxy(DedicatedBrowserVerification.Confirmed, companion));
        companion.BrowserDiscoveryReady.Should().BeFalse();
        Fact(browser, "Browser Companion").Should().Be("Connected");
        Fact(browser, "Approved target page").Should().Be("Not detected yet");
        Fact(browser, "Element picking").Should().Be("Unavailable");
        browser.Explanation.Should().Contain("not ready in this browser");
    }

    // E. A Companion paired in another (normal) Edge does not satisfy the dedicated profile's readiness.
    [Fact]
    public void ACompanionPairedElsewhere_DoesNotReadAsConnectedHere()
    {
        var companion = new DedicatedCompanionReadiness { State = "SessionConflict", Message = "Another browser is paired.", Connected = false };
        var browser = Browser(Proxy(DedicatedBrowserVerification.Confirmed, companion));
        Fact(browser, "Browser Companion").Should().Be("Another browser is paired");
        companion.BrowserDiscoveryReady.Should().BeFalse();
        browser.ActionLabel.Should().NotBe("Restart with Browser Companion", "restarting this browser does not resolve another browser's pairing");
    }

    // A version mismatch is not "Connected", even when a heartbeat arrives.
    [Fact]
    public void AnIncompatibleCompanionIsNotConnected()
    {
        var companion = Ready with { State = "Connected", VersionCompatible = false };
        var browser = Browser(Proxy(DedicatedBrowserVerification.Confirmed, companion));
        Fact(browser, "Browser Companion").Should().NotBe("Connected");
        companion.BrowserDiscoveryReady.Should().BeFalse();
    }
}
