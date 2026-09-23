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
/// The Authentication pane as one coherent surface.
///
/// The defect these tests exist for: the pane led with "Authentication testing — Not configured — No authentication is
/// configured for this Target Environment" while the card immediately below reported "Microsoft Entra ID — Detected —
/// Matches saved configuration". Both sentences were generated correctly; they were answers to different questions that
/// the page had merged. Readiness was handed the target's own "requires sign-in" flag in place of the saved
/// configuration, and once that is untangled the rest of the page follows: one configuration section instead of two,
/// verification that reports how verification is done rather than how detection was done, and an authenticated context
/// that keeps the API and the DOM apart.
///
/// Configuration != Detection != Verification != Testing context != Readiness. All five can disagree, correctly.
/// </summary>
public sealed class AuthenticationPaneSemanticsTests : BunitContext
{
    private const string Url = "https://m2lbdev.example.com/";
    private const string Authority = "https://login.microsoftonline.com/11111111-1111-1111-1111-111111111111";
    private const string Tenant = "11111111-1111-1111-1111-111111111111";
    private const string ClientId = "22222222-2222-2222-2222-222222222222";

    private readonly FrontendAnalysisSettingsService _settings = new();
    private readonly Mock<ITargetEnvironmentDetectionApiService> _detector = new();
    private readonly Mock<ILocalHttpsProxyApiService> _proxyApi = new();
    private readonly Mock<IManagedEdgeCdpApiService> _edgeApi = new();
    private readonly LocalHttpsProxyRuntime _proxyRuntime;

