using BirkNext.LocalHttpsProxy;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// Authentication readiness. The whole point of this derivation is to keep apart five things that a simpler
/// implementation merges: a certificate being trusted, the proxy running, the browser running, the browser using
/// <em>this</em> proxy, and authentication having been verified. Each one collapsed is a page that says Ready when a
/// request would fail.
/// </summary>
public sealed class AuthenticationReadinessTests
{
    private static ProxyCertificateStatus Certificate(ProxyCertificateTrustState state = ProxyCertificateTrustState.Trusted) =>
        new() { State = state, InstallSupported = true, NotAfter = new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero) };

    private static LocalHttpsProxyStatus Proxy(
        LocalHttpsProxyState state = LocalHttpsProxyState.Ready,
        LocalHttpsProxyRuntimePhase phase = LocalHttpsProxyRuntimePhase.Running,
        DedicatedBrowserVerification edge = DedicatedBrowserVerification.Confirmed,
        int port = 12345, int? edgePort = 12345) => new()
        {
            State = state, RuntimeStatus = phase, Port = port, ProxyListening = true,
            ExpectedProxyPort = port, EdgeProxyPort = edgePort, ProxyArgumentConfigured = edgePort is not null,
            EdgeVerification = edge, EdgeRunning = edge is not DedicatedBrowserVerification.NotRunning,
            EdgeProfileDirectory = @"C:\Users\x\AppData\Local\BirkNext\LocalHttpsProxyEdgeProfile",
            StartedAt = new DateTimeOffset(2026, 9, 21, 14, 22, 0, TimeSpan.Zero),
        };

    private static AuthenticationReadinessSummary Summarize(
        LocalHttpsProxyStatus? proxy = null, ProxyCertificateStatus? certificate = null,
        bool configured = true, bool loaded = true,
        AuthenticatedTestingMethod method = AuthenticatedTestingMethod.LocalHttpsProxy) =>
        AuthenticationReadinessPresentation.Summarize(configured, method, proxy ?? Proxy(), certificate ?? Certificate(), loaded);

    private static AuthenticationPrerequisite Item(AuthenticationReadinessSummary s, string id) =>
        s.Prerequisites.Single(p => p.Id == id);

    [Fact]
    public void EverythingInPlaceIsReady()
    {
        var summary = Summarize();
        summary.State.Should().Be(AuthenticationReadiness.Ready);
        summary.Label.Should().Be("Ready");
        summary.Attention.Should().Be(0);
        summary.CanVerify.Should().BeTrue();
        Item(summary, "browser").StatusLabel.Should().Be("Proxy active");
    }

    [Fact]
    public void ReadyIsAboutPrerequisitesAndNeverClaimsAuthenticationWasVerified()
    {
        var summary = Summarize();
        summary.Detail.Should().Contain("prerequisites are available");
        summary.Detail.Should().NotContain("verified");
        summary.Label.Should().NotContain("Verified");
    }

    [Fact]
    public void AStoppedProxyIsActionRequiredRatherThanAFailure()
    {
        var summary = Summarize(Proxy(LocalHttpsProxyState.Stopped, LocalHttpsProxyRuntimePhase.Stopped, DedicatedBrowserVerification.NotRunning));
        summary.State.Should().Be(AuthenticationReadiness.ActionRequired);
        var proxy = Item(summary, "proxy");
        proxy.StatusLabel.Should().Be("Not running");
        proxy.State.Should().Be(AuthPrerequisiteState.NeedsAttention, "stopped is a resting state, not a fault");
        proxy.Tone.Should().NotBe("error");
        proxy.ActionLabel.Should().Be("Start proxy");
    }

    [Fact]
    public void AFailedProxyUsesFailureStylingAndItsOwnReason()
    {
        var summary = Summarize(Proxy(LocalHttpsProxyState.Failed, LocalHttpsProxyRuntimePhase.Failed, DedicatedBrowserVerification.NotRunning) with
        { FailureReason = "Port 12345 is already in use." });
        var proxy = Item(summary, "proxy");
        proxy.State.Should().Be(AuthPrerequisiteState.Failed);
        proxy.Tone.Should().Be("error");
        proxy.Explanation.Should().Contain("already in use");
        proxy.ActionLabel.Should().Be("Retry");
    }

    [Fact]
    public void AnUntrustedCertificateIsActionRequiredAndSaysWhatToDo()
    {
        var summary = Summarize(certificate: Certificate(ProxyCertificateTrustState.NotTrusted));
        summary.State.Should().Be(AuthenticationReadiness.ActionRequired);
        var certificate = Item(summary, "certificate");
        certificate.StatusLabel.Should().Be("Needs attention");
        certificate.ActionLabel.Should().Be("Install certificate");
    }

    [Fact]
    public void AnUnknownCertificateIsUnknownAndNeverReportedAsMissing()
    {
        var summary = Summarize(certificate: Certificate(ProxyCertificateTrustState.Unknown));
        var certificate = Item(summary, "certificate");
        certificate.StatusLabel.Should().Be("Unknown");
        certificate.State.Should().Be(AuthPrerequisiteState.Unknown);
        certificate.Blocks.Should().BeFalse("an unchecked certificate is not a known problem");
        // Unknown must not become a false Ready either.
        summary.State.Should().Be(AuthenticationReadiness.Limited);
    }

    [Fact]
    public void ANotInstalledCertificateIsDistinctFromAnUnknownOne()
    {
        Item(Summarize(certificate: Certificate(ProxyCertificateTrustState.NotGenerated)), "certificate")
            .StatusLabel.Should().Be("Not installed");
    }

    [Fact]
    public void ManualInstallOffersInstructionsRatherThanAnInstallButtonThatCannotWork()
    {
        var summary = AuthenticationReadinessPresentation.Summarize(true, AuthenticatedTestingMethod.LocalHttpsProxy,
            Proxy(), new ProxyCertificateStatus { State = ProxyCertificateTrustState.NotTrusted, InstallSupported = false }, loaded: true);
        Item(summary, "certificate").ActionLabel.Should().Be("View setup instructions");
    }

    [Fact]
    public void AProxyRunningWithNoBrowserIsActionRequired()
    {
        var summary = Summarize(Proxy(edge: DedicatedBrowserVerification.NotRunning, edgePort: null));
        summary.State.Should().Be(AuthenticationReadiness.ActionRequired);
        var browser = Item(summary, "browser");
        browser.StatusLabel.Should().Be("Not running");
        browser.Explanation.Should().Contain("proxy is running");
        browser.ActionLabel.Should().Be("Open browser");
    }

    [Fact]
    public void ABrowserFromAnEarlierRuntimeIsNeverReportedAsProxyActive()
    {
        // Running, ours, but launched against a port this runtime no longer listens on.
        var summary = Summarize(Proxy(edge: DedicatedBrowserVerification.Mismatch, port: 12345, edgePort: 12000));
        summary.State.Should().Be(AuthenticationReadiness.ActionRequired);
        var browser = Item(summary, "browser");
        browser.StatusLabel.Should().Be("Proxy not active");
        browser.ActionLabel.Should().Be("Restart browser with proxy");
        browser.Facts.Should().Contain(f => f.Value == "127.0.0.1:12345");
        browser.Facts.Should().Contain(f => f.Value == "127.0.0.1:12000");
    }

    [Fact]
    public void ABrowserWeCannotVouchForIsNotConfirmedRatherThanActive()
    {
        var browser = Item(Summarize(Proxy(edge: DedicatedBrowserVerification.NotConfirmed, edgePort: null)), "browser");
        browser.StatusLabel.Should().Be("Proxy not confirmed");
        browser.StatusLabel.Should().NotContain("active");
        browser.ActionLabel.Should().Be("Restart browser with proxy");
    }

    [Fact]
    public void WithTheProxyStoppedTheBrowserIsNotTheUsersNextProblem()
    {
        var browser = Item(Summarize(Proxy(LocalHttpsProxyState.Stopped, LocalHttpsProxyRuntimePhase.Stopped, DedicatedBrowserVerification.NotRunning)), "browser");
        browser.State.Should().Be(AuthPrerequisiteState.NotRequired);
        browser.Blocks.Should().BeFalse("a browser cannot be pointed at a proxy that is not there");
        browser.ActionLabel.Should().BeNull();
    }

    [Fact]
    public void ThreeMissingPrerequisitesAreCountedAndStatedOnce()
    {
        var summary = Summarize(
            Proxy(LocalHttpsProxyState.Stopped, LocalHttpsProxyRuntimePhase.Stopped, DedicatedBrowserVerification.NotRunning),
            Certificate(ProxyCertificateTrustState.NotTrusted));
        // The browser is waiting on the proxy, so there are two things to fix, not three.
        summary.Detail.Should().Be("2 prerequisites need attention.");
    }

    [Fact]
    public void OneMissingPrerequisiteReadsAsSingular()
    {
        Summarize(certificate: Certificate(ProxyCertificateTrustState.NotTrusted))
            .Detail.Should().Be("1 prerequisite needs attention.");
    }

    [Fact]
    public void NothingIsClaimedBeforeTheBackendHasAnswered()
    {
        var summary = Summarize(loaded: false);
        summary.State.Should().Be(AuthenticationReadiness.Checking);
        summary.Prerequisites.Should().OnlyContain(p => p.State == AuthPrerequisiteState.Checking);
        summary.Prerequisites.Should().NotContain(p => p.StatusLabel.Contains("Not"), "a first fetch must not render a false negative");
    }

    [Fact]
    public void NoAuthenticationConfiguredIsNotConfiguredRatherThanFailing()
    {
        var summary = Summarize(configured: false);
        summary.State.Should().Be(AuthenticationReadiness.NotConfigured);
        summary.Prerequisites.Should().BeEmpty("there is nothing to prepare until a method is chosen");
    }

    [Fact]
    public void AMethodWithoutProxyPrerequisitesDoesNotInventThem()
    {
        var summary = Summarize(method: AuthenticatedTestingMethod.ManualOnly);
        summary.State.Should().Be(AuthenticationReadiness.Limited);
        summary.Prerequisites.Should().BeEmpty();
    }

    [Fact]
    public void ADisabledVerifyActionAlwaysSaysWhatIsMissing()
    {
        AuthenticationReadinessPresentation.VerificationBlockedReason(Summarize()).Should().BeEmpty();
        var blocked = AuthenticationReadinessPresentation.VerificationBlockedReason(
            Summarize(Proxy(LocalHttpsProxyState.Stopped, LocalHttpsProxyRuntimePhase.Stopped, DedicatedBrowserVerification.NotRunning)));
        blocked.Should().Contain("local https proxy");
    }

    [Fact]
    public void EndpointDiscoveryIsNotDisabledJustBecauseTheProxyIsNot()
    {
        var incomplete = Summarize(Proxy(LocalHttpsProxyState.Stopped, LocalHttpsProxyRuntimePhase.Stopped, DedicatedBrowserVerification.NotRunning));
        var (status, tone, note) = EndpointDiscoveryCta.Status(incomplete, knownEndpoints: 7);
        status.Should().Contain("setup incomplete");
        tone.Should().Be("needs-action");
        note.Should().Contain("still available", "Endpoint Discovery holds history from more than one source");

        EndpointDiscoveryCta.Status(Summarize(), 0).Status.Should().Be("Ready");
    }
}
