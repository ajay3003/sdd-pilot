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
        AuthConfigurationState configured = AuthConfigurationState.Configured, bool loaded = true,
        AuthenticatedTestingMethod method = AuthenticatedTestingMethod.LocalHttpsProxy,
        string? scopeFingerprint = null, bool environmentAllowed = true) =>
        AuthenticationReadinessPresentation.Summarize(configured, method, proxy ?? Proxy(), certificate ?? Certificate(), loaded,
            scopeFingerprint, environmentAllowed);

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
        var summary = AuthenticationReadinessPresentation.Summarize(AuthConfigurationState.Configured, AuthenticatedTestingMethod.LocalHttpsProxy,
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

    /// <summary>
    /// A missing provider is a missing provider — not a missing certificate, a stopped proxy or a closed browser. The
    /// prerequisites belong to the chosen METHOD and stay reported and operable, because "is the certificate trusted?"
    /// has the same answer either way and the user may well be preparing before configuring.
    /// </summary>
    [Fact]
    public void NoAuthenticationConfiguredIsNotConfiguredRatherThanFailing()
    {
        var summary = Summarize(configured: AuthConfigurationState.NotConfigured);
        summary.State.Should().Be(AuthenticationReadiness.NotConfigured);
        summary.Detail.Should().Be("No authentication provider is configured for this Target Environment.");
        summary.CanVerify.Should().BeFalse();
        summary.Prerequisites.Should().HaveCount(3, "the proxy method's prerequisites do not depend on the provider");
        summary.Prerequisites.Should().OnlyContain(p => p.State == AuthPrerequisiteState.Ok);
    }

    [Fact]
    public void AProxyThePolicyForbidsIsExplainedRatherThanOffered()
    {
        var summary = Summarize(
            Proxy(LocalHttpsProxyState.Stopped, LocalHttpsProxyRuntimePhase.Stopped, DedicatedBrowserVerification.NotRunning),
            environmentAllowed: false);
        var proxy = Item(summary, "proxy");
        proxy.StatusLabel.Should().Be("Not available here");
        proxy.Explanation.Should().Contain("non-production");
        proxy.ActionLabel.Should().BeNull("a start this environment may never perform is not a button");
    }

    [Fact]
    public void ASessionBelongingToAnotherEnvironmentIsNamedRatherThanAdopted()
    {
        var running = Proxy() with { SessionId = "other-session", ContextFingerprint = "other-fingerprint", ProfileId = "acc" };
        var proxy = Item(Summarize(running, scopeFingerprint: "this-fingerprint"), "proxy");
        proxy.StatusLabel.Should().Be("Running for another environment");
        proxy.Explanation.Should().Contain("acc");
        proxy.ActionLabel.Should().Be("Stop proxy", "stopping someone else's session stays an explicit act");
    }

    /// <summary>
    /// The defect this whole derivation was rewritten for. Readiness used to be handed the target's own
    /// "requires sign-in" flag, so a fully configured Entra ID environment whose target happens not to force a
    /// sign-in announced "No authentication is configured" — directly above a card saying the detected provider
    /// matched the saved configuration.
    /// </summary>
    [Fact]
    public void AConfiguredEnvironmentIsNeverReportedAsUnconfigured()
    {
        var configured = new FrontendAuthenticationSettings
        {
            AuthenticationType = FrontendAuthenticationType.MicrosoftEntraId,
            RequiresAuthentication = false,
            ExpectedAuthority = "https://login.microsoftonline.com/tenant",
            ExpectedClientId = "22222222-2222-2222-2222-222222222222",
        };

        AuthenticationPaneStates.Configuration(configured).Should().Be(AuthConfigurationState.Configured);

        var summary = Summarize(configured: AuthenticationPaneStates.Configuration(configured));
        summary.State.Should().NotBe(AuthenticationReadiness.NotConfigured);
        summary.Detail.Should().NotContain("No authentication");
        summary.Label.Should().Be("Ready");
    }

    [Fact]
    public void TheNotConfiguredSentenceNamesTheConfiguration_NotTheTarget()
    {
        // "No authentication is configured for this Target Environment" read as a claim about the target application.
        // What is actually missing is a provider in BirkNext's own saved configuration.
        Summarize(configured: AuthConfigurationState.NotConfigured).Detail
            .Should().Be("No authentication provider is configured for this Target Environment.");
    }

    [Fact]
    public void AHalfWrittenConfigurationIsLimitedEvenWithEveryPrerequisiteInPlace()
    {
        var summary = Summarize(configured: AuthConfigurationState.Partial);
        summary.State.Should().Be(AuthenticationReadiness.Limited);
        summary.Detail.Should().Be("The saved authentication configuration is incomplete.");
        summary.Prerequisites.Should().OnlyContain(p => p.State == AuthPrerequisiteState.Ok,
            "the prerequisites really are in place; it is the configuration that is not");
    }

    /// <summary>
    /// Readiness is about prerequisites. Whether a person has confirmed the sign-in workflow is a separate answer on a
    /// separate card, and letting it decide this one is how "manual verification required" came to mean "not configured".
    /// </summary>
    [Fact]
    public void AnUnverifiedButFullyPreparedEnvironmentIsStillReady()
    {
        var summary = Summarize();
        summary.State.Should().Be(AuthenticationReadiness.Ready);
        summary.Label.Should().NotContain("verif");
        summary.Detail.Should().NotContain("verif");
    }

    [Fact]
    public void AHalfWrittenConfigurationStillShowsItsPrerequisites()
    {
        Summarize(configured: AuthConfigurationState.Partial).Prerequisites.Should().HaveCount(3);
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
