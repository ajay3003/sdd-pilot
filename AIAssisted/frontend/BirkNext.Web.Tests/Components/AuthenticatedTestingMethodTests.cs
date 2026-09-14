using System.Text.Json;
using BirkNext.LocalHttpsProxy;
using BirkNext.ManagedEdge;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Component = BirkNext.Web.Components.FrontendAnalysisSettings;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// Authenticated testing method is SAVED Target Environment configuration (Authentication section): defaults to ManagedEdgeCdp for
/// new and legacy profiles, follows Edit / Save changes / Cancel, survives duplication, is never switched automatically, and the
/// Local HTTPS proxy runtime (start/stop/certificate/credential) never enters edit mode, never persists and never shows a credential.
/// </summary>
public sealed class AuthenticatedTestingMethodTests : BunitContext
{
    private const string Url = "https://m2lbdev.bufetat.no/";
    private const string Origin = "https://m2lbdev.bufetat.no";
    private const string FakeToken = "eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiJ4In0.c2ln";
    private readonly FrontendAnalysisSettingsService _settings = new();
    private readonly Mock<ITargetEnvironmentDetectionApiService> _detector = new();
    private readonly Mock<IManagedEdgeCdpApiService> _edgeApi = new();
    private readonly Mock<ILocalHttpsProxyApiService> _proxyApi = new();
    private readonly ManagedEdgeRuntime _edgeRuntime;
    private readonly LocalHttpsProxyRuntime _proxyRuntime;

    private static readonly LocalHttpsProxyStatus Listening = new()
    {
        SessionId = "proxy-session", State = LocalHttpsProxyState.Listening, Port = 8888, LocalIntegrationAvailable = true, EnvironmentAllowed = true, PortAvailable = true,
        ApprovedHosts = ["m2lbdev.bufetat.no:443"], TargetOrigin = Origin, Certificate = new() { State = ProxyCertificateTrustState.Trusted, InstallSupported = true }
    };
    private static readonly LocalHttpsProxyStatus Ready = Listening with
    {
        State = LocalHttpsProxyState.Ready, InterceptedRequests = 3, AuthenticatedRequestsObserved = 1, AuthenticatedCredentialAvailable = true,
        CredentialObservedHost = "m2lbdev.bufetat.no", CredentialObservedAt = DateTimeOffset.UtcNow, CredentialExpiresAt = DateTimeOffset.UtcNow.AddMinutes(25), CredentialFormat = "JWT",
        Evidence = "Authenticated API context available (memory only)."
    };

    public AuthenticatedTestingMethodTests()
    {
        _edgeRuntime = new ManagedEdgeRuntime(_edgeApi.Object);
        _proxyRuntime = new LocalHttpsProxyRuntime(_proxyApi.Object);
        Services.AddSingleton<IFrontendAnalysisSettingsService>(_settings);
        Services.AddSingleton(_detector.Object);
        Services.AddSingleton(_edgeRuntime);
        Services.AddSingleton(_proxyRuntime);
        JSInterop.SetupVoid("birkNextStorage.setItem", _ => true).SetVoidResult();
        _detector.Setup(a => a.DetectFromUrlAsync(It.IsAny<string>(), default)).ReturnsAsync(new TargetEnvironmentDetectionResult
        {
            Success = true, OriginalUrl = Url, Reachability = TargetReachability.Reachable,
            DetectedClientFramework = ClientFrameworkType.BlazorWebAssembly, DetectedAuthenticationType = FrontendAuthenticationType.MicrosoftEntraId,
            ManualAuthenticationVerificationRequired = true, State = DetectionState.ManualAuthenticationVerificationRequired
        });
        _edgeApi.Setup(a => a.ConnectAsync(It.IsAny<ManagedEdgeConnectRequest>())).ReturnsAsync(new ManagedEdgeStatus { State = ManagedEdgeState.TargetTabNotInspectable, TargetOrigin = Origin, DiscoveredTargetTabs = 1, TrustDecision = ManagedEdgeTrustDecision.TargetNotInspectable });
        _edgeApi.Setup(a => a.DisconnectAsync(It.IsAny<ManagedEdgeSessionRequest>())).Returns(Task.CompletedTask);
        _proxyApi.Setup(a => a.CheckCompatibilityAsync(It.IsAny<LocalHttpsProxyScopeRequest>())).ReturnsAsync(Listening with { SessionId = null, State = LocalHttpsProxyState.NotStarted, Port = 0, CanStart = true });
        _proxyApi.Setup(a => a.StartAsync(It.IsAny<LocalHttpsProxyScopeRequest>())).ReturnsAsync(Ready);
        _proxyApi.Setup(a => a.StatusAsync(It.IsAny<LocalHttpsProxySessionRequest>())).ReturnsAsync(Ready);
        _proxyApi.Setup(a => a.StopAsync(It.IsAny<LocalHttpsProxySessionRequest>())).ReturnsAsync(Listening with { SessionId = null, State = LocalHttpsProxyState.Stopped });
    }

