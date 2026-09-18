using AngleSharp.Dom;
using BirkNext.LocalHttpsProxy;
using BirkNext.ManagedEdge;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Component = BirkNext.Web.Components.FrontendAnalysisSettings;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// Authenticated testing has to be readable in a few seconds. The card shows five things and nothing else:
/// state, method, what to do about the browser, what access exists, and a way in to the rest.
///
/// The load-bearing distinction is between two facts that look alike and are not: the local proxy SERVER being
/// ready, and managed EDGE being configured to route through it. BirkNext cannot read Edge's settings, so the
/// only honest positive signal is traffic that actually arrived at the proxy. Until then the card instructs
/// rather than claims — it never reports the browser as configured because a server is listening.
/// </summary>
public sealed class AuthenticatedTestingDensityTests : BunitContext
{
    private const string Url = "https://m2lbdev.bufetat.no/";
    private const string Origin = "https://m2lbdev.bufetat.no";
    private const string DevOnlyNote =
        "DEV-only authenticated API testing. BirkNext temporarily processes approved bearer credentials in memory but never displays, logs or saves them.";

    private readonly FrontendAnalysisSettingsService _settings = new();
    private readonly Mock<ITargetEnvironmentDetectionApiService> _detector = new();
    private readonly Mock<IManagedEdgeCdpApiService> _edgeApi = new();
    private readonly Mock<ILocalHttpsProxyApiService> _proxyApi = new();
    private readonly ManagedEdgeRuntime _edgeRuntime;
    private readonly LocalHttpsProxyRuntime _proxyRuntime;

    /// <summary>The proxy server is up and nothing has come through it: a listening server, no browser evidence.</summary>
    private static readonly LocalHttpsProxyStatus Listening = new()
    {
        SessionId = "proxy-session", State = LocalHttpsProxyState.Listening, Port = 8888,
        LocalIntegrationAvailable = true, EnvironmentAllowed = true, PortAvailable = true, CanStart = true,
        ApprovedHosts = ["m2lbdev.bufetat.no:443"], TargetOrigin = Origin,
        Certificate = new() { State = ProxyCertificateTrustState.Trusted, InstallSupported = true },
    };

    /// <summary>Requests have reached the proxy, which is the only thing that proves Edge was pointed at it.</summary>
    private static readonly LocalHttpsProxyStatus Routed = Listening with
    {
        State = LocalHttpsProxyState.Ready, InterceptedRequests = 4, AuthenticatedRequestsObserved = 1,
        AuthenticatedCredentialAvailable = true, CredentialObservedHost = "m2lbdev.bufetat.no",
        CredentialObservedAt = DateTimeOffset.UtcNow, CredentialExpiresAt = DateTimeOffset.UtcNow.AddMinutes(25),
        Evidence = "Authenticated API context available (memory only).",
    };

    public AuthenticatedTestingDensityTests()
    {
        _edgeRuntime = new ManagedEdgeRuntime(_edgeApi.Object);
        _proxyRuntime = new LocalHttpsProxyRuntime(_proxyApi.Object);
        Services.AddSingleton<IFrontendAnalysisSettingsService>(_settings);
        Services.AddSingleton(_detector.Object);
        Services.AddSingleton(_edgeRuntime);
        Services.AddSingleton(_proxyRuntime);
        Services.AddSingleton<IEndpointDiscoveryService, EndpointDiscoveryService>();
        JSInterop.SetupVoid("birkNextStorage.setItem", _ => true).SetVoidResult();
        JSInterop.SetupVoid("birkNextStorage.setDiscovery", _ => true).SetVoidResult();
        JSInterop.Setup<string?>("birkNextStorage.getDiscovery").SetResult(null);
        _proxyApi.Setup(a => a.CheckCompatibilityAsync(It.IsAny<LocalHttpsProxyScopeRequest>()))
            .ReturnsAsync(Listening with { SessionId = null, State = LocalHttpsProxyState.NotStarted, Port = 0 });
        _proxyApi.Setup(a => a.StartAsync(It.IsAny<LocalHttpsProxyScopeRequest>())).ReturnsAsync(Routed);
        _proxyApi.Setup(a => a.StatusAsync(It.IsAny<LocalHttpsProxySessionRequest>())).ReturnsAsync(Routed);
    }

