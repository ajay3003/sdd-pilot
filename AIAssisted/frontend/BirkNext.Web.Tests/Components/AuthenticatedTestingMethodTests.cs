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
        Evidence = "Authenticated API context available (memory only).",
        // Authenticated API endpoints discovered from observed traffic (never assumed /health or /graphql). No credential.
        ObservedEndpoints =
        [
            new() { EndpointType = ObservedEndpointType.Rest, Origin = Origin, Path = "/api/children", Method = "GET", ResponseStatus = 200, ResponseContentType = "application/json", BearerObserved = true, Confidence = ObservedEndpointConfidence.Verified, Count = 3, LastObservedAt = DateTimeOffset.UtcNow },
            new() { EndpointType = ObservedEndpointType.GraphQl, Origin = Origin, Path = "/internal/gql", Method = "POST", ResponseStatus = 200, RequestContentType = "application/json", ResponseContentType = "application/json", BearerObserved = true, Confidence = ObservedEndpointConfidence.Verified, OperationType = GraphQlOperationType.Query, OperationName = "Me", Count = 1, LastObservedAt = DateTimeOffset.UtcNow }
        ],
        ObservedNetworkEndpoints =
        [
            new() { Category = ObservedTrafficCategory.Rest, Scheme = "https", Host = "m2lbdev.bufetat.no", Port = 443, Path = "/api/children", Method = "GET", AuthObserved = true, LastStatus = 200, Confidence = ObservedEndpointConfidence.Verified, Count = 3, FirstObservedAt = DateTimeOffset.UtcNow, LastObservedAt = DateTimeOffset.UtcNow, PageOrigin = Origin, PagePath = "/barn/1" }
        ]
    };

    public AuthenticatedTestingMethodTests()
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

    [Theory]
    [InlineData(FrontendEnvironmentType.Development, AuthenticatedTestingMethod.LocalHttpsProxy)]
    [InlineData(FrontendEnvironmentType.QA, AuthenticatedTestingMethod.LocalHttpsProxy)]
    [InlineData(FrontendEnvironmentType.Test, AuthenticatedTestingMethod.LocalHttpsProxy)]
    [InlineData(FrontendEnvironmentType.RC, AuthenticatedTestingMethod.LocalHttpsProxy)]
    [InlineData(FrontendEnvironmentType.Production, AuthenticatedTestingMethod.ManagedEdgeCdp)]
    [InlineData(FrontendEnvironmentType.Local, AuthenticatedTestingMethod.ManagedEdgeCdp)]
    [InlineData(FrontendEnvironmentType.Custom, AuthenticatedTestingMethod.ManagedEdgeCdp)]
    public void NewProfileDefaultsByEnvironmentType(FrontendEnvironmentType type, AuthenticatedTestingMethod expected) =>
        Assert.Equal(expected, new FrontendAnalysisSettingsService().CreateProfile("New", type).Authentication.AuthenticatedTestingMethod);

    [Fact]
    public void PersistedModelDefaultStaysManagedEdgeCdpForLegacyDeserialization()
    {
        // The default applied at creation is environment-specific, but the persisted-model default must stay ManagedEdgeCdp so legacy JSON
        // without the field keeps deserializing to ManagedEdgeCdp (see LegacyProfileWithoutTheFieldDeserializesToManagedEdgeCdp).
        Assert.Equal(AuthenticatedTestingMethod.ManagedEdgeCdp, new FrontendAuthenticationSettings().AuthenticatedTestingMethod);
        Assert.Equal(AuthenticatedTestingMethod.ManagedEdgeCdp, new FrontendAnalysisProfile().Authentication.AuthenticatedTestingMethod);
    }

    [Theory]
    [InlineData(AuthenticatedTestingMethod.ManagedEdgeCdp)]
    [InlineData(AuthenticatedTestingMethod.LocalHttpsProxy)]
    [InlineData(AuthenticatedTestingMethod.ManualOnly)]
    public void ExplicitlyChosenMethodPersistsThroughSaveAndReload(AuthenticatedTestingMethod method)
    {
        var settings = new FrontendAnalysisSettingsService();
        var profile = settings.CreateProfile("Env", FrontendEnvironmentType.Development);
        profile.Authentication.AuthenticatedTestingMethod = method;
        settings.UpdateProfile(profile);
        var reloaded = JsonSerializer.Deserialize<FrontendAnalysisProfile>(JsonSerializer.Serialize(settings.Settings.Profiles.Single(p => p.Id == profile.Id)))!;
        Assert.Equal(method, reloaded.Authentication.AuthenticatedTestingMethod);
    }

    [Theory]
    [InlineData(FrontendEnvironmentType.Development, AuthenticatedTestingMethod.LocalHttpsProxy)]
    [InlineData(FrontendEnvironmentType.QA, AuthenticatedTestingMethod.LocalHttpsProxy)]
    [InlineData(FrontendEnvironmentType.Test, AuthenticatedTestingMethod.LocalHttpsProxy)]
    [InlineData(FrontendEnvironmentType.RC, AuthenticatedTestingMethod.LocalHttpsProxy)]
    [InlineData(FrontendEnvironmentType.Production, AuthenticatedTestingMethod.ManagedEdgeCdp)]
    [InlineData(FrontendEnvironmentType.Local, AuthenticatedTestingMethod.ManagedEdgeCdp)]
    public void ResetRestoresEnvironmentTypeDefaultMethodAndKeepsIdentity(FrontendEnvironmentType type, AuthenticatedTestingMethod expected)
    {
        var settings = new FrontendAnalysisSettingsService();
        var profile = settings.CreateProfile("Env", type);
        profile.TargetUrl = "https://target.example.test/";
        // Move off the default, then reset.
        profile.Authentication.AuthenticatedTestingMethod = expected == AuthenticatedTestingMethod.LocalHttpsProxy ? AuthenticatedTestingMethod.ManualOnly : AuthenticatedTestingMethod.LocalHttpsProxy;
        settings.ResetProfile(profile.Id);
        var reset = settings.Settings.Profiles.Single(p => p.Id == profile.Id);
        Assert.Equal(expected, reset.Authentication.AuthenticatedTestingMethod);
        Assert.Equal("Env", reset.Name);
        Assert.Equal(type, reset.EnvironmentType);
        Assert.Equal("https://target.example.test/", reset.TargetUrl);
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
        // Duplicate preserves the SOURCE's method, even when it differs from the new-profile default for that environment type.
        var cdpSource = settings.CreateProfile("Cdp", FrontendEnvironmentType.Development);
        cdpSource.Authentication.AuthenticatedTestingMethod = AuthenticatedTestingMethod.ManagedEdgeCdp;
        Assert.Equal(AuthenticatedTestingMethod.ManagedEdgeCdp, settings.DuplicateProfile(cdpSource.Id).Authentication.AuthenticatedTestingMethod);
        var manualSource = settings.CreateProfile("Manual", FrontendEnvironmentType.QA);
        manualSource.Authentication.AuthenticatedTestingMethod = AuthenticatedTestingMethod.ManualOnly;
        Assert.Equal(AuthenticatedTestingMethod.ManualOnly, settings.DuplicateProfile(manualSource.Id).Authentication.AuthenticatedTestingMethod);
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

    [Fact]
    public void ReadOnlyShowsSelectedMethodAndNamesOfTheOtherMethods()
    {
        var cut = Open(); // dev profile from legacy JSON (no field) => ManagedEdgeCdp
        OpenTab(cut, "Authentication");
        Assert.Empty(cut.FindAll("#authenticated-testing-method-select"));
        Assert.Equal(AuthenticatedTestingMethodLabels.CdpOption, Row(cut, "authenticated-testing-method-value"));
        var others = Row(cut, "authenticated-testing-method-others");
        Assert.Contains(AuthenticatedTestingMethodLabels.ProxyOption, others);
        Assert.Contains(AuthenticatedTestingMethodLabels.ManualOption, others);
        Assert.DoesNotContain(AuthenticatedTestingMethodLabels.CdpOption, others);
        // Read-only must not render any runtime panel for an unselected method.
        Assert.True(Has(cut, "managed-edge-panel"));
        Assert.False(Has(cut, "local-https-proxy-panel"));
        Assert.False(Has(cut, "manual-only-panel"));
    }

    [Fact]
    public void EditModeExposesAllThreeChoicesWithProxyRecommended()
    {
        var cut = Open();
        OpenTab(cut, "Authentication");
        Click(cut, "Edit Environment");
        var options = cut.Find("#authenticated-testing-method-select").QuerySelectorAll("option");
        Assert.Equal(3, options.Length);
        var texts = options.Select(o => o.TextContent.Trim()).ToArray();
        Assert.Contains(texts, t => t.Contains(AuthenticatedTestingMethodLabels.ProxyOption) && t.Contains("recommended"));
        Assert.Contains(AuthenticatedTestingMethodLabels.CdpOption, texts);
        Assert.Contains(AuthenticatedTestingMethodLabels.ManualOption, texts);
        Assert.Equal(0, SaveCalls());
    }

    // ── CDP blocked: no silent switch (section 26) ───────────────────────────

    [Fact]
    public void DetectionHasOneCompactSummaryAndExplicitApply()
    {
        var cut = Open();
        Click(cut, "Detect settings");
        OpenTab(cut, "Authentication");
        Assert.Single(cut.FindAll("h3").Where(x => x.TextContent == "Detected & configured authentication"));
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
        // The capability inventory lives in the selected method.s setup panel, one disclosure away.
        foreach (var candidate in new[] { "managed-edge-panel", "local-https-proxy-panel", "manual-only-panel" })
            Assert.Equal(candidate == panel, Has(cut, candidate));
        Assert.Equal(method == AuthenticatedTestingMethod.ManagedEdgeCdp, Has(cut, "browser-delivery-trust"));
        Assert.Equal(method == AuthenticatedTestingMethod.LocalHttpsProxy, Has(cut, "proxy-security-warning"));
        // Method is a row of the one Authenticated testing card now, not a card of its own.
        Assert.Empty(cut.FindAll("[data-testid='authenticated-testing-method']"));
        Assert.True(cut.Markup.IndexOf("data-testid=\"authentication-discovery\"") < cut.Markup.IndexOf("data-testid=\"authenticated-testing\""));
        Assert.True(cut.Markup.IndexOf("data-testid=\"authenticated-testing\"") < cut.Markup.IndexOf($"data-testid=\"{panel}\""));
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
        cut.WaitForAssertion(() => Assert.Equal("Verified", Row(cut, "capability-rest")));
        Click(cut, "Edit Environment");
        SelectMethod(cut, AuthenticatedTestingMethod.ManualOnly);
        // Switching away stops the proxy asynchronously; once it settles every authenticated capability is cleared — nothing reads "Available"
        // or "Verified" (the cleared wording is "Unavailable" on the manual grid or "Not observed" on the just-cleared proxy grid; both mean not authenticated).
        cut.WaitForAssertion(() => _proxyApi.Verify(x => x.StopAsync(It.IsAny<LocalHttpsProxySessionRequest>()), Times.AtLeastOnce));
        cut.WaitForState(() => Has(cut, "manual-only-panel"));
        Assert.False(Has(cut, "proxy-credential"));
        AssertNoAuthenticatedCapabilityClaimed(cut);
        SelectMethod(cut, AuthenticatedTestingMethod.ManagedEdgeCdp);
        cut.WaitForState(() => Has(cut, "managed-edge-panel"));
        AssertNoAuthenticatedCapabilityClaimed(cut);
        Click(cut, "Cancel");
        Assert.Equal(AuthenticatedTestingMethod.LocalHttpsProxy, Persisted().Authentication.AuthenticatedTestingMethod);
        Assert.Equal(0, SaveCalls());
    }

    // No authenticated surface is claimed: every capability row reads a cleared value, never "Available" or "Verified".
    private static void AssertNoAuthenticatedCapabilityClaimed(IRenderedComponent<Component> cut)
    {
        foreach (var capability in new[] { "api", "dom", "rest", "graphql" })
            Assert.Contains(Row(cut, $"capability-{capability}"), new[] { "Unavailable", "Not observed" });
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
        Assert.Contains("fa-state-blocked", cut.Find("[data-testid='managed-edge-panel']").ClassList);
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
        Assert.Contains("fa-state-neutral", cut.Find("[data-testid='local-https-proxy-panel']").ClassList);

        await cut.InvokeAsync(() => Click(cut, "Check proxy compatibility"));
        cut.WaitForAssertion(() => Assert.Contains("m2lbdev.bufetat.no:443", Row(cut, "proxy-approved-hosts")));
        await cut.InvokeAsync(() => Click(cut, "Start authenticated proxy"));
        cut.WaitForAssertion(() => Assert.Equal("Ready", Row(cut, "proxy-state")));
        Assert.Contains("fa-state-ready", cut.Find("[data-testid='local-https-proxy-panel']").ClassList);
        Assert.Contains("Runtime: Authenticated API context available", Row(cut, "local-https-proxy-panel"));
        Assert.Equal("127.0.0.1:8888", Row(cut, "proxy-endpoint"));
        Assert.Equal("Trusted", Row(cut, "proxy-certificate"));
        Assert.Equal("Detected", Row(cut, "proxy-authenticated-traffic"));
        Assert.Equal("Available - memory only", Row(cut, "proxy-credential"));
        Assert.True(Has(cut, "proxy-credential-expiry"));
        // REST/GraphQL are verified from observed traffic, not from the credential; the auth context is separately "Available".
        // (The detailed per-page endpoint tables live in the Endpoint Discovery tab, not here.)
        Assert.Equal("Verified", Row(cut, "capability-rest"));
        Assert.Equal("Verified", Row(cut, "capability-graphql"));
        Assert.Equal("Unavailable", Row(cut, "capability-dom"));
        Assert.Equal("Available", Row(cut, "capability-api"));
        Assert.False(Has(cut, "discovered-rest"));
        Assert.False(Has(cut, "proxy-checks"));

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

    // ── proxy Step 3 — Browser uses normal managed Edge only (no integrated launch) ──

    [Fact]
    public void ProxyBrowserStepUsesNormalManagedEdgeAndHasNoIntegratedLaunch()
    {
        var cut = Open(authenticationJson: """{ "authenticatedTestingMethod": "LocalHttpsProxy" }""");
        OpenTab(cut, "Authentication");
        Assert.True(Has(cut, "local-https-proxy-panel"));
        Assert.True(Has(cut, "proxy-browser-step"));
        // No integrated/separate browser launch in proxy mode.
        Assert.False(HasButton(cut, "Start Edge with proxy"));
        Assert.DoesNotContain("--proxy-server", cut.Markup);
        Assert.DoesNotContain("--remote-debugging", cut.Markup);
        // Normal managed Edge / manual instructions.
        Assert.Equal("Manual", Row(cut, "proxy-browser-auth"));
        Assert.Contains("normal managed Microsoft Edge", cut.Markup);
        Assert.True(Has(cut, "proxy-setup-instructions"));
        Assert.Contains("does not change your default Windows or Edge proxy settings", Row(cut, "proxy-browser-note"));
        Assert.Contains("127.0.0.1", Row(cut, "proxy-browser-endpoint"));
        // Before the proxy is started the browser step is not actionable.
        Assert.Contains("Complete Steps 1", Row(cut, "proxy-browser-prerequisite"));
    }

    [Fact]
    public async Task ProxyBrowserStepBecomesReadyOnceProxyListeningAndCertificateTrusted()
    {
        var cut = Open(authenticationJson: """{ "authenticatedTestingMethod": "LocalHttpsProxy" }""");
        OpenTab(cut, "Authentication");
        await cut.InvokeAsync(() => Click(cut, "Start authenticated proxy"));
        cut.WaitForAssertion(() => Assert.Equal("Ready", Row(cut, "proxy-state")));
        // Ready mock is Listening with a Trusted certificate → no prerequisite blocker and a concrete endpoint.
        Assert.False(Has(cut, "proxy-browser-prerequisite"));
        Assert.Equal("127.0.0.1:8888", Row(cut, "proxy-browser-endpoint"));
    }

    [Fact]
    public void CdpBrowserLaunchRemainsAvailableForCdpMethod()
    {
        var cut = Open(); // legacy JSON (no field) => ManagedEdgeCdp
        OpenTab(cut, "Authentication");
        Assert.True(Has(cut, "managed-edge-panel"));
        Assert.False(Has(cut, "local-https-proxy-panel"));
        // CDP keeps its own managed-Edge launch action.
        Assert.Contains("Start Edge for authenticated testing", cut.Markup);
        Assert.True(HasButton(cut, "Check Edge compatibility"));
    }

    // ── proxy session lifetime follows the runtime/environment, not the Authentication component ──

    [Fact]
    public async Task NavigatingAwayFromAuthenticationDoesNotStopTheProxy()
    {
        var cut = Open(authenticationJson: """{ "authenticatedTestingMethod": "LocalHttpsProxy" }""");
        OpenTab(cut, "Authentication");
        await cut.InvokeAsync(() => Click(cut, "Start authenticated proxy"));
        cut.WaitForAssertion(() => Assert.Equal("Ready", Row(cut, "proxy-state")));
        Assert.True(_proxyRuntime.SessionActive);

        // Navigating to another page unmounts/disposes this component. It must NOT stop the proxy or wipe the context.
        await cut.InvokeAsync(() => cut.Instance.DisposeAsync());

        _proxyApi.Verify(a => a.StopAsync(It.IsAny<LocalHttpsProxySessionRequest>()), Times.Never);
        Assert.True(_proxyRuntime.SessionActive);
        Assert.True(_proxyRuntime.Status.AuthenticatedCredentialAvailable);
    }

    [Fact]
    public async Task NavigatingAwayFromAuthenticationDoesNotDisconnectCdp()
    {
        var connected = new ManagedEdgeStatus { SessionId = "edge-session", State = ManagedEdgeState.ConnectedUnproven, OriginMatched = true, TargetOrigin = Origin };
        _edgeApi.Setup(a => a.ConnectAsync(It.IsAny<ManagedEdgeConnectRequest>())).ReturnsAsync(connected);
        _edgeApi.Setup(a => a.StatusAsync(It.IsAny<ManagedEdgeSessionRequest>(), It.IsAny<bool>())).ReturnsAsync(connected);
        var cut = Open();
        OpenTab(cut, "Authentication");
        await cut.InvokeAsync(() => Click(cut, "Connect to existing Edge"));
        cut.WaitForAssertion(() => Assert.NotNull(_edgeRuntime.Status.SessionId));
        // Navigating away must not disconnect the CDP session either (same runtime-scoped lifetime model).
        await cut.InvokeAsync(() => cut.Instance.DisposeAsync());
        _edgeApi.Verify(a => a.DisconnectAsync(It.IsAny<ManagedEdgeSessionRequest>()), Times.Never);
        Assert.NotNull(_edgeRuntime.Status.SessionId);
    }

    // ── Authentication keeps only the auth-context summary; detailed endpoint discovery moved to the Endpoint Discovery tab (sections 5, 56) ──

    [Fact]
    public async Task AuthenticationProxyPanelKeepsOnlyTheContextSummaryAndLinksToEndpointDiscovery()
    {
        var cut = Open(authenticationJson: """{ "authenticatedTestingMethod": "LocalHttpsProxy" }""");
        OpenTab(cut, "Authentication");
        await cut.InvokeAsync(() => Click(cut, "Start authenticated proxy"));
        cut.WaitForAssertion(() => Assert.Equal("Ready", Row(cut, "proxy-state")));

        // The compact context/capability summary stays; the detailed endpoint tables and replay checks are gone from Authentication.
        Assert.Equal("Available - memory only", Row(cut, "proxy-credential"));
        Assert.Equal("Available", Row(cut, "capability-api"));
        Assert.False(Has(cut, "discovered-rest"));
        Assert.False(Has(cut, "discovered-graphql"));
        Assert.False(Has(cut, "observed-endpoints"));
        Assert.False(Has(cut, "proxy-checks"));
        Assert.False(HasButton(cut, "Run authenticated REST GET"));
        // A link points to the new tab.
        Assert.True(Has(cut, "proxy-discovery-link"));
        Assert.True(HasButton(cut, "View Endpoint Discovery"));
    }

    [Fact]
    public async Task AuthContextAvailableWithoutObservedTrafficReportsNotVerifiedCapability()
    {
        // A credential is available but no REST/GraphQL endpoint has been observed yet: context available, neither surface verified.
        var contextOnly = Ready with { ObservedEndpoints = [], ObservedNetworkEndpoints = [], AuthenticatedRequestsObserved = 1 };
        _proxyApi.Setup(a => a.StartAsync(It.IsAny<LocalHttpsProxyScopeRequest>())).ReturnsAsync(contextOnly);
        _proxyApi.Setup(a => a.StatusAsync(It.IsAny<LocalHttpsProxySessionRequest>())).ReturnsAsync(contextOnly);
        var cut = Open(authenticationJson: """{ "authenticatedTestingMethod": "LocalHttpsProxy" }""");
        OpenTab(cut, "Authentication");
        await cut.InvokeAsync(() => Click(cut, "Start authenticated proxy"));
        cut.WaitForAssertion(() => Assert.Equal("Ready", Row(cut, "proxy-state")));

        Assert.Equal("Available", Row(cut, "capability-api"));
        Assert.Equal("Not observed", Row(cut, "capability-rest"));
        Assert.Equal("Not observed", Row(cut, "capability-graphql"));
    }

    [Fact]
    public async Task NavigatingAwayDoesNotClearObservedApiEndpoints()
    {
        var cut = Open(authenticationJson: """{ "authenticatedTestingMethod": "LocalHttpsProxy" }""");
        OpenTab(cut, "Authentication");
        await cut.InvokeAsync(() => Click(cut, "Start authenticated proxy"));
        cut.WaitForAssertion(() => Assert.True(_proxyRuntime.Status.AuthenticatedRestObserved));

        // Endpoint discovery lives in the app-scoped runtime, not the component: navigation must not clear it.
        await cut.InvokeAsync(() => cut.Instance.DisposeAsync());
        _proxyApi.Verify(a => a.StopAsync(It.IsAny<LocalHttpsProxySessionRequest>()), Times.Never);
        Assert.True(_proxyRuntime.Status.AuthenticatedRestObserved);
        Assert.NotEmpty(_proxyRuntime.Status.ObservedEndpoints);
    }

    [Fact]
    public async Task NavigatingWithinBirkNextDoesNotClearEndpointDiscovery()
    {
        var discovery = Services.GetRequiredService<IEndpointDiscoveryService>();
        var cut = Open(authenticationJson: """{ "authenticatedTestingMethod": "LocalHttpsProxy" }""");
        OpenTab(cut, "Authentication");
        await cut.InvokeAsync(() => Click(cut, "Start authenticated proxy"));
        // Starting the proxy folds observed traffic into the persisted, app-scoped discovery store.
        cut.WaitForAssertion(() => Assert.NotEmpty(discovery.GetSnapshot("dev").Pages));

        // Navigating away (component dispose) must NOT clear the endpoint discovery held in the app-scoped store.
        await cut.InvokeAsync(() => cut.Instance.DisposeAsync());
        Assert.NotEmpty(discovery.GetSnapshot("dev").Pages);
        Assert.Contains(discovery.GetSnapshot("dev").Pages, p => p.PagePath == "/barn/1");
    }
}