    private IRenderedComponent<Component> Open(string environmentType = "Development", string authenticationJson = """{ "authenticationType": "MicrosoftEntraId" }""")
    {
        JSInterop.Setup<string?>("birkNextStorage.getItem", _ => true).SetResult($$"""
            {"activeProfileId":"local","profiles":[
              {"id":"local","name":"Local","targetUrl":"https://local.example.com"},
              {"id":"dev","name":"M2LB DEV","environmentType":"{{environmentType}}","targetUrl":"{{Url}}","healthEndpoint":"https://m2lbdev.bufetat.no/health","graphQlEndpoint":"https://m2lbdev.bufetat.no/graphql","authentication":{{authenticationJson}}}
            ]}
            """);
        var cut = Render<Component>();
        cut.FindAll(".fa-profile-chip").Single(b => b.TextContent.Contains("M2LB DEV")).Click();
        return cut;
    }

    private static void Click(IRenderedComponent<Component> cut, string label) => cut.FindAll("button").Single(b => b.TextContent.Trim() == label).Click();
    private static void OpenTab(IRenderedComponent<Component> cut, string label) => cut.FindAll("[role=tab]").Single(t => t.TextContent.Trim() == label).Click();
    private static bool HasButton(IRenderedComponent<Component> cut, string label) => cut.FindAll("button").Any(b => b.TextContent.Trim() == label);
    private static bool Has(IRenderedComponent<Component> cut, string testId) => cut.FindAll($"[data-testid='{testId}']").Count > 0;
    private static string Row(IRenderedComponent<Component> cut, string testId) => cut.Find($"[data-testid='{testId}']").TextContent.Trim();
    private int SaveCalls() => JSInterop.Invocations.Count(i => i.Identifier == "birkNextStorage.setItem");
    private FrontendAnalysisProfile Persisted() => _settings.Settings.Profiles.Single(x => x.Id == "dev");
    private static void SelectMethod(IRenderedComponent<Component> cut, AuthenticatedTestingMethod method) => cut.Find("#authenticated-testing-method-select").Change(method.ToString());

    // ── model / persistence (section 35) ─────────────────────────────────────

    [Fact]
    public void NewProfileDefaultsToManagedEdgeCdp()
    {
        Assert.Equal(AuthenticatedTestingMethod.ManagedEdgeCdp, new FrontendAnalysisSettingsService().CreateProfile("New", FrontendEnvironmentType.Development).Authentication.AuthenticatedTestingMethod);
        Assert.Equal(AuthenticatedTestingMethod.ManagedEdgeCdp, new FrontendAuthenticationSettings().AuthenticatedTestingMethod);
    }