    private IRenderedComponent<Component> Open(AuthenticatedTestingMethod method = AuthenticatedTestingMethod.LocalHttpsProxy)
    {
        var authentication = $$"""
            {"authenticationType":"MicrosoftEntraId","requiresAuthentication":true,"authenticatedTestingMethod":"{{method}}"}
            """;
        JSInterop.Setup<string?>("birkNextStorage.getItem", _ => true).SetResult($$"""
            {"activeProfileId":"local","profiles":[
              {"id":"local","name":"Local","targetUrl":"https://local.example.com"},
              {"id":"dev","name":"M2LB DEV","environmentType":"Development","targetUrl":"{{Url}}","authentication":{{authentication}}}
            ]}
            """);
        var cut = Render<Component>();
        cut.FindAll(".fa-profile-chip").Single(b => b.TextContent.Contains("M2LB DEV")).Click();
        cut.FindAll("[role=tab]").Single(t => t.TextContent.Trim() == "Authentication").Click();
        return cut;
    }

    private static IElement Card(IRenderedComponent<Component> cut) => cut.Find("[data-testid='authenticated-testing']");
    private static IReadOnlyList<IElement> All(IRenderedComponent<Component> cut, string id) => cut.FindAll($"[data-testid='{id}']");
    private static string Row(IRenderedComponent<Component> cut, string id) => cut.Find($"[data-testid='{id}']").TextContent.Trim();
    private static bool Has(IRenderedComponent<Component> cut, string id) => cut.FindAll($"[data-testid='{id}']").Count > 0;

    /// <summary>What the reader sees before opening anything: a collapsed disclosure body is <c>hidden</c>.</summary>
    private static IElement Primary(IRenderedComponent<Component> cut)
    {
        var clone = (IElement)Card(cut).Clone(true);
        foreach (var collapsed in clone.QuerySelectorAll("[hidden], details:not([open])").ToList()) collapsed.Remove();
        return clone;
    }

    private static string PrimaryText(IRenderedComponent<Component> cut) => Primary(cut).TextContent;

    private static void OpenSetupDetails(IRenderedComponent<Component> cut) =>
        cut.Find("[data-testid='authenticated-testing-setup-toggle']").Click();

    /// <summary>Starts the proxy from the panel itself, so the runtime session is bound to the real profile.</summary>
    private static void StartProxy(IRenderedComponent<Component> cut)
    {
        OpenSetupDetails(cut);
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Start authenticated proxy").Click();
        cut.WaitForAssertion(() => Row(cut, "proxy-state").Should().NotBe("Not started"));
    }

    // ── §31. The primary surface is five things ──────────────────────────────────────────────

    [Fact]
    public void ThePrimarySurfaceShowsStateMethodGuidanceAccessAndAWayIn()
    {
        var cut = Open();

        Row(cut, "authenticated-testing-state").Should().Be("Not connected");
        Row(cut, "authenticated-testing-method-value").Should().Be(AuthenticatedTestingMethodLabels.Option(AuthenticatedTestingMethod.LocalHttpsProxy));
        All(cut, "authenticated-testing-proxy-guidance").Should().ContainSingle();
        All(cut, "authenticated-testing-access").Should().ContainSingle();
        All(cut, "authenticated-testing-setup-toggle").Should().ContainSingle();

        // Three access rows, no finer grain: REST, GraphQL and the certificate belong in the details.
        Row(cut, "testing-access-public").Should().Be("Available");
        Row(cut, "testing-access-api").Should().Be("Not available");
        Row(cut, "testing-access-dom").Should().Be("Not available");
    }

    [Fact]
    public void TheLongExplanationAndTheRuntimePanelStayOutOfThePrimarySurface()
    {
        var cut = Open();
        var primary = PrimaryText(cut);

        primary.Should().NotContain(AuthenticatedTestingStates.Purpose);
        primary.Should().NotContain(DevOnlyNote);
        primary.Should().NotContain("Start authenticated proxy");
        primary.Should().NotContain(AuthenticatedTestingStates.EndpointHandoff);
        // No warning-styled strip on a card whose state is merely "not set up yet".
        Primary(cut).QuerySelectorAll(".fa-section-note").Should().BeEmpty();
    }

