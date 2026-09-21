using BirkNext.LocalHttpsProxy;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;
using Moq;
using Xunit;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// BirkNext never owns a browser's proxy configuration. The local HTTPS proxy is a SERVER that BirkNext starts
/// and stops; pointing a browser at it is the user's own action, in their own Edge settings.
///
/// That has a consequence the UI has to state honestly. When the proxy stops, a browser the user configured is
/// still pointed at a port that no longer answers — and BirkNext cannot put it back, because it never changed
/// it. The remedy is to say so. Claiming a restore that did not happen would be worse than saying nothing.
/// </summary>
public sealed class ProxyOwnershipSafetyTests
{
    private static readonly LocalHttpsProxyStatus Listening = new()
    {
        SessionId = "s", State = LocalHttpsProxyState.Listening, Port = 8888,
        LocalIntegrationAvailable = true, EnvironmentAllowed = true, PortAvailable = true,
    };

    private static readonly LocalHttpsProxyStatus Routed = Listening with
    {
        State = LocalHttpsProxyState.Ready, AuthenticatedRequestsObserved = 3, AuthenticatedCredentialAvailable = true,
    };

    private static readonly LocalHttpsProxyStatus Stopped = new() { State = LocalHttpsProxyState.Stopped, Evidence = "Proxy stopped." };

    // ── The state model: server state, routing and ownership are three different questions ───

    [Theory]
    [InlineData(LocalHttpsProxyState.Starting, true)]
    [InlineData(LocalHttpsProxyState.Listening, true)]
    [InlineData(LocalHttpsProxyState.WaitingForAuthenticatedTraffic, true)]
    [InlineData(LocalHttpsProxyState.Ready, true)]
    [InlineData(LocalHttpsProxyState.Stopped, false)]
    [InlineData(LocalHttpsProxyState.Stale, false)]
    [InlineData(LocalHttpsProxyState.Failed, false)]
    [InlineData(LocalHttpsProxyState.NotStarted, false)]
    public void ProxyServerStateIsItsOwnQuestion(LocalHttpsProxyState state, bool running) =>
        AuthenticatedTestingStates.ProxyServerRunning(Listening with { State = state }).Should().Be(running);

    [Fact]
    public void ARunningServerIsNotABrowserThatUsesIt()
    {
        // The distinction the whole feature turns on, restated here so it cannot quietly collapse.
        AuthenticatedTestingStates.ProxyServerRunning(Listening).Should().BeTrue();
        AuthenticatedTestingStates.BrowserRoutedThroughProxy(Listening).Should().BeFalse();
    }

    // ── The state that needs saying: routed, then the proxy went away ────────────────────────

    [Fact]
    public void AProxyThatStopsWhileABrowserUsesItSaysSoAndClaimsNoRestore()
    {
        AuthenticatedTestingStates.ProxyStoppedWhileRouted(Stopped, browserWasRouted: true).Should().BeTrue();

        var title = AuthenticatedTestingStates.ProxyGuidanceTitle(Stopped, browserWasRouted: true);
        var text = AuthenticatedTestingStates.ProxyGuidanceText(Stopped, browserWasRouted: true);

        title.Should().Be("Proxy is no longer running");
        text.Should().Contain("settings are unchanged");
        // Never a claim that cleanup happened, because none did and none could.
        text.Should().NotContainAny("restored", "Restored", "automatically", "reverted");
        text.Should().NotContain("Restore your previous");
    }

    [Theory]
    [InlineData(LocalHttpsProxyState.Stopped)]
    [InlineData(LocalHttpsProxyState.Stale)]
    [InlineData(LocalHttpsProxyState.Failed)]
    public void EveryWayTheProxyCanGoAwayProducesTheSameWarning(LocalHttpsProxyState state) =>
        AuthenticatedTestingStates.ProxyGuidanceTitle(Listening with { State = state }, browserWasRouted: true)
            .Should().Be("Proxy is no longer running");