    [Fact]
    public void LegacyProfileWithoutTheFieldDeserializesToManagedEdgeCdp()
    {
        var storage = new JsonSerializerOptions { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
        var legacy = JsonSerializer.Deserialize<FrontendAnalysisProfile>("""{"id":"dev","name":"Dev","targetUrl":"https://m2lbdev.bufetat.no/","authentication":{"authenticationType":"MicrosoftEntraId","browserDeliveryTrust":"ApprovedMcasProxyOrigin"}}""", storage)!;
        Assert.Equal(AuthenticatedTestingMethod.ManagedEdgeCdp, legacy.Authentication.AuthenticatedTestingMethod);
        Assert.Equal(ManagedEdgeTrustModel.ApprovedMcasProxyOrigin, legacy.Authentication.BrowserDeliveryTrust);
        var noAuth = JsonSerializer.Deserialize<FrontendAnalysisProfile>("""{"id":"dev","name":"Dev"}""", storage)!;
        Assert.Equal(AuthenticatedTestingMethod.ManagedEdgeCdp, noAuth.Authentication.AuthenticatedTestingMethod);
    }

    [Fact]
    public void MethodIsSerializedOnceUnderAuthenticationAndProxyRuntimeStateNever()
    {
        var profile = new FrontendAnalysisProfile { Id = "dev", TargetUrl = Url };
        profile.Authentication.AuthenticatedTestingMethod = AuthenticatedTestingMethod.LocalHttpsProxy;
        var json = JsonSerializer.Serialize(profile);
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(json, "\"authenticatedTestingMethod\"").Count);
        Assert.Contains("\"authenticatedTestingMethod\":\"LocalHttpsProxy\"", json);
        foreach (var runtimeOnly in new[] { "sessionId", "\"port\"", "certificate", "interceptedRequests", "authenticatedCredentialAvailable", "credentialExpiresAt", "approvedHosts", "bearer", "Bearer", "eyJ", "cookie" })
            Assert.DoesNotContain(runtimeOnly, json);
        Assert.Equal(AuthenticatedTestingMethod.LocalHttpsProxy, JsonSerializer.Deserialize<FrontendAnalysisProfile>(json)!.Authentication.AuthenticatedTestingMethod);
    }

    [Fact]
    public void DuplicatePreservesMethod()
    {
        var settings = new FrontendAnalysisSettingsService();
        var source = settings.CreateProfile("Dev", FrontendEnvironmentType.Development);
        source.TargetUrl = Url;
        source.Authentication.AuthenticatedTestingMethod = AuthenticatedTestingMethod.LocalHttpsProxy;
        var copy = settings.DuplicateProfile(source.Id);
        Assert.Equal(AuthenticatedTestingMethod.LocalHttpsProxy, copy.Authentication.AuthenticatedTestingMethod);
        Assert.Equal(AuthenticatedTestingMethod.ManagedEdgeCdp, settings.CreateProfile("Other", FrontendEnvironmentType.Test).Authentication.AuthenticatedTestingMethod);
    }