    // ── §34. One card, not two ───────────────────────────────────────────────────────────────

    [Fact]
    public void MethodIsPartOfTheAuthenticatedTestingCardAndNotASecondCard()
    {
        var cut = Open();

        Has(cut, "authenticated-testing-method").Should().BeFalse("method is a row of the authenticated testing card now");
        All(cut, "authenticated-testing").Should().ContainSingle();
        cut.FindAll("h3").Count(h => h.TextContent.Trim() == "Authenticated testing").Should().Be(1);
        // Everything the deleted card carried is still reachable, one disclosure away.
        OpenSetupDetails(cut);
        Has(cut, "authenticated-testing-method-others").Should().BeTrue();
        Has(cut, "authenticated-testing-method-help").Should().BeTrue();
        Has(cut, "local-https-proxy-panel").Should().BeTrue();
    }

    // ── §32, §35. Proxy server ready is not Edge configured ──────────────────────────────────

    [Fact]
    public void AListeningProxyServerNeverReportsTheBrowserAsConfigured()
    {
        // A listening server with nothing routed through it: the only honest output is an instruction.
        AuthenticatedTestingStates.BrowserRoutedThroughProxy(Listening).Should().BeFalse();
        AuthenticatedTestingStates.ProxyGuidanceTitle(Listening).Should().Be("Enable proxy in Edge settings");
        AuthenticatedTestingStates.ProxyGuidanceText(Listening)
            .Should().Be("Use the BirkNext local HTTPS proxy in managed Edge to collect authenticated API traffic.");

        var cut = Open();
        Row(cut, "authenticated-testing-proxy-guidance-title").Should().Be("Enable proxy in Edge settings");
        PrimaryText(cut).Should().NotContainAny("Edge proxy active", "Proxy enabled automatically", "Edge proxy configured");
    }

    [Fact]
    public void ObservedTrafficIsTheOnlyThingThatProvesEdgeIsRouted()
    {
        AuthenticatedTestingStates.BrowserRoutedThroughProxy(Routed).Should().BeTrue();
        // Each signal alone is sufficient, because each of them can only exist if a request arrived.
        AuthenticatedTestingStates.BrowserRoutedThroughProxy(Listening with { AuthenticatedRequestsObserved = 1 }).Should().BeTrue();
        AuthenticatedTestingStates.BrowserRoutedThroughProxy(Listening with { AuthenticatedCredentialAvailable = true }).Should().BeTrue();
        // A running, trusted, port-available server on its own proves nothing about the browser.
        AuthenticatedTestingStates.BrowserRoutedThroughProxy(Listening with { State = LocalHttpsProxyState.Ready }).Should().BeFalse();
    }

    [Fact]
    public void TheGuidanceFollowsTheStateOnceTrafficHasBeenSeen()
    {
        var cut = Open();
        StartProxy(cut);

        cut.WaitForAssertion(() => Row(cut, "authenticated-testing-proxy-guidance-title").Should().Be("Edge proxy configured"));
        Row(cut, "authenticated-testing-proxy-guidance-text")
            .Should().Be("Authenticated API traffic can be collected through the BirkNext proxy.");
        Row(cut, "authenticated-testing-edge-setup")
            .Should().Be("Traffic has been observed through the proxy, so managed Edge is routed through it.");
        // And the access summary moves with it, because a credential was actually captured.
        Row(cut, "testing-access-api").Should().Be("Available");
    }

    // ── §32. The callout is informational, shared, and belongs to one method ─────────────────

    [Fact]
    public void TheCalloutIsInformationalAndUsesTheSharedPrimitive()
    {
        var cut = Open();
        var callout = cut.Find("[data-testid='authenticated-testing-proxy-guidance']");

        callout.ClassList.Should().Contain("callout").And.Contain("callout-info");
        callout.GetAttribute("data-kind").Should().Be("Info");
        callout.HasAttribute("role").Should().BeFalse("a setup step the reader performs is not an alert");
        foreach (var tone in new[] { "warn", "error", "danger" })
            callout.ClassList.Should().NotContain(c => c.Contains(tone, StringComparison.OrdinalIgnoreCase));

        var icon = callout.QuerySelector(".callout-icon")!;
        icon.GetAttribute("aria-hidden").Should().Be("true");
        // The message survives without the icon.
        Row(cut, "authenticated-testing-proxy-guidance-title").Should().NotBeEmpty();
    }