    [Fact]
    public void AProxyThatNoBrowserEverUsedIsNotWarnedAbout()
    {
        // Nothing was ever pointed at it, so stopping it leaves nothing pointing anywhere.
        AuthenticatedTestingStates.ProxyStoppedWhileRouted(Stopped, browserWasRouted: false).Should().BeFalse();
        AuthenticatedTestingStates.ProxyGuidanceTitle(Stopped, browserWasRouted: false)
            .Should().Be("Open the dedicated proxy browser");
    }

    [Fact]
    public void AWorkingProxyKeepsItsOwnGuidance()
    {
        AuthenticatedTestingStates.ProxyGuidanceTitle(Routed, browserWasRouted: true).Should().Be("Proxy traffic observed");
        AuthenticatedTestingStates.ProxyGuidanceTitle(Listening, browserWasRouted: false).Should().Be("Open the dedicated proxy browser");
    }

    [Fact]
    public void NoWordingAnywhereClaimsBirkNextChangesBrowserProxySettings()
    {
        foreach (var routed in new[] { true, false })
        foreach (var proxy in new[] { Listening, Routed, Stopped })
        foreach (var copy in new[]
                 {
                     AuthenticatedTestingStates.ProxyGuidanceTitle(proxy, routed),
                     AuthenticatedTestingStates.ProxyGuidanceText(proxy, routed),
                     AuthenticatedTestingStates.EdgeBrowserSetup(proxy, routed),
                 })
            copy.Should().NotContainAny(
                "Previous settings will be restored", "settings have been restored", "automatically",
                "Proxy enabled automatically", "Edge proxy active");
    }

    // ── The fact must outlive the proxy, or the warning can never be shown ───────────────────

    [Fact]
    public async Task TheRuntimeRemembersThatABrowserWasRoutedAfterTheProxyStops()
    {
        var api = new Mock<ILocalHttpsProxyApiService>();
        api.Setup(a => a.StartAsync(It.IsAny<LocalHttpsProxyScopeRequest>())).ReturnsAsync(Listening);
        api.Setup(a => a.GetRuntimeAsync()).ReturnsAsync(Routed);
        api.Setup(a => a.StopAsync(It.IsAny<LocalHttpsProxySessionRequest>())).ReturnsAsync(Stopped);
        await using var runtime = new LocalHttpsProxyRuntime(api.Object);
        var profile = new FrontendAnalysisProfile { Id = "dev", Name = "Dev", TargetUrl = "https://app.example.test" };

        await runtime.StartAsync(profile);
        runtime.BrowserWasRouted.Should().BeFalse("nothing has come through the proxy yet");

        await runtime.RefreshAsync();
        runtime.BrowserWasRouted.Should().BeTrue("traffic arrived, which is the only proof a browser was pointed here");

        await runtime.StopAsync();

        // The status is replaced, but the browser is still pointed at the port — and now at nothing.
        runtime.Status.State.Should().Be(LocalHttpsProxyState.Stopped);
        runtime.BrowserWasRouted.Should().BeTrue("this is exactly when the user must be told");
        AuthenticatedTestingStates.ProxyStoppedWhileRouted(runtime.Status, runtime.BrowserWasRouted).Should().BeTrue();
    }

    [Fact]
    public async Task AFreshSessionDoesNotInheritTheLastOnesRouting()
    {
        var api = new Mock<ILocalHttpsProxyApiService>();
        api.Setup(a => a.StartAsync(It.IsAny<LocalHttpsProxyScopeRequest>())).ReturnsAsync(Listening);
        api.Setup(a => a.GetRuntimeAsync()).ReturnsAsync(Routed);
        api.Setup(a => a.StopAsync(It.IsAny<LocalHttpsProxySessionRequest>())).ReturnsAsync(Stopped);
        await using var runtime = new LocalHttpsProxyRuntime(api.Object);
        var profile = new FrontendAnalysisProfile { Id = "dev", Name = "Dev", TargetUrl = "https://app.example.test" };

        await runtime.StartAsync(profile);
        await runtime.RefreshAsync();
        await runtime.StopAsync();
        await runtime.StartAsync(profile);

        runtime.BrowserWasRouted.Should().BeFalse("a new proxy session starts with no browser known to use it");
    }
}