    [Theory]
    [InlineData(FrontendEnvironmentType.Production)]
    [InlineData(FrontendEnvironmentType.Custom)]
    public void ValidationRejectsProxyForProductionAndCustom(FrontendEnvironmentType type)
    {
        var settings = new FrontendAnalysisSettingsService();
        var profile = settings.CreateProfile("Prod", type);
        profile.TargetUrl = Url;
        profile.Authentication.AuthenticatedTestingMethod = AuthenticatedTestingMethod.LocalHttpsProxy;
        var result = settings.ValidateProfile(profile);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("Local HTTPS proxy is available only"));
        profile.Authentication.AuthenticatedTestingMethod = AuthenticatedTestingMethod.ManagedEdgeCdp;
        Assert.DoesNotContain(settings.ValidateProfile(profile).Errors, e => e.Contains("Local HTTPS proxy"));
    }

    [Theory]
    [InlineData(FrontendEnvironmentType.Local)]
    [InlineData(FrontendEnvironmentType.Development)]
    [InlineData(FrontendEnvironmentType.QA)]
    [InlineData(FrontendEnvironmentType.Test)]
    [InlineData(FrontendEnvironmentType.RC)]
    public void ValidationAcceptsProxyForNonProductionEnvironments(FrontendEnvironmentType type)
    {
        var settings = new FrontendAnalysisSettingsService();
        var profile = settings.CreateProfile("Env", type);
        profile.TargetUrl = Url;
        profile.Authentication.AuthenticatedTestingMethod = AuthenticatedTestingMethod.LocalHttpsProxy;
        Assert.DoesNotContain(settings.ValidateProfile(profile).Errors, e => e.Contains("Local HTTPS proxy"));
    }

    [Fact]
    public void ScopeDerivesOnlyExplicitlyConfiguredHttpsHostsAndFingerprintTracksTargets()
    {
        var profile = new FrontendAnalysisProfile { Id = "dev", TargetUrl = Url, RestBaseUrl = "https://api-dev.bufetat.no/v1", GraphQlEndpoint = "https://m2lbdev.bufetat.no/graphql", HealthEndpoint = "http://insecure.example.test/health" };
        profile.AllowedRestHosts.Add("gateway.example.test:8443");
        profile.Security.AllowedGraphQlHosts.Add("*.wild.example.test");
        Assert.Equal(["api-dev.bufetat.no:443", "gateway.example.test:8443", "m2lbdev.bufetat.no:443"], LocalHttpsProxyScope.ApprovedHosts(profile));
        var before = LocalHttpsProxyScope.Fingerprint(profile);
        profile.RestBaseUrl = "https://api-qa.bufetat.no/v1";
        Assert.NotEqual(before, LocalHttpsProxyScope.Fingerprint(profile));
        var request = LocalHttpsProxyScope.Request(profile);
        Assert.Equal("dev", request.ProfileId);
        Assert.Equal(64, request.ContextFingerprint.Length);
        Assert.DoesNotContain("eyJ", JsonSerializer.Serialize(request));
    }

    // ── configuration UI ─────────────────────────────────────────────────────

    [Fact]
    public void AuthenticationTabShowsCdpPanelAndReadOnlyMethodByDefault()
    {
        var cut = Open();
        OpenTab(cut, "Authentication");
        Assert.True(Has(cut, "managed-edge-panel"));
        Assert.False(Has(cut, "local-https-proxy-panel"));
        Assert.Equal(AuthenticatedTestingMethodLabels.CdpOption, Row(cut, "authenticated-testing-method-value"));
        Assert.False(HasButton(cut, "Save changes"));
    }

    [Fact]
    public void SelectingProxyShowsWarningAndRequiresSaveChangesToPersist()
    {
        var cut = Open();
        OpenTab(cut, "Authentication");
        Click(cut, "Edit Environment");
        Assert.False(Has(cut, "proxy-security-warning"));
        SelectMethod(cut, AuthenticatedTestingMethod.LocalHttpsProxy);
        Assert.Equal(AuthenticatedTestingMethodLabels.ProxySecurityWarning, Row(cut, "proxy-security-warning"));
        Assert.Contains("in memory", Row(cut, "authenticated-testing-method-help"));
        Assert.True(Has(cut, "local-https-proxy-panel"));
        Assert.False(Has(cut, "managed-edge-panel"));
        Assert.True(Has(cut, "auth-unsaved-changes"));
        Assert.Equal(AuthenticatedTestingMethod.ManagedEdgeCdp, Persisted().Authentication.AuthenticatedTestingMethod);
        Assert.Equal(0, SaveCalls());
        Click(cut, "Save changes");
        Assert.Equal(AuthenticatedTestingMethod.LocalHttpsProxy, Persisted().Authentication.AuthenticatedTestingMethod);
        Assert.Equal(1, SaveCalls());
        Assert.Equal(AuthenticatedTestingMethodLabels.ProxyOption, Row(cut, "authenticated-testing-method-value"));
        Assert.True(Has(cut, "local-https-proxy-panel"));
    }

    [Fact]
    public void CancelRestoresPersistedMethod()
    {
        var cut = Open();
        OpenTab(cut, "Authentication");
        Click(cut, "Edit Environment");
        SelectMethod(cut, AuthenticatedTestingMethod.LocalHttpsProxy);
        Click(cut, "Cancel");
        Assert.Equal(AuthenticatedTestingMethod.ManagedEdgeCdp, Persisted().Authentication.AuthenticatedTestingMethod);
        Assert.Equal(AuthenticatedTestingMethodLabels.CdpOption, Row(cut, "authenticated-testing-method-value"));
        Assert.True(Has(cut, "managed-edge-panel"));
        Assert.Equal(0, SaveCalls());
    }

    [Fact]
    public void ProductionCannotSaveProxyMethod()
    {
        var cut = Open(environmentType: "Production");
        OpenTab(cut, "Authentication");
        Click(cut, "Edit Environment");
        SelectMethod(cut, AuthenticatedTestingMethod.LocalHttpsProxy);
        Assert.True(Has(cut, "authenticated-testing-method-environment-blocked"));
        Assert.Contains("Production", Row(cut, "proxy-environment"));
        Assert.True(cut.FindAll("button").Single(b => b.TextContent.Trim() == "Start authenticated proxy").HasAttribute("disabled"));
        Click(cut, "Save changes");
        Assert.Equal(AuthenticatedTestingMethod.ManagedEdgeCdp, Persisted().Authentication.AuthenticatedTestingMethod);
        Assert.Equal(0, SaveCalls());
        Assert.Contains("Local HTTPS proxy is available only", cut.Markup);
    }

    [Fact]
    public void ManualOnlyShowsNoAutomationPanel()
    {
        var cut = Open(authenticationJson: """{ "authenticationType": "MicrosoftEntraId", "authenticatedTestingMethod": "ManualOnly" }""");
        OpenTab(cut, "Authentication");
        Assert.True(Has(cut, "manual-only-panel"));
        Assert.False(Has(cut, "managed-edge-panel"));
        Assert.False(Has(cut, "local-https-proxy-panel"));
        OpenTab(cut, "Validation");
        Assert.Contains("manual verification only", Row(cut, "validation-coverage-api"));
    }

    // ── CDP blocked: no silent switch (section 26) ───────────────────────────

    [Fact]
    public void DetectionHasOneCompactSummaryAndExplicitApply()
    {
        var cut = Open();
        Click(cut, "Detect settings");
        OpenTab(cut, "Authentication");
        Assert.Single(cut.FindAll("h3").Where(x => x.TextContent == "Authentication discovery"));
        Assert.Empty(cut.FindAll(".fa-result-grid"));
        Assert.DoesNotContain("Detected Authentication", cut.Markup);
        Assert.True(HasButton(cut, "Apply authentication"));
        Assert.Equal(0, SaveCalls());
    }

    [Theory]
    [InlineData(AuthenticatedTestingMethod.ManagedEdgeCdp, "managed-edge-panel")]
    [InlineData(AuthenticatedTestingMethod.LocalHttpsProxy, "local-https-proxy-panel")]
    [InlineData(AuthenticatedTestingMethod.ManualOnly, "manual-only-panel")]
    public void OnlySelectedWorkflowAndConfigurationAppearInPrimaryOrder(AuthenticatedTestingMethod method, string panel)
    {
        var cut = Open(authenticationJson: $$"""{"authenticatedTestingMethod":"{{method}}"}""");
        OpenTab(cut, "Authentication");
        Assert.Empty(cut.FindAll("#authenticated-testing-method-select"));
        Assert.Single(cut.FindAll("[data-testid='authenticated-testing-method-value']"));
        Assert.Single(cut.FindAll("[data-testid='authentication-discovery']"));
        Assert.Single(cut.FindAll("[data-testid='authenticated-capabilities']"));
        foreach (var candidate in new[] { "managed-edge-panel", "local-https-proxy-panel", "manual-only-panel" })
            Assert.Equal(candidate == panel, Has(cut, candidate));
        Assert.Equal(method == AuthenticatedTestingMethod.ManagedEdgeCdp, Has(cut, "browser-delivery-trust"));
        Assert.Equal(method == AuthenticatedTestingMethod.LocalHttpsProxy, Has(cut, "proxy-security-warning"));
        Assert.True(cut.Markup.IndexOf("data-testid=\"authentication-discovery\"") < cut.Markup.IndexOf("data-testid=\"authenticated-testing-method\""));
        Assert.True(cut.Markup.IndexOf("data-testid=\"authenticated-testing-method\"") < cut.Markup.IndexOf($"data-testid=\"{panel}\""));
        Assert.DoesNotContain("TargetTabNotInspectable", cut.Markup);
        Click(cut, "Edit Environment");
        Assert.Single(cut.FindAll("#authenticated-testing-method-select"));
        Assert.Equal(method == AuthenticatedTestingMethod.ManagedEdgeCdp, Has(cut, "browser-delivery-trust"));
    }

    [Fact]
    public async Task ProtectedTargetHasNoCapabilitiesOrInternalEnumAndNoProductionProxyHint()
    {
        var cut = Open(environmentType: "Production");
        OpenTab(cut, "Authentication");
        await cut.InvokeAsync(() => Click(cut, "Connect to existing Edge"));
        cut.WaitForAssertion(() => Assert.Contains("Enterprise browser protection prevents debugger attachment", cut.Markup));
        Assert.DoesNotContain("TargetTabNotInspectable", cut.Markup);
        Assert.False(Has(cut, "cdp-blocked-proxy-hint"));
        foreach (var capability in new[] { "api", "dom", "rest", "graphql" })
            Assert.Equal("Unavailable", Row(cut, $"capability-{capability}"));
        OpenTab(cut, "Validation");
        Assert.DoesNotContain("TargetTabNotInspectable", cut.Markup);
    }

    [Fact]
    public async Task SwitchingFromReadyProxyClearsCapabilitiesAndCancelRestoresMethodWithoutCredentials()
    {
        var cut = Open(authenticationJson: """{"authenticatedTestingMethod":"LocalHttpsProxy"}""");
        OpenTab(cut, "Authentication");
        await cut.InvokeAsync(() => Click(cut, "Start authenticated proxy"));
        cut.WaitForAssertion(() => Assert.Equal("Available", Row(cut, "capability-rest")));
        Click(cut, "Edit Environment");
        SelectMethod(cut, AuthenticatedTestingMethod.ManualOnly);
        foreach (var capability in new[] { "api", "dom", "rest", "graphql" })
            Assert.Equal("Unavailable", Row(cut, $"capability-{capability}"));
        Assert.False(Has(cut, "proxy-credential"));
        cut.WaitForAssertion(() => _proxyApi.Verify(x => x.StopAsync(It.IsAny<LocalHttpsProxySessionRequest>()), Times.AtLeastOnce));
        SelectMethod(cut, AuthenticatedTestingMethod.ManagedEdgeCdp);
        Assert.Equal("Unavailable", Row(cut, "capability-rest"));
        Click(cut, "Cancel");
        Assert.Equal(AuthenticatedTestingMethod.LocalHttpsProxy, Persisted().Authentication.AuthenticatedTestingMethod);
        Assert.Equal("Unavailable", Row(cut, "capability-rest"));
        Assert.Equal(0, SaveCalls());
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task CdpCapabilitiesFollowIndividualRuntimeFlags(bool rest, bool graphQl)
    {
        _edgeApi.Setup(x => x.ConnectAsync(It.IsAny<ManagedEdgeConnectRequest>())).ReturnsAsync(new ManagedEdgeStatus
        {
            State = ManagedEdgeState.ConnectedAuthenticated, RestAvailable = rest, GraphQlAvailable = graphQl
        });
        var cut = Open();
        OpenTab(cut, "Authentication");
        await cut.InvokeAsync(() => Click(cut, "Connect to existing Edge"));
        cut.WaitForAssertion(() => Assert.Equal("Available", Row(cut, "capability-dom")));
        Assert.Equal(rest ? "Available" : "Unavailable", Row(cut, "capability-rest"));
        Assert.Equal(graphQl ? "Available" : "Unavailable", Row(cut, "capability-graphql"));
        Assert.Equal(0, SaveCalls());
    }

    [Fact]
    public async Task CdpBlockedShowsProxyHintButNeverSwitchesTheSavedMethod()
    {
        var cut = Open();
        OpenTab(cut, "Authentication");
        Assert.False(Has(cut, "cdp-blocked-proxy-hint"));
        await cut.InvokeAsync(() => Click(cut, "Connect to existing Edge"));
        cut.WaitForAssertion(() => Assert.True(Has(cut, "cdp-blocked-proxy-hint")));
        Assert.Equal(AuthenticatedTestingMethodLabels.CdpBlockedProxyHint, Row(cut, "cdp-blocked-proxy-hint"));
        Assert.Equal(AuthenticatedTestingMethod.ManagedEdgeCdp, Persisted().Authentication.AuthenticatedTestingMethod);
        Assert.True(Has(cut, "managed-edge-panel"));
        Assert.False(Has(cut, "local-https-proxy-panel"));
        Assert.False(HasButton(cut, "Save changes"));
        Assert.Equal(0, SaveCalls());
    }

    // ── proxy runtime: transient, never dirty, never persisted, never a credential (Y, Z, J) ──

    [Fact]
    public async Task ProxyRuntimeOperationsNeverDirtyOrPersistAndNeverShowACredential()
    {
        var cut = Open(authenticationJson: """{ "authenticationType": "MicrosoftEntraId", "authenticatedTestingMethod": "LocalHttpsProxy" }""");
        OpenTab(cut, "Authentication");
        var persistedBefore = JsonSerializer.Serialize(Persisted());
        Assert.True(Has(cut, "local-https-proxy-panel"));
        Assert.Equal(AuthenticatedTestingMethodLabels.ProxySecurityWarning, Row(cut, "proxy-security-warning"));
        Assert.Equal("Not started", Row(cut, "proxy-state"));

        await cut.InvokeAsync(() => Click(cut, "Check proxy compatibility"));
        cut.WaitForAssertion(() => Assert.Contains("m2lbdev.bufetat.no:443", Row(cut, "proxy-approved-hosts")));
        await cut.InvokeAsync(() => Click(cut, "Start authenticated proxy"));
        cut.WaitForAssertion(() => Assert.Equal("Ready", Row(cut, "proxy-state")));
        Assert.Equal("127.0.0.1:8888", Row(cut, "proxy-endpoint"));
        Assert.Equal("Trusted", Row(cut, "proxy-certificate"));
        Assert.Equal("Detected", Row(cut, "proxy-authenticated-traffic"));
        Assert.Equal("Available - memory only", Row(cut, "proxy-credential"));
        Assert.True(Has(cut, "proxy-credential-expiry"));
        Assert.Contains("Available", Row(cut, "capability-rest"));
        Assert.Contains("Available", Row(cut, "capability-graphql"));
        Assert.Contains("Unavailable", Row(cut, "capability-dom"));
        Assert.Contains("Available", Row(cut, "capability-api"));

        // Runtime evidence is transient: no edit mode, no Save changes, no storage write, persisted profile unchanged.
        Assert.True(HasButton(cut, "Edit Environment"));
        Assert.False(HasButton(cut, "Save changes"));
        Assert.Equal(0, SaveCalls());
        Assert.Equal(persistedBefore, JsonSerializer.Serialize(Persisted()));
        foreach (var forbidden in new[] { "eyJ", FakeToken, "Bearer ", "Cookie", "Show token", "Copy token", "Reveal token" })
            Assert.DoesNotContain(forbidden, cut.Markup);
        Assert.DoesNotContain(cut.FindAll("input,textarea"), e => (e.GetAttribute("id") ?? "").Contains("token", StringComparison.OrdinalIgnoreCase));

        OpenTab(cut, "Validation");
        Assert.Equal(AuthenticatedTestingMethodLabels.ProxyOption, Row(cut, "validation-testing-method"));
        Assert.Contains("Ready", Row(cut, "validation-proxy-state"));
        Assert.Equal("Available - memory only", Row(cut, "validation-proxy-credential"));
        Assert.Contains("Available via Local HTTPS Proxy", Row(cut, "validation-coverage-api"));
        Assert.Contains("Unavailable", Row(cut, "validation-coverage-dom"));
        Assert.Equal("Available", Row(cut, "validation-coverage-public"));

        OpenTab(cut, "Authentication");
        await cut.InvokeAsync(() => Click(cut, "Stop proxy"));
        cut.WaitForAssertion(() => Assert.Equal("Stopped", Row(cut, "proxy-state")));
        Assert.Equal("Waiting for authenticated traffic", Row(cut, "proxy-credential"));
        _proxyApi.Verify(a => a.StopAsync(It.Is<LocalHttpsProxySessionRequest>(r => r.SessionId == "proxy-session" && r.ProfileId == "dev")), Times.Once);
        Assert.Equal(0, SaveCalls());
        Assert.Equal(persistedBefore, JsonSerializer.Serialize(Persisted()));
    }

    [Fact]
    public async Task EditingTheEnvironmentStalesTheProxySessionAndSavingRequiresARestart()
    {
        var cut = Open(authenticationJson: """{ "authenticationType": "MicrosoftEntraId", "authenticatedTestingMethod": "LocalHttpsProxy" }""");
        OpenTab(cut, "Authentication");
        await cut.InvokeAsync(() => Click(cut, "Start authenticated proxy"));
        cut.WaitForAssertion(() => Assert.Equal("Ready", Row(cut, "proxy-state")));
        Click(cut, "Edit Environment");
        OpenTab(cut, "Target Application");
        cut.Find("input[type=url][placeholder='https://myapp.example.com']").Change("https://other.bufetat.no/");
        OpenTab(cut, "Authentication");
        cut.WaitForAssertion(() => Assert.Contains("Stale", Row(cut, "proxy-state")));
        Assert.Equal("Waiting for authenticated traffic", Row(cut, "proxy-credential"));
        cut.WaitForAssertion(() => _proxyApi.Verify(a => a.StopAsync(It.IsAny<LocalHttpsProxySessionRequest>()), Times.AtLeastOnce));
    }
}
