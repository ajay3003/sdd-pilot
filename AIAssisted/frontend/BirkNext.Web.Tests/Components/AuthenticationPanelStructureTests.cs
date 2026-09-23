using BirkNext.LocalHttpsProxy;
using BirkNext.Web.Components;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// The Authentication readiness panel as a rendered surface: what is visible without interacting, what is one
/// disclosure down, and what is never shown at all.
///
/// The rule the whole page hangs on is that readiness is understandable without reading runtime metadata. Process ids
/// and launch arguments answer "why is this not working" for someone already debugging; they must not be the way a
/// reader finds out whether they can test.
/// </summary>
public sealed class AuthenticationPanelStructureTests : BunitContext
{
    private static ProxyCertificateStatus Certificate(ProxyCertificateTrustState state = ProxyCertificateTrustState.Trusted) =>
        new() { State = state, InstallSupported = true, Thumbprint = "A1B2C3D4E5F60718293A4B5C6D7E8F9012345678", NotAfter = new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero) };

    private static LocalHttpsProxyStatus Proxy(
        DedicatedBrowserVerification edge = DedicatedBrowserVerification.Confirmed,
        LocalHttpsProxyState state = LocalHttpsProxyState.Ready,
        int port = 12345, int? edgePort = 12345, int? pid = 14520) => new()
        {
            State = state, RuntimeStatus = LocalHttpsProxyRuntimePhase.Running, Port = port, ProxyListening = true,
            RuntimeId = "runtime-abc123", ExpectedProxyPort = port, EdgeProxyPort = edgePort,
            ProxyArgumentConfigured = edgePort is not null, EdgeVerification = edge, EdgeProcessId = pid,
            EdgeProxyArgument = edge == DedicatedBrowserVerification.Confirmed ? DedicatedBrowserProxyArgument.Verified : DedicatedBrowserProxyArgument.Unknown,
            ObservedEdgeProxyEndpoint = edge == DedicatedBrowserVerification.Confirmed ? $"127.0.0.1:{port}" : null,
            EdgeProfileVerified = edge == DedicatedBrowserVerification.Confirmed ? true : null,
            EdgeProxyTraffic = edge == DedicatedBrowserVerification.Confirmed ? DedicatedBrowserProxyTraffic.NotObserved : DedicatedBrowserProxyTraffic.Unknown,
            EdgeRunning = edge is not DedicatedBrowserVerification.NotRunning,
            EdgeProfileDirectory = @"C:\Users\x\AppData\Local\BirkNext\LocalHttpsProxyEdgeProfile",
            Certificate = Certificate(),
            StartedAt = new DateTimeOffset(2026, 9, 21, 14, 22, 0, TimeSpan.Zero),
        };

    private IRenderedComponent<AuthenticationReadinessPanel> Render(
        LocalHttpsProxyStatus? proxy = null, ProxyCertificateStatus? certificate = null, bool loaded = true, int knownEndpoints = 0,
        AuthConfigurationState configuration = AuthConfigurationState.Configured,
        AuthVerificationState verification = AuthVerificationState.Required,
        AuthenticatedTestingState context = AuthenticatedTestingState.NotConnected)
    {
        var resolved = proxy ?? Proxy();
        var summary = AuthenticationReadinessPresentation.Summarize(
            configuration, AuthenticatedTestingMethod.LocalHttpsProxy, resolved, certificate ?? Certificate(), loaded);
        var state = new AuthenticationPaneState(
            configuration, AuthDetectionState.Detected, AuthenticationMatchState.MatchesSaved,
            verification, context,
            AuthenticatedApiAvailable: context == AuthenticatedTestingState.Ready,
            AuthenticatedDomAvailable: false,
            summary);
        return Render<AuthenticationReadinessPanel>(p => p
            .Add(c => c.State, state)
            .Add(c => c.Proxy, resolved)
            .Add(c => c.KnownEndpoints, knownEndpoints));
    }

    private static string Text(IRenderedComponent<AuthenticationReadinessPanel> cut, string testId) =>
        cut.Find($"[data-testid={testId}]").TextContent.Trim();

    // ── Technical details ────────────────────────────────────────────────────

    [Fact]
    public void TechnicalDetailsAreCollapsedByDefault()
    {
        var cut = Render();
        // ReviewDisclosure keeps its body in the DOM and hides it, so collapsed is an attribute rather than an absence.
        cut.FindAll("[data-testid=auth-technical-details] [hidden]").Should().NotBeEmpty();
    }

    [Fact]
    public void TechnicalDetailsCarryTheRuntimeIdentifiersThatExplainAFailure()
    {
        var details = Render().Find("[data-testid=auth-technical-facts]").TextContent;
        details.Should().Contain("runtime-abc123");
        details.Should().Contain("14520", "the Edge process id");
        details.Should().Contain("--proxy-server=127.0.0.1:12345", "the argument actually recorded at launch");
        details.Should().Contain("LocalHttpsProxyEdgeProfile");
        details.Should().Contain("A1B2C3D4E5F60718293A4B5C6D7E8F9012345678", "the certificate thumbprint");
    }

    [Fact]
    public void ReadinessIsUnderstandableWithoutOpeningTheTechnicalDetails()
    {
        var cut = Render();
        // The visible surface says the state in words. None of it requires reading a pid or an argument.
        Text(cut, "auth-readiness-state").Should().Be("Ready");
        Text(cut, "auth-status-browser").Should().Be("Ready to capture");
        cut.Find("[data-testid=auth-readiness-hero]").TextContent.Should().NotContain("--proxy-server");
        cut.Find("[data-testid=auth-readiness-hero]").TextContent.Should().NotContain("14520");
    }

    // Technical details keep BirkNext's launch record and the running process's own argument apart, and name the
    // source: never Edge Settings, never the Windows proxy.
    [Fact]
    public void TechnicalDetailsSeparateIntentFromEvidenceAndNameTheSource()
    {
        var cut = Render();
        Text(cut, "auth-tech-launch-record").Should().Be("--proxy-server=127.0.0.1:12345");
        Text(cut, "auth-tech-process-argument").Should().Be("--proxy-server=127.0.0.1:12345");
        Text(cut, "auth-tech-traffic").Should().Be("Not yet observed");
        Text(cut, "auth-tech-source").Should().Contain("Edge Settings, Edge policy and the Windows proxy are not read or changed");

        var unread = Render(Proxy(DedicatedBrowserVerification.NotConfirmed));
        Text(unread, "auth-tech-process-argument").Should().Be("Not read");
        Text(unread, "auth-tech-launch-record").Should().Be("--proxy-server=127.0.0.1:12345");
    }

    [Fact]
    public void ValuesThatDoNotExistAreShownAsAbsentRatherThanAsBlanks()
    {
        var details = Render(Proxy(DedicatedBrowserVerification.NotRunning, edgePort: null, pid: null))
            .Find("[data-testid=auth-technical-facts]").TextContent;
        details.Should().Contain("—");
        details.Should().NotContain("--proxy-server", "nothing was launched, so no argument was recorded");
    }

    [Fact]
    public void NoCredentialShapedValueIsEverRendered()
    {
        var markup = Render().Markup;
        // The technical details are diagnostics, never credentials.
        foreach (var forbidden in new[] { "Bearer", "Authorization", "Set-Cookie", "secret", "password" })
            markup.Should().NotContain(forbidden);
    }

    // ── The three answers in the hero ────────────────────────────────────────

    /// <summary>
    /// The regression this pane was rebuilt for. Configuration, verification and testing context are three questions
    /// with three answers, and the hero states each one without borrowing another's word. The page used to lead with
    /// "Not configured" because the target did not force a sign-in, while the card below reported that the detected
    /// provider matched the saved configuration.
    /// </summary>
    [Fact]
    public void TheHeroStatesConfigurationVerificationAndContextSeparately()
    {
        var cut = Render(verification: AuthVerificationState.Required, context: AuthenticatedTestingState.Ready);

        Text(cut, "auth-summary-configuration").Should().Be("Configured");
        Text(cut, "auth-summary-verification").Should().Be("Manual verification required");
        Text(cut, "auth-summary-context").Should().Be("Ready");

        // Verification still being required does not make the pane, or the configuration, unconfigured.
        Text(cut, "auth-readiness-state").Should().Be("Ready");
        cut.Find("[data-testid=auth-readiness-hero]").TextContent.Should().NotContain("No authentication");
    }

    [Fact]
    public void AnUnconfiguredEnvironmentSaysWhichConfigurationIsMissing()
    {
        var cut = Render(configuration: AuthConfigurationState.NotConfigured, verification: AuthVerificationState.NotApplicable);

        Text(cut, "auth-summary-configuration").Should().Be("Not configured");
        Text(cut, "auth-readiness-detail").Should().Be("No authentication provider is configured for this Target Environment.");
        // The prerequisites are the method's, not the provider's, so they stay visible and operable.
        cut.FindAll("[data-testid=auth-prerequisites]").Should().ContainSingle();
        cut.Find("[data-testid=auth-verify]").HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public void AnAvailableApiContextIsNotReportedAsAnAvailableBrowserDom()
    {
        var api = new AuthenticationPaneState(
            AuthConfigurationState.Configured, AuthDetectionState.Detected, AuthenticationMatchState.MatchesSaved,
            AuthVerificationState.Required, AuthenticatedTestingState.Partial,
            AuthenticatedApiAvailable: true, AuthenticatedDomAvailable: false,
            AuthenticationReadinessPresentation.Summarize(AuthConfigurationState.Configured,
                AuthenticatedTestingMethod.LocalHttpsProxy, Proxy(), Certificate(), loaded: true));

        api.AuthenticatedApiAvailable.Should().BeTrue();
        api.AuthenticatedDomAvailable.Should().BeFalse("proxy traffic never produces a DOM");
        api.TestingContext.Should().Be(AuthenticatedTestingState.Partial,
            "an API context without an observed endpoint is partial access, not full access");
    }

    // ── Prerequisites as cards, not as a wizard ──────────────────────────────

    [Fact]
    public void PrerequisitesAreThreeStatusCardsUnderOneHeadingAndAreNeverNumbered()
    {
        var cut = Render();
        var section = cut.Find("[data-testid=auth-prerequisites]");

        section.QuerySelector("#auth-prerequisites-heading")!.TextContent.Trim().Should().Be("Prerequisites");
        section.QuerySelectorAll(".ar-card").Should().HaveCount(3);
        foreach (var id in new[] { "certificate", "proxy", "browser" })
            cut.FindAll($"[data-testid=auth-card-{id}]").Should().ContainSingle();

        foreach (var step in new[] { "Step 1", "Step 2", "Step 3", "Step 4" })
            cut.Markup.Should().NotContain(step, "the setup is not a linear wizard once the system is operational");
    }

    // ── Verification action ──────────────────────────────────────────────────

    [Fact]
    public void VerifyIsOfferedOnlyWhenThePrerequisitesAreActuallyReady()
    {
        Render().Find("[data-testid=auth-verify]").HasAttribute("disabled").Should().BeFalse();
        Render().FindAll("[data-testid=auth-verify-blocked]").Should().BeEmpty();
    }

    [Fact]
    public void ADisabledVerifyAlwaysNamesWhatIsMissing()
    {
        var cut = Render(Proxy(DedicatedBrowserVerification.NotRunning, LocalHttpsProxyState.Stopped, edgePort: null));
        cut.Find("[data-testid=auth-verify]").HasAttribute("disabled").Should().BeTrue();
        Text(cut, "auth-verify-blocked").Should().Contain("local https proxy");
    }

    // ── No duplicated warnings ───────────────────────────────────────────────

    [Fact]
    public void AProblemIsExplainedOnItsOwnCardAndNotRepeatedInTheHero()
    {
        var cut = Render(Proxy(DedicatedBrowserVerification.NotRunning, LocalHttpsProxyState.Stopped, edgePort: null));

        // The hero counts what needs attention; the card carries the sentence and the action.
        Text(cut, "auth-readiness-detail").Should().Be("1 prerequisite needs attention.");
        Text(cut, "auth-card-proxy").Should().Contain("cannot be captured until the proxy is started");
        cut.Find("[data-testid=auth-readiness-hero]").TextContent
            .Should().NotContain("cannot be captured", "the hero counts problems; it does not restate them");
        cut.FindAll("[data-testid=auth-action-proxy]").Should().ContainSingle("one state, one action");
    }

    // ── Endpoint Discovery hand-off ──────────────────────────────────────────

    [Fact]
    public void EndpointDiscoveryKeepsItsCanonicalNameAndIsNeverDisabledByTheProxy()
    {
        var cut = Render(Proxy(DedicatedBrowserVerification.NotRunning, LocalHttpsProxyState.Stopped, edgePort: null), knownEndpoints: 7);
        var cta = cut.Find("[data-testid=auth-discovery-cta]").TextContent;
        cta.Should().Contain("Endpoint Discovery");
        cta.Should().NotContain("Endpoint Detection");
        Text(cut, "auth-discovery-status").Should().Contain("setup incomplete");
        Text(cut, "auth-discovery-note").Should().Contain("still available");
        cut.Find("[data-testid=auth-open-discovery]").HasAttribute("disabled").Should().BeFalse();
    }

    // ── Loading ──────────────────────────────────────────────────────────────

    [Fact]
    public void BeforeTheBackendAnswersNothingIsStatedAsAFact()
    {
        var cut = Render(loaded: false);
        Text(cut, "auth-readiness-state").Should().Be("Checking…");
        var markup = cut.Markup;
        foreach (var premature in new[] { "Not running", "Not installed", "Proxy not active" })
            markup.Should().NotContain(premature, "a first paint must not report a state it has not read");
    }

    [Fact]
    public void AnUnknownCertificateIsLimitedRatherThanFailed()
    {
        var cut = Render(certificate: Certificate(ProxyCertificateTrustState.Unknown));
        Text(cut, "auth-readiness-state").Should().Be("Limited");
        Text(cut, "auth-status-certificate").Should().Be("Unknown");
        cut.Find("[data-testid=auth-card-certificate]").GetAttribute("class").Should().NotContain("error");
    }

    // ── Accessibility ────────────────────────────────────────────────────────

    [Fact]
    public void EveryStatusIsCarriedByTextAndIconsAreHiddenFromAssistiveTechnology()
    {
        var cut = Render();
        foreach (var icon in cut.FindAll(".ar-icon"))
            icon.GetAttribute("aria-hidden").Should().Be("true");
        // The chips say the state in words, so colour is never the only carrier.
        Text(cut, "auth-status-certificate").Should().Be("Trusted");
        Text(cut, "auth-status-proxy").Should().Be("Running");
        cut.Find("[data-testid=auth-readiness]").GetAttribute("aria-label").Should().Be("Authentication readiness");
    }
}