    [Theory]
    [InlineData(AuthenticatedTestingMethod.ManagedEdgeCdp)]
    [InlineData(AuthenticatedTestingMethod.ManualOnly)]
    public void MethodsWithoutAProxyAreNeverToldToConfigureOne(AuthenticatedTestingMethod method)
    {
        var cut = Open(method);

        Has(cut, "authenticated-testing-proxy-guidance").Should().BeFalse();
        PrimaryText(cut).Should().NotContain("proxy");
        OpenSetupDetails(cut);
        Has(cut, "authenticated-testing-edge-setup").Should().BeFalse();
    }

    // ── §33. The details carry the procedure, and the security note is untouched ─────────────

    [Fact]
    public void TheDetailsHoldThePurposeSetupAndHandoff()
    {
        var cut = Open();
        OpenSetupDetails(cut);

        Row(cut, "authenticated-testing-purpose").Should().Be(AuthenticatedTestingStates.Purpose);
        Row(cut, "authenticated-testing-edge-setup")
            .Should().Be("Configure managed Edge to use the BirkNext local HTTPS proxy endpoint shown below.");
        Row(cut, "authenticated-testing-discovery-link").Should().Contain(AuthenticatedTestingStates.EndpointHandoff);
        Has(cut, "authenticated-testing-open-discovery").Should().BeTrue();
    }

    [Fact]
    public void TheDevOnlyCredentialNoteSurvivesTheRestructureWordForWord()
    {
        var cut = Open();
        OpenSetupDetails(cut);

        Row(cut, "proxy-security-warning").Should().Be(DevOnlyNote);
        cut.Find("[data-testid='authenticated-testing-method-help']").TextContent.Should().Contain("DEV only");
        AuthenticatedTestingMethodLabels.ProxySecurityWarning.Should().Be(DevOnlyNote);
    }

    // ── §37. One sentence per subject ────────────────────────────────────────────────────────

    [Fact]
    public void TheBrowserSetupIsExplainedOncePerSurface()
    {
        var cut = Open();
        OpenSetupDetails(cut);
        var text = Card(cut).TextContent;

        Occurrences(text, "does not change").Should().Be(1, "the panel states what BirkNext will not do; the row states what the user must do");
        Occurrences(text, AuthenticatedTestingStates.Purpose).Should().Be(1);
        Occurrences(text, DevOnlyNote).Should().Be(1);
    }

    [Fact]
    public void TheStateIsNamedOnceOnTheCardAndEchoedOnlyAsTheDisclosureHint()
    {
        var cut = Open();
        var label = AuthenticatedTestingStates.Label(AuthenticatedTestingState.NotConnected);

        Row(cut, "authenticated-testing-state").Should().Be(label);
        // The hint on the toggle is the one deliberate repeat: it tells the reader what is inside.
        cut.Find("[data-testid='authenticated-testing-setup-toggle'] .disclosure-hint").TextContent.Trim().Should().Be(label);
        Occurrences(PrimaryText(cut), label).Should().Be(2);
    }

    // ── Wording that would be a claim rather than an instruction ─────────────────────────────

    [Fact]
    public void NoWordingPromisesAutomaticBrowserConfigurationOrRestoration()
    {
        foreach (var proxy in new[] { Listening, Routed })
        foreach (var copy in new[]
                 {
                     AuthenticatedTestingStates.ProxyGuidanceTitle(proxy),
                     AuthenticatedTestingStates.ProxyGuidanceText(proxy),
                     AuthenticatedTestingStates.EdgeBrowserSetup(proxy),
                 })
            copy.Should().NotContainAny(
                "automatically", "Previous settings will be restored", "Proxy enabled automatically", "will be restored");
    }

    private static int Occurrences(string haystack, string needle)
    {
        var count = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal)) count++;
        return count;
    }
}