    /// <summary>A prepared runtime: certificate trusted, proxy listening, dedicated Edge confirmed on this port.</summary>
    private static readonly LocalHttpsProxyStatus Prepared = new()
    {
        SessionId = "proxy-session", RuntimeId = "runtime-abc123", State = LocalHttpsProxyState.Listening,
        RuntimeStatus = LocalHttpsProxyRuntimePhase.Running, Port = 8888, ProxyListening = true,
        ExpectedProxyPort = 8888, EdgeProxyPort = 8888, ProxyArgumentConfigured = true, EdgeProcessId = 14520,
        EdgeVerification = DedicatedBrowserVerification.Confirmed, EdgeRunning = true,
        EdgeProxyArgument = DedicatedBrowserProxyArgument.Verified, ObservedEdgeProxyEndpoint = "127.0.0.1:8888", EdgeProfileVerified = true,
        EdgeProxyTraffic = DedicatedBrowserProxyTraffic.NotObserved,
        EdgeProfileDirectory = @"C:\Users\x\AppData\Local\BirkNext\LocalHttpsProxyEdgeProfile",
        LocalIntegrationAvailable = true, EnvironmentAllowed = true, PortAvailable = true, CanStart = true,
        ApprovedHosts = ["m2lbdev.example.com:443"], TargetOrigin = "https://m2lbdev.example.com",
        StartedAt = new DateTimeOffset(2026, 9, 22, 8, 0, 10, TimeSpan.Zero),
        Certificate = new()
        {
            State = ProxyCertificateTrustState.Trusted, InstallSupported = true,
            Thumbprint = "A1B2C3D4E5F60718293A4B5C6D7E8F9012345678",
            NotAfter = new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero),
        },
    };

    /// <summary>Prepared, and an authenticated request has actually come through: an API context, never a DOM.</summary>
    private static readonly LocalHttpsProxyStatus WithApiContext = Prepared with
    {
        State = LocalHttpsProxyState.Ready, InterceptedRequests = 6, AuthenticatedRequestsObserved = 2,
        AuthenticatedCredentialAvailable = true, CredentialObservedHost = "m2lbdev.example.com",
        CredentialObservedAt = new DateTimeOffset(2026, 9, 22, 8, 10, 50, TimeSpan.Zero),
        CredentialExpiresAt = new DateTimeOffset(2026, 9, 22, 8, 40, 50, TimeSpan.Zero),
        Evidence = "Authenticated API context available (memory only).",
    };

    public AuthenticationPaneSemanticsTests()
    {
        _proxyRuntime = new LocalHttpsProxyRuntime(_proxyApi.Object);
        Services.AddSingleton<IFrontendAnalysisSettingsService>(_settings);
        Services.AddSingleton(_detector.Object);
        Services.AddSingleton(_proxyRuntime);
        Services.AddSingleton(new ManagedEdgeRuntime(_edgeApi.Object));
        Services.AddSingleton<IEndpointDiscoveryService, EndpointDiscoveryService>();
        JSInterop.SetupVoid("birkNextStorage.setItem", _ => true).SetVoidResult();
        JSInterop.SetupVoid("birkNextStorage.setDiscovery", _ => true).SetVoidResult();
        JSInterop.Setup<string?>("birkNextStorage.getDiscovery").SetResult(null);
        _detector.Setup(x => x.DetectFromUrlAsync(Url, default)).ReturnsAsync(Detection);
    }

    private static TargetEnvironmentDetectionResult Detection() => new()
    {
        OriginalUrl = Url, Success = true, Reachability = TargetReachability.Reachable,
        Confidence = DetectionConfidence.High,
        DetectedClientFramework = ClientFrameworkType.BlazorWebAssembly,
        SuggestedEnvironmentType = FrontendEnvironmentType.Development,
        State = DetectionState.ManualAuthenticationVerificationRequired,
        ManualAuthenticationVerificationRequired = true,
        ManualAuthenticationVerificationStatus = ManualAuthenticationVerificationStatus.Required,
        AuthenticationRequired = true,
        DetectedAuthenticationType = FrontendAuthenticationType.MicrosoftEntraId,
        DetectedAuthority = Authority, DetectedTenantId = Tenant, DetectedClientId = ClientId,
    };

    /// <summary>Entra ID saved with the identifiers detection reports, and the proxy as the testing method.</summary>
    private const string Configured = $$"""
        { "authenticationType": "MicrosoftEntraId", "requiresAuthentication": true,
          "expectedAuthority": "{{Authority}}", "expectedTenant": "{{Tenant}}", "expectedClientId": "{{ClientId}}",
          "authenticatedTestingMethod": "LocalHttpsProxy" }
        """;

    /// <summary>A provider with none of the identifiers it needs: saved, but not enough to check anything against.</summary>
    private const string PartiallyConfigured = """
        { "authenticationType": "MicrosoftEntraId", "requiresAuthentication": true,
          "authenticatedTestingMethod": "LocalHttpsProxy" }
        """;

    private const string NotConfigured = """
        { "authenticationType": "None", "requiresAuthentication": false, "authenticatedTestingMethod": "LocalHttpsProxy" }
        """;

    private void Runtime(LocalHttpsProxyStatus status)
    {
        _proxyApi.Setup(a => a.CheckCompatibilityAsync(It.IsAny<LocalHttpsProxyScopeRequest>())).ReturnsAsync(status);
        _proxyApi.Setup(a => a.StatusAsync(It.IsAny<LocalHttpsProxySessionRequest>())).ReturnsAsync(status);
        _proxyApi.Setup(a => a.StartAsync(It.IsAny<LocalHttpsProxyScopeRequest>())).ReturnsAsync(status);
    }

    private IRenderedComponent<Component> Open(string authentication, bool detect = false)
    {
        JSInterop.Setup<string?>("birkNextStorage.getItem", _ => true).SetResult($$"""
        {"activeProfileId":"local","profiles":[
          {"id":"local","name":"Local","targetUrl":"https://local.example.com"},
          {"id":"dev","name":"M2LB DEV","environmentType":"Development","targetUrl":"{{Url}}","authentication":{{authentication}}}
        ]}
        """);
        var cut = Render<Component>();
        cut.FindAll(".fa-profile-chip").Single(b => b.TextContent.Contains("M2LB DEV")).Click();
        if (detect)
        {
            cut.FindAll("button").Single(b => b.TextContent.Trim() == "Detect settings").Click();
            cut.WaitForAssertion(() => cut.Markup.Should().Contain("Detected from target"));
        }
        cut.FindAll("[role=tab]").Single(t => t.TextContent.Trim() == "Authentication").Click();
        return cut;
    }

    private static IElement El(IRenderedComponent<Component> cut, string id) => cut.Find($"[data-testid='{id}']");
    private static string Row(IRenderedComponent<Component> cut, string id) => El(cut, id).TextContent.Trim();
    private static bool Has(IRenderedComponent<Component> cut, string id) => cut.FindAll($"[data-testid='{id}']").Count > 0;

    /// <summary>What the reader sees: hidden disclosure bodies and closed &lt;details&gt; are not on the page.</summary>
    private static string Visible(IRenderedComponent<Component> cut)
    {
        var clone = (IElement)cut.Find("[data-testid='fa-auth-panel']").Clone(true);
        foreach (var collapsed in clone.QuerySelectorAll("[hidden], details:not([open])").ToList()) collapsed.Remove();
        return clone.TextContent;
    }

    // ── §37. The contradiction, pinned ───────────────────────────────────────────────────────

    /// <summary>
    /// The exact defect. Entra ID is detected, the saved configuration matches it, and no part of the page may say the
    /// environment has no authentication configured.
    /// </summary>
    [Fact]
    public void DetectedAndMatchingAuthenticationIsNeverAccompaniedByNoAuthenticationConfigured()
    {
        Runtime(Prepared);
        var cut = Open(Configured, detect: true);

        Row(cut, "authentication-match-summary").Should().Contain("matches saved configuration");
        Row(cut, "authentication-detection-state").Should().Be("Detected");

        var visible = Visible(cut);
        visible.Should().NotContain("No authentication is configured");
        visible.Should().NotContain("No authentication provider is configured");
        Row(cut, "auth-readiness-state").Should().NotBe("Not configured");
        Row(cut, "auth-summary-configuration").Should().Be("Configured");
    }

    // ── §38. The four concepts, displayed independently ──────────────────────────────────────

    // 1. Configured + detected + unverified.
    [Fact]
    public void ConfiguredAndDetectedButUnverifiedShowsAllThreeAnswersAtOnce()
    {
        Runtime(Prepared);
        var cut = Open(Configured, detect: true);

        Row(cut, "authentication-configuration-state").Should().Be("Configured");
        Row(cut, "authentication-detection-state").Should().Be("Detected");
        Row(cut, "auth-summary-verification").Should().Be("Manual verification required");
        // A live session with nothing through it yet is not the same as no context at all.
        Row(cut, "auth-summary-context").Should().Be("Waiting for authenticated traffic");
        Row(cut, "testing-access-api").Should().Be("Not available");
        // The summary row and the card badge are the same derivation, so they cannot drift apart.
        Row(cut, "auth-summary-context").Should().Be(Row(cut, "authenticated-testing-state"));
        // A credential that has run out is not the same as never having had one.
        AuthenticationPaneStates.TestingContext(AuthenticatedTestingState.NotConnected, credentialExpired: true)
            .Should().Be(AuthenticatedTestingState.Expired);
        AuthenticationPaneStates.TestingContext(AuthenticatedTestingState.Ready, credentialExpired: false)
            .Should().Be(AuthenticatedTestingState.Ready);
    }

    // 2. Configured + detected + verified.
    [Fact]
    public void ARecordedPassingVerificationIsReportedAsVerifiedWithoutTouchingTheOtherThree()
    {
        AuthenticationPaneStates.Verification(required: true, ManualAuthenticationVerificationStatus.Passed)
            .Should().Be(AuthVerificationState.Verified);
        AuthenticationPaneStates.VerificationLabel(AuthVerificationState.Verified).Should().Be("Verified");
        AuthenticationPaneStates.Verification(required: true, ManualAuthenticationVerificationStatus.Failed)
            .Should().Be(AuthVerificationState.Failed);
        // A verification nobody asked for is Not required, which is not the same as Not done.
        AuthenticationPaneStates.Verification(required: false, ManualAuthenticationVerificationStatus.Required)
            .Should().Be(AuthVerificationState.NotApplicable);
    }

    // 3. Configured + detection unavailable.
    [Fact]
    public void ConfigurationSurvivesDetectionNeverHavingRun()
    {
        Runtime(Prepared);
        var cut = Open(Configured);

        Row(cut, "authentication-configuration-state").Should().Be("Configured");
        Row(cut, "authentication-detection-state").Should().Be("Not run");
        Row(cut, "authentication-config-source").Should().Be("Not available");
        Visible(cut).Should().NotContain("No authentication provider is configured");
    }

    // 4. Not configured.
    [Fact]
    public void AnEnvironmentWithNoProviderSaysSoOnceAndInTheRightWords()
    {
        Runtime(Prepared);
        var cut = Open(NotConfigured);

        Row(cut, "authentication-configuration-state").Should().Be("Not configured");
        Row(cut, "auth-summary-configuration").Should().Be("Not configured");
        Row(cut, "auth-readiness-detail").Should().Be("No authentication provider is configured for this Target Environment.");
        // The sentence names BirkNext's own configuration, not the target application.
        Row(cut, "auth-readiness-detail").Should().NotContain("No authentication is configured");
    }

    [Fact]
    public void AProviderWithoutItsIdentifiersIsPartialRatherThanConfigured()
    {
        Runtime(Prepared);
        var cut = Open(PartiallyConfigured);

        Row(cut, "authentication-configuration-state").Should().Be("Partially configured");
        Row(cut, "auth-readiness-state").Should().Be("Limited");
        Row(cut, "auth-readiness-detail").Should().Be("The saved authentication configuration is incomplete.");
    }

    // 5. An authenticated API context while manual verification is still required.
    [Fact]
    public void AnAuthenticatedApiContextDoesNotMarkVerificationDone()
    {
        Runtime(WithApiContext);
        var cut = Open(Configured, detect: true);
        cut.Find("[data-testid='auth-action-proxy']").Click();

        cut.WaitForAssertion(() => Row(cut, "testing-access-api").Should().Be("Available"));
        Row(cut, "auth-summary-context").Should().Be(Row(cut, "authenticated-testing-state"));
        // Runtime evidence is not a human confirmation, and never becomes one.
        Row(cut, "auth-summary-verification").Should().Be("Manual verification required");
        Row(cut, "authentication-verification-last").Should().Be("Never");
    }

    // 6. An authenticated DOM is unavailable while the API context is available.
    [Fact]
    public void ProxyTrafficProducesAnApiContextAndNeverABrowserDom()
    {
        Runtime(WithApiContext);
        var cut = Open(Configured);
        cut.Find("[data-testid='auth-action-proxy']").Click();

        cut.WaitForAssertion(() => Row(cut, "testing-access-api").Should().Be("Available"));
        Row(cut, "testing-access-dom").Should().Be("Not available");
        Row(cut, "testing-access-expiry").Should().Be(
            new DateTimeOffset(2026, 9, 22, 8, 40, 50, TimeSpan.Zero).ToLocalTime().ToString("HH:mm:ss"));
    }

    // ── §39. Prerequisites ───────────────────────────────────────────────────────────────────

    [Fact]
    public void EachPrerequisiteReportsItsOwnStateAndUnknownStaysUnknown()
    {
        Runtime(Prepared);
        var cut = Open(Configured);

        Row(cut, "auth-status-certificate").Should().Be("Trusted");
        Row(cut, "auth-status-proxy").Should().Be("Running");
        Row(cut, "auth-status-browser").Should().Be("Ready to capture");
        Row(cut, "auth-readiness-state").Should().Be("Ready");

        // A certificate nobody could look at is Unknown, which is neither Trusted nor Missing.
        var unknown = AuthenticationReadinessPresentation.Summarize(
            AuthConfigurationState.Configured, AuthenticatedTestingMethod.LocalHttpsProxy, Prepared,
            new ProxyCertificateStatus { State = ProxyCertificateTrustState.Unknown }, loaded: true);
        unknown.Prerequisites.Single(p => p.Id == "certificate").State.Should().Be(AuthPrerequisiteState.Unknown);
        unknown.State.Should().Be(AuthenticationReadiness.Limited);
    }

    /// <summary>
    /// "Proxy in use" is a claim about traffic from the process BirkNext launched; a configuration verified on that
    /// running process is "Ready to capture"; BirkNext's launch record alone is only "Launched with proxy configuration".
    /// A running browser on its own earns none of them, and nothing ever says "Proxy active".
    /// </summary>
    [Fact]
    public void EachProxyClaimNeedsItsOwnEvidence()
    {
        Runtime(Prepared);
        Row(Open(Configured), "auth-status-browser").Should().Be("Ready to capture");

        static string Browser(LocalHttpsProxyStatus status) => AuthenticationReadinessPresentation
            .Summarize(AuthConfigurationState.Configured, AuthenticatedTestingMethod.LocalHttpsProxy, status,
                status.Certificate, loaded: true)
            .Prerequisites.Single(p => p.Id == "browser").StatusLabel;

        Browser(Prepared with { EdgeProxyTraffic = DedicatedBrowserProxyTraffic.Observed })
            .Should().Be("Proxy in use");
        Browser(Prepared with { EdgeVerification = DedicatedBrowserVerification.Mismatch, EdgeProxyArgument = DedicatedBrowserProxyArgument.Mismatch, ObservedEdgeProxyEndpoint = "127.0.0.1:9999" })
            .Should().Be("Proxy configuration mismatch");
        Browser(Prepared with { EdgeVerification = DedicatedBrowserVerification.NotConfirmed, EdgeProxyArgument = DedicatedBrowserProxyArgument.Unknown, EdgeProxyTraffic = DedicatedBrowserProxyTraffic.Unknown })
            .Should().Be("Launched with proxy configuration");
        Browser(Prepared with { EdgeVerification = DedicatedBrowserVerification.NotConfirmed, EdgeProxyPort = null, ProxyArgumentConfigured = false })
            .Should().Be("Proxy not confirmed");
        Browser(Prepared with { EdgeVerification = DedicatedBrowserVerification.NotRunning, EdgeRunning = false })
            .Should().Be("Not running");
    }

    // ── §40. One configuration section ───────────────────────────────────────────────────────

    [Fact]
    public void ThereIsExactlyOneConfigurationSectionAndItCarriesEveryConfigurationFact()
    {
        Runtime(Prepared);
        var cut = Open(Configured, detect: true);

        cut.FindAll("[data-testid='authentication-configuration']").Should().ContainSingle();
        cut.FindAll("[data-testid='authentication-discovery']").Should().ContainSingle();
        El(cut, "authentication-discovery").Closest("[data-testid='authentication-configuration']").Should().NotBeNull();

        var configuration = El(cut, "authentication-configuration");
        Row(cut, "authentication-configured-provider").Should().Be("Microsoft Entra ID");
        Row(cut, "authentication-provider").Should().Be("Microsoft Entra ID");
        Row(cut, "authentication-sign-in-required").Should().Be("Yes");
        Row(cut, "authentication-detection-state").Should().Be("Detected");
        Row(cut, "authentication-match-summary").Should().Contain("matches saved configuration");
        Row(cut, "authentication-config-source").Should().Be("Detect Settings");
        configuration.TextContent.Should().Contain("Use Edit Environment");
    }

    // ── §41. Verification says how verification is done ──────────────────────────────────────

    [Fact]
    public void TheVerificationCardNeverReportsADetectionMethodAsItsMethod()
    {
        Runtime(Prepared);
        var cut = Open(Configured, detect: true);

        var verification = El(cut, "authentication-verification");
        Row(cut, "authentication-verification-state").Should().Be("Manual verification required");
        Row(cut, "authentication-verification-method").Should().Be("Manual verification in a signed-in browser");
        Row(cut, "authentication-verification-last").Should().Be("Never");
        verification.TextContent.Should().NotContain("Automated detection");
        verification.QuerySelector("[data-testid='authentication-verification-open']").Should().NotBeNull();
        Row(cut, "authentication-verification-scope").Should().Contain("does not create an authenticated testing context");
    }

    /// <summary>The primary action has to do something. It used to set a flag no part of the page read.</summary>
    [Fact]
    public void VerifyAuthenticationOpensTheInstructionsThatDescribeIt()
    {
        Runtime(Prepared);
        var cut = Open(Configured, detect: true);

        El(cut, "auth-verify").HasAttribute("disabled").Should().BeFalse();
        El(cut, "auth-verify").Click();

        cut.FindAll("[role=tab][aria-selected=true]").Single().TextContent.Trim().Should().Be("Target Application");
        // The instructions are open, not merely reachable: the action used to set a flag nothing read.
        cut.Markup.Should().Contain("Sign in using your work account.");
        cut.FindAll("button").Should().Contain(b => b.TextContent.Trim() == "Mark verification passed");
    }

    // ── §43. Capabilities ────────────────────────────────────────────────────────────────────

    [Fact]
    public void TestingCapabilitiesAreOneCollapsedInventoryWithNoSemanticsLost()
    {
        Runtime(Prepared);
        var cut = Open(Configured);

        El(cut, "auth-capabilities-toggle").GetAttribute("aria-expanded").Should().Be("false");
        El(cut, "auth-capabilities-body").HasAttribute("hidden").Should().BeTrue();
        cut.FindAll("[data-testid='authenticated-capabilities']").Should().ContainSingle();

        El(cut, "auth-capabilities-toggle").Click();
        var capabilities = El(cut, "authenticated-capabilities").TextContent;
        foreach (var row in new[]
                 {
                     "Security · Public", "Security · Authenticated API context", "Security · Authenticated DOM",
                     "REST · Public discovery", "REST · Authenticated endpoint",
                     "GraphQL · Schema discovery", "GraphQL · Authenticated query endpoint",
                 })
            capabilities.Should().Contain(row);

        // "Verified" means an authenticated request was observed; nothing here is verified yet.
        Row(cut, "capability-rest").Should().Be("Not observed");
        Row(cut, "capability-graphql").Should().Be("Not observed");
        Row(cut, "capability-api").Should().Be("Unavailable");
        // And the inventory carries no second Endpoint Discovery call to action.
        El(cut, "auth-capabilities-body").QuerySelector("[data-testid='auth-open-discovery']").Should().BeNull();
    }

    // ── §44. Technical details ───────────────────────────────────────────────────────────────

    [Fact]
    public void TechnicalDetailsAreCollapsedAndCarryTheIdentifiersThatExplainAFailure()
    {
        Runtime(Prepared);
        var cut = Open(Configured);

        El(cut, "auth-technical-details-toggle").GetAttribute("aria-expanded").Should().Be("false");
        El(cut, "auth-technical-details-body").HasAttribute("hidden").Should().BeTrue();

        El(cut, "auth-technical-details-toggle").Click();
        var facts = El(cut, "auth-technical-facts").TextContent;
        facts.Should().Contain("runtime-abc123");
        facts.Should().Contain("14520", "the Edge process id");
        facts.Should().Contain("--proxy-server=127.0.0.1:8888", "the argument actually recorded at launch");
        facts.Should().Contain("LocalHttpsProxyEdgeProfile");
        facts.Should().Contain("A1B2C3D4E5F60718293A4B5C6D7E8F9012345678", "the certificate thumbprint");

        // The diagnostics are diagnostics, never credentials.
        foreach (var forbidden in new[] { "Bearer", "Authorization", "Set-Cookie", "secret", "password", "token" })
            El(cut, "auth-technical-details-body").TextContent.Should().NotContain(forbidden);
    }

    /// <summary>
    /// The expected identifiers have exactly one home — the detected-versus-configured comparison — so the technical
    /// disclosure points at it rather than printing a second, less informative copy.
    /// </summary>
    [Fact]
    public void TheExpectedIdentifiersAreNotDuplicatedIntoTheTechnicalDisclosure()
    {
        Runtime(Prepared);
        var cut = Open(Configured, detect: true);
        El(cut, "auth-technical-details-toggle").Click();

        var authentication = El(cut, "auth-technical-authentication").TextContent;
        authentication.Should().NotContain(Authority);
        authentication.Should().NotContain(ClientId);
        authentication.Should().Contain("Authentication configuration → Detection → View details");
        authentication.Should().Contain("Saved verification mode");
    }

    // ── §45–46. Advanced, and one Endpoint Discovery hand-off ────────────────────────────────

    [Fact]
    public void DestructiveActionsLiveBehindOneCollapsedAdvancedSection()
    {
        Runtime(Prepared);
        var cut = Open(Configured);

        // One Advanced / Maintenance section on the page, collapsed, and nothing destructive outside it.
        cut.FindAll("[data-testid='profile-advanced']").Should().ContainSingle();
        El(cut, "profile-advanced-toggle").GetAttribute("aria-expanded").Should().Be("false");
        Visible(cut).Should().NotContain("Remove test certificate");

        El(cut, "profile-advanced-toggle").Click();
        var body = El(cut, "profile-advanced-body");
        var button = body.QuerySelectorAll("button").Single(b => b.TextContent.Trim() == "Remove test certificate");
        button.ClassList.Should().Contain("btn-danger", "a destructive action must not look like an ordinary one");
        body.QuerySelector("[data-testid='advanced-remove-certificate']")!.TextContent
            .Should().Contain("BirkNext DEV HTTPS Inspection CA");

        // Authentication owns the certificate it installs, and nothing else. Resetting the whole profile is
        // General's, and a page-wide maintenance footer used to offer it under every pane including this one.
        body.QuerySelectorAll("button").Should().NotContain(b => b.TextContent.Trim() == "Reset Profile");
    }

    /// <summary>Reset Profile is the whole profile's, so it lives on General and is offered by no other pane.</summary>
    [Fact]
    public void ResetProfileBelongsToGeneralAndStillWorksThere()
    {
        Runtime(Prepared);
        var cut = Open(Configured);

        cut.FindAll("[role=tab]").Single(t => t.TextContent.Trim() == "General").Click();
        El(cut, "profile-advanced-toggle").Click();

        El(cut, "profile-advanced-body").QuerySelectorAll("button").Single(b => b.TextContent.Trim() == "Reset Profile").Click();
        cut.Find(".modal-title").TextContent.Should().Be("Reset Profile");
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Reset Profile" && b.ClassList.Contains("btn-danger")
            && b.Closest(".modal-backdrop") is not null).Click();

        cut.WaitForAssertion(() => _settings.Settings.Profiles.Single(p => p.Id == "dev").Name.Should().Be("M2LB DEV"));
    }

    [Fact]
    public void EndpointDiscoveryHasExactlyOneCallToActionAndKeepsItsName()
    {
        Runtime(Prepared);
        var cut = Open(Configured);

        cut.FindAll("[data-testid='auth-open-discovery']").Should().ContainSingle();
        cut.Find("[data-testid='fa-auth-panel']").QuerySelectorAll("button")
            .Where(b => b.TextContent.Contains("Endpoint Discovery")).Should().ContainSingle();
        Has(cut, "authenticated-testing-open-discovery").Should().BeFalse();
        El(cut, "auth-discovery-cta").TextContent.Should().Contain("Endpoint Discovery").And.NotContain("Endpoint Detection");
        El(cut, "auth-open-discovery").HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public void AnIncompleteSetupLimitsWhatArrivesNextRatherThanLockingTheSurface()
    {
        Runtime(Prepared with
        {
            SessionId = null, State = LocalHttpsProxyState.NotStarted, RuntimeStatus = LocalHttpsProxyRuntimePhase.Stopped,
            EdgeVerification = DedicatedBrowserVerification.NotRunning, EdgeRunning = false,
        });
        var cut = Open(Configured);

        Row(cut, "auth-discovery-status").Should().Contain("setup incomplete");
        El(cut, "auth-open-discovery").HasAttribute("disabled").Should().BeFalse();
    }

    // ── §30. Nothing is asserted before the backend has answered ─────────────────────────────

    [Fact]
    public void BeforeTheFirstBackendAnswerNoPrerequisiteReportsAFalseNegative()
    {
        var checking = AuthenticationReadinessPresentation.Summarize(
            AuthConfigurationState.Configured, AuthenticatedTestingMethod.LocalHttpsProxy, null, null, loaded: false);

        checking.State.Should().Be(AuthenticationReadiness.Checking);
        checking.Label.Should().Be("Checking…");
        checking.Prerequisites.Should().OnlyContain(p => p.StatusLabel == "Checking…");
        foreach (var premature in new[] { "Not running", "Not installed", "Proxy not active", "Not configured" })
            checking.Prerequisites.Should().NotContain(p => p.StatusLabel.Contains(premature));
    }

    // ── §34. Accessibility ───────────────────────────────────────────────────────────────────

    [Fact]
    public void EveryStateIsCarriedByTextAndEveryDisclosureIsAKeyboardButton()
    {
        Runtime(Prepared);
        var cut = Open(Configured, detect: true);
        var panel = cut.Find("[data-testid='fa-auth-panel']");

        // Section headings are unique and hierarchical; the state is always a word beside them.
        panel.QuerySelectorAll("h3").Select(h => h.TextContent.Trim()).Should().OnlyHaveUniqueItems();
        foreach (var id in new[] { "auth-summary-configuration", "auth-summary-verification", "auth-summary-context" })
            Row(cut, id).Should().NotBeNullOrWhiteSpace();

        foreach (var icon in panel.QuerySelectorAll(".ar-icon"))
            icon.GetAttribute("aria-hidden").Should().Be("true");

        foreach (var toggle in panel.QuerySelectorAll(".disclosure-toggle"))
        {
            toggle.TagName.Should().Be("BUTTON");
            toggle.GetAttribute("aria-expanded").Should().BeOneOf("true", "false");
            cut.Find($"#{toggle.GetAttribute("aria-controls")}").Should().NotBeNull();
            toggle.TextContent.Trim().Should().NotBeEmpty("an icon-only disclosure says nothing to a screen reader");
        }

        foreach (var button in panel.QuerySelectorAll("button"))
            button.TextContent.Trim().Should().NotBeEmpty();

        cut.Find("[data-testid='auth-prerequisites'] .ar-cards").GetAttribute("aria-labelledby")
            .Should().Be("auth-prerequisites-heading");
    }
}
