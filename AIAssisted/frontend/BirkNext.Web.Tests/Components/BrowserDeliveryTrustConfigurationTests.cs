using System.Text.Json;
using AngleSharp.Dom;
using BirkNext.ManagedEdge;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Component = BirkNext.Web.Components.FrontendAnalysisSettings;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// Browser delivery trust is SAVED Target Environment configuration (Authentication section), the single trust-policy source
/// of truth. It defaults to ExactOrigin, follows the normal Edit / Save changes / Cancel flow, survives reload and duplication,
/// and is never touched by detection, connection or verification. Observed delivery stays runtime-only.
/// </summary>
public sealed class BrowserDeliveryTrustConfigurationTests : BunitContext
{
    private const string Url = "https://m2lbdev.bufetat.no/";
    private const string Origin = "https://m2lbdev.bufetat.no";
    private const string ProxyOrigin = "https://m2lbdev-bufetat-no.access.mcas.ms";
    private readonly FrontendAnalysisSettingsService _settings = new();
    private readonly Mock<ITargetEnvironmentDetectionApiService> _detector = new();
    private readonly Mock<IManagedEdgeCdpApiService> _api = new();
    private readonly ManagedEdgeRuntime _runtime;

    public BrowserDeliveryTrustConfigurationTests()
    {
        _runtime = new ManagedEdgeRuntime(_api.Object);
        Services.AddSingleton<IFrontendAnalysisSettingsService>(_settings);
        Services.AddSingleton(_detector.Object);
        Services.AddSingleton(_runtime);
        JSInterop.SetupVoid("birkNextStorage.setItem", _ => true).SetVoidResult();
        _detector.Setup(a => a.DetectFromUrlAsync(It.IsAny<string>(), default)).ReturnsAsync(new TargetEnvironmentDetectionResult
        {
            Success = true, OriginalUrl = Url, Reachability = TargetReachability.Reachable,
            DetectedClientFramework = ClientFrameworkType.BlazorWebAssembly, DetectedAuthenticationType = FrontendAuthenticationType.MicrosoftEntraId,
            ManualAuthenticationVerificationRequired = true, State = DetectionState.ManualAuthenticationVerificationRequired
        });
        var connected = new ManagedEdgeStatus { SessionId = "s", State = ManagedEdgeState.ConnectedUnproven, OriginMatched = true, TargetOrigin = Origin, DeliveryOrigin = Origin, TrustDecision = ManagedEdgeTrustDecision.ExactOriginTrusted };
        _api.Setup(a => a.ConnectAsync(It.IsAny<ManagedEdgeConnectRequest>())).ReturnsAsync(connected);
        _api.Setup(a => a.StatusAsync(It.IsAny<ManagedEdgeSessionRequest>(), false)).ReturnsAsync(connected);
        _api.Setup(a => a.StatusAsync(It.IsAny<ManagedEdgeSessionRequest>(), true)).ReturnsAsync(connected with { State = ManagedEdgeState.ConnectedAuthenticated, RestAvailable = true });
        _api.Setup(a => a.DisconnectAsync(It.IsAny<ManagedEdgeSessionRequest>())).Returns(Task.CompletedTask);
    }

    private IRenderedComponent<Component> Open(string authenticationJson = """{ "authenticationType": "MicrosoftEntraId" }""")
    {
        JSInterop.Setup<string?>("birkNextStorage.getItem", _ => true).SetResult($$"""
            {"activeProfileId":"local","profiles":[
              {"id":"local","name":"Local","targetUrl":"https://local.example.com"},
              {"id":"dev","name":"M2LB DEV","environmentType":"Development","targetUrl":"{{Url}}","authentication":{{authenticationJson}}}
            ]}
            """);
        var cut = Render<Component>();
        cut.FindAll(".fa-profile-chip").Single(b => b.TextContent.Contains("M2LB DEV")).Click();
        return cut;
    }

    private static void Click(IRenderedComponent<Component> cut, string label) => cut.FindAll("button").Single(b => b.TextContent.Trim() == label).Click();
    private static void OpenTab(IRenderedComponent<Component> cut, string label) => cut.FindAll("[role=tab]").Single(t => t.TextContent.Trim() == label).Click();
    private static bool ButtonDisabled(IRenderedComponent<Component> cut, string label) => cut.FindAll("button").Single(b => b.TextContent.Trim() == label).HasAttribute("disabled");
    private static bool HasButton(IRenderedComponent<Component> cut, string label) => cut.FindAll("button").Any(b => b.TextContent.Trim() == label);
    private int SaveCalls() => JSInterop.Invocations.Count(i => i.Identifier == "birkNextStorage.setItem");
    private FrontendAnalysisProfile Persisted() => _settings.Settings.Profiles.Single(x => x.Id == "dev");
    private static IElement TrustSelect(IRenderedComponent<Component> cut) => cut.Find("#browser-delivery-trust-model");
    private static string Row(IRenderedComponent<Component> cut, string testId) => cut.Find($"[data-testid='{testId}']").TextContent.Trim();

    // ── Model / persistence ──────────────────────────────────────────────────

    [Fact]
    public void NewProfileDefaultsToExactOrigin()
    {
        var profile = new FrontendAnalysisSettingsService().CreateProfile("New", FrontendEnvironmentType.Development);
        Assert.Equal(ManagedEdgeTrustModel.ExactOrigin, profile.Authentication.BrowserDeliveryTrust);
        Assert.Equal(ManagedEdgeTrustModel.ExactOrigin, new FrontendAuthenticationSettings().BrowserDeliveryTrust);
    }

    [Fact]
    public void LegacyProfileWithoutTrustFieldDeserializesToExactOrigin()
    {
        // Same converter set as FrontendAnalysisSettingsService uses for browser storage (string enums).
        var storage = new JsonSerializerOptions { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
        var legacy = JsonSerializer.Deserialize<FrontendAnalysisProfile>("""{"id":"dev","name":"Dev","targetUrl":"https://m2lbdev.bufetat.no/","authentication":{"authenticationType":"MicrosoftEntraId","requiresAuthentication":true}}""", storage)!;
        Assert.Equal(ManagedEdgeTrustModel.ExactOrigin, legacy.Authentication.BrowserDeliveryTrust);
        Assert.Equal(FrontendAuthenticationType.MicrosoftEntraId, legacy.Authentication.AuthenticationType);
        var noAuth = JsonSerializer.Deserialize<FrontendAnalysisProfile>("""{"id":"dev","name":"Dev","targetUrl":"https://m2lbdev.bufetat.no/"}""", storage)!;
        Assert.Equal(ManagedEdgeTrustModel.ExactOrigin, noAuth.Authentication.BrowserDeliveryTrust);
    }

    [Fact]
    public void TrustModelIsSerializedOnceAsStringUnderAuthenticationAndRuntimeEvidenceNever()
    {
        var profile = new FrontendAnalysisProfile { Id = "dev", TargetUrl = Url };
        profile.Authentication.BrowserDeliveryTrust = ManagedEdgeTrustModel.ApprovedMcasProxyOrigin;
        var json = JsonSerializer.Serialize(profile);
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(json, "\"browserDeliveryTrust\"").Count);
        Assert.Contains("\"browserDeliveryTrust\":\"ApprovedMcasProxyOrigin\"", json);
        using var document = JsonDocument.Parse(json);
        Assert.Equal("ApprovedMcasProxyOrigin", document.RootElement.GetProperty("authentication").GetProperty("browserDeliveryTrust").GetString());
        foreach (var runtimeOnly in new[] { "deliveryOrigin", "DeliveryOrigin", "trustDecision", "TrustDecision", "correlationEvidence", "\"browserDelivery\"", "\"BrowserDelivery\"", "sessionId", "webSocketDebuggerUrl", "observedBrowserOrigin", "proxiedDelivery" })
            Assert.DoesNotContain(runtimeOnly, json);
        var roundTrip = JsonSerializer.Deserialize<FrontendAnalysisProfile>(json)!;
        Assert.Equal(ManagedEdgeTrustModel.ApprovedMcasProxyOrigin, roundTrip.Authentication.BrowserDeliveryTrust);
    }

    [Fact]
    public void DuplicateProfilePreservesTrustPolicy()
    {
        var settings = new FrontendAnalysisSettingsService();
        var source = settings.CreateProfile("Dev", FrontendEnvironmentType.Development);
        source.TargetUrl = Url;
        source.Authentication.BrowserDeliveryTrust = ManagedEdgeTrustModel.ApprovedMcasProxyOrigin;
        var copy = settings.DuplicateProfile(source.Id);
        Assert.NotEqual(source.Id, copy.Id);
        Assert.Equal(ManagedEdgeTrustModel.ApprovedMcasProxyOrigin, copy.Authentication.BrowserDeliveryTrust);
        Assert.Equal(ManagedEdgeTrustModel.ExactOrigin, settings.CreateProfile("Other", FrontendEnvironmentType.Test).Authentication.BrowserDeliveryTrust);
    }

    [Fact]
    public void ValidationWarnsWhenProxyTrustIsCombinedWithNonHttpsTarget()
    {
        var settings = new FrontendAnalysisSettingsService();
        var profile = settings.CreateProfile("Dev", FrontendEnvironmentType.Development);
        profile.TargetUrl = "http://m2lbdev.bufetat.no/";
        profile.Authentication.BrowserDeliveryTrust = ManagedEdgeTrustModel.ApprovedMcasProxyOrigin;
        var result = settings.ValidateProfile(profile);
        Assert.True(result.IsValid);
        Assert.Contains(result.Warnings, w => w.Contains("Frontend URL is not HTTPS"));
        Assert.Contains("HTTPS", BrowserDeliveryTrustLabels.Configuration(profile));
        profile.TargetUrl = Url;
        Assert.DoesNotContain(settings.ValidateProfile(profile).Warnings, w => w.Contains("Frontend URL is not HTTPS"));
        Assert.Equal("Valid", BrowserDeliveryTrustLabels.Configuration(profile));
    }

    // ── Authentication configuration UI ──────────────────────────────────────

    [Fact]
    public void AuthenticationTabShowsSavedTrustSectionReadOnlyWithExactOriginDefault()
    {
        var cut = Open();
        OpenTab(cut, "Authentication");
        var section = cut.Find("[data-testid='browser-delivery-trust']");
        Assert.Contains("Browser delivery trust", section.TextContent);
        Assert.Equal(Origin, Row(cut, "browser-delivery-trust-target"));
        Assert.Equal("Exact origin", Row(cut, "browser-delivery-trust-model"));
        Assert.Contains("Only an authenticated browser tab at the configured target origin is accepted.", Row(cut, "browser-delivery-trust-help"));
        Assert.Empty(cut.FindAll("input, select, textarea"));          // read-only until Edit Environment
        Assert.False(HasButton(cut, "Save changes"));
        Assert.DoesNotContain("Accept an approved Microsoft Defender for Cloud Apps proxied delivery", cut.Markup);
    }

    [Fact]
    public void SelectingApprovedProxyEntersDirtyDraftAndSavePersists()
    {
        var cut = Open();
        Click(cut, "Edit Environment");
        OpenTab(cut, "Authentication");
        Assert.Equal("ExactOrigin", TrustSelect(cut).GetAttribute("value"));
        Assert.True(ButtonDisabled(cut, "Save changes"));
        Assert.True(ButtonDisabled(cut, "Cancel"));

        TrustSelect(cut).Change("ApprovedMcasProxyOrigin");

        Assert.False(ButtonDisabled(cut, "Save changes"));                // dirty
        Assert.False(ButtonDisabled(cut, "Cancel"));
        Assert.Contains("Direct delivery at the configured target origin is always preferred", Row(cut, "browser-delivery-trust-help"));
        Assert.DoesNotContain("required", Row(cut, "browser-delivery-trust-help"), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Unsaved changes", cut.Markup);
        Assert.Equal(ManagedEdgeTrustModel.ExactOrigin, Persisted().Authentication.BrowserDeliveryTrust);   // not persisted yet
        Assert.Equal(0, SaveCalls());
        // The runtime panel mirrors the draft policy read-only.
        Assert.Contains("Approved MCAS proxy permitted", Row(cut, "edge-configured-trust"));

        Click(cut, "Save changes");
        cut.WaitForAssertion(() => Assert.Equal(ManagedEdgeTrustModel.ApprovedMcasProxyOrigin, Persisted().Authentication.BrowserDeliveryTrust));
        Assert.Equal(1, SaveCalls());
        cut.WaitForAssertion(() => Assert.False(HasButton(cut, "Save changes")));
        Assert.Equal(BrowserDeliveryTrustLabels.ApprovedProxyOption, Row(cut, "browser-delivery-trust-model"));
        // Persisted JSON carries the policy once and no runtime evidence.
        var json = JsonSerializer.Serialize(_settings.Settings);
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(json, "ApprovedMcasProxyOrigin").Count);
        Assert.DoesNotContain("deliveryOrigin", json);
        Assert.DoesNotContain("trustDecision", json);
    }

    [Fact]
    public void CancelRestoresPersistedTrustModel()
    {
        var cut = Open("""{ "authenticationType": "MicrosoftEntraId", "browserDeliveryTrust": "ExactOrigin" }""");
        Click(cut, "Edit Environment");
        OpenTab(cut, "Authentication");
        TrustSelect(cut).Change("ApprovedMcasProxyOrigin");
        Assert.False(ButtonDisabled(cut, "Cancel"));
        Click(cut, "Cancel");
        Assert.Equal(ManagedEdgeTrustModel.ExactOrigin, Persisted().Authentication.BrowserDeliveryTrust);
        Assert.Equal(0, SaveCalls());
        Assert.False(HasButton(cut, "Save changes"));
        Assert.Equal("Exact origin", Row(cut, "browser-delivery-trust-model"));
        Assert.Contains("Exact origin only", Row(cut, "edge-configured-trust"));
    }

    [Fact]
    public void ReloadRestoresPersistedApprovedProxyPolicy()
    {
        var cut = Open("""{ "authenticationType": "MicrosoftEntraId", "browserDeliveryTrust": "ApprovedMcasProxyOrigin" }""");
        Assert.Equal(ManagedEdgeTrustModel.ApprovedMcasProxyOrigin, Persisted().Authentication.BrowserDeliveryTrust);
        OpenTab(cut, "Authentication");
        Assert.Equal(BrowserDeliveryTrustLabels.ApprovedProxyOption, Row(cut, "browser-delivery-trust-model"));
        Assert.Contains("Approved MCAS proxy permitted", Row(cut, "edge-configured-trust"));
        Click(cut, "Edit Environment");
        Assert.Equal("ApprovedMcasProxyOrigin", TrustSelect(cut).GetAttribute("value"));
        Assert.True(ButtonDisabled(cut, "Save changes"));                 // opening the editor is not a change
    }

    [Fact]
    public void ChangingTrustPolicyKeepsPublicDetectionCurrentButStalesTheRuntimeSession()
    {
        var cut = Open();
        Click(cut, "Detect settings");
        cut.WaitForAssertion(() => Assert.Contains("Detected from target", cut.Markup));
        OpenTab(cut, "Authentication");
        Click(cut, "Connect to managed Edge");
        cut.WaitForAssertion(() => Assert.Equal("Exact origin — trusted", Row(cut, "edge-trust-decision")));
        Click(cut, "Edit Environment");
        TrustSelect(cut).Change("ApprovedMcasProxyOrigin");
        // Runtime evidence is bound to the verification-context fingerprint (includes authentication configuration) -> stale.
        cut.WaitForAssertion(() => Assert.Contains("Stale", cut.Find("[data-testid='edge-connection']").TextContent));
        // Public target discovery is identity-only (profile + URL) and must stay current.
        Assert.DoesNotContain("Needs re-check", cut.Markup);
        Assert.DoesNotContain("Discoveries are stale", cut.Markup);
        Assert.DoesNotContain("target URL changed", cut.Markup);
        OpenTab(cut, "Validation");
        var freshness = cut.FindAll(".fa-dl-row").Single(r => r.TextContent.Contains("Detection freshness")).TextContent;
        Assert.Contains("Current", freshness);
        Assert.DoesNotContain("Stale", freshness);
    }

    // ── Runtime never writes the policy ───────────────────────────────────────

    [Fact]
    public void DetectConnectVerifyDisconnectNeverModifyTrustModelOrDirtyTheEnvironment()
    {
        var cut = Open("""{ "authenticationType": "MicrosoftEntraId", "browserDeliveryTrust": "ApprovedMcasProxyOrigin" }""");
        var persisted = JsonSerializer.Serialize(_settings.Settings);
        Click(cut, "Detect settings");
        cut.WaitForAssertion(() => Assert.Contains("Detected from target", cut.Markup));
        OpenTab(cut, "Authentication");
        foreach (var action in new[] { "Connect to managed Edge", "Verify authenticated access", "Disconnect BirkNext from Edge" })
        {
            Click(cut, action);
            cut.WaitForAssertion(() => Assert.False(cut.FindAll("button").Single(b => b.TextContent.Trim() == "Check Edge compatibility").HasAttribute("disabled")));
            Assert.False(HasButton(cut, "Save changes"));
            Assert.False(HasButton(cut, "Cancel"));
            Assert.Empty(cut.FindAll("input, select, textarea"));
            Assert.Equal(persisted, JsonSerializer.Serialize(_settings.Settings));
            Assert.Equal(ManagedEdgeTrustModel.ApprovedMcasProxyOrigin, Persisted().Authentication.BrowserDeliveryTrust);
            Assert.Equal(0, SaveCalls());
            Assert.DoesNotContain("Needs re-check", cut.Markup);
        }
        _api.Verify(a => a.ConnectAsync(It.Is<ManagedEdgeConnectRequest>(r => r.TrustModel == ManagedEdgeTrustModel.ApprovedMcasProxyOrigin && r.ProfileId == "dev")), Times.Once);
    }

    // ── Runtime panel and Validation tab reflect what was actually observed ───

    [Fact]
    public void DirectDeliveryUnderProxyPermittedPolicyIsTrustedAndShownEverywhere()
    {
        var direct = new ManagedEdgeStatus
        {
            SessionId = "s", State = ManagedEdgeState.ConnectedAuthenticated, OriginMatched = true, TargetOrigin = Origin, DeliveryOrigin = Origin,
            TrustModel = ManagedEdgeTrustModel.ApprovedMcasProxyOrigin, TrustDecision = ManagedEdgeTrustDecision.ExactOriginTrusted, RestAvailable = true,
            Evidence = "Expected origin matched; administrator-approved protected GET returned the expected status and content type."
        };
        _api.Setup(a => a.ConnectAsync(It.IsAny<ManagedEdgeConnectRequest>())).ReturnsAsync(direct);
        _api.Setup(a => a.StatusAsync(It.IsAny<ManagedEdgeSessionRequest>(), It.IsAny<bool>())).ReturnsAsync(direct);
        var cut = Open("""{ "authenticationType": "MicrosoftEntraId", "browserDeliveryTrust": "ApprovedMcasProxyOrigin" }""");
        OpenTab(cut, "Authentication");
        Click(cut, "Connect to managed Edge");
        cut.WaitForAssertion(() => Assert.Equal("Exact origin — trusted", Row(cut, "edge-trust-decision")));
        Assert.Equal("Direct", Row(cut, "edge-delivery"));
        Assert.Contains("Approved MCAS proxy permitted", Row(cut, "edge-configured-trust"));
        OpenTab(cut, "Validation");
        Assert.Equal("Valid", Row(cut, "validation-trust-configuration"));
        Assert.Equal(BrowserDeliveryTrustLabels.ApprovedProxyOption, Row(cut, "validation-configured-trust"));
        Assert.Equal("Direct", Row(cut, "validation-observed-delivery"));
        Assert.Equal("Verified", Row(cut, "validation-trust-decision"));
        Assert.Contains("Verified", cut.FindAll(".fa-dl-row").Single(r => r.TextContent.Contains("Authenticated access")).TextContent);
        Assert.False(HasButton(cut, "Save changes"));
    }

    [Fact]
    public void ProxiedDeliveryUnderProxyPermittedPolicyIsShownAsVerifiedViaApprovedProxy()
    {
        var proxied = new ManagedEdgeStatus
        {
            SessionId = "s", State = ManagedEdgeState.ConnectedAuthenticated, OriginMatched = true, TargetOrigin = Origin, DeliveryOrigin = ProxyOrigin,
            TrustModel = ManagedEdgeTrustModel.ApprovedMcasProxyOrigin, TrustDecision = ManagedEdgeTrustDecision.ApprovedProxyTrusted, RestAvailable = true,
            CorrelationEvidence = "Saved policy permits approved MCAS proxy; HTTPS access.mcas.ms delivery; live document origin matches; navigation history includes configured target, Entra authority."
        };
        _api.Setup(a => a.ConnectAsync(It.IsAny<ManagedEdgeConnectRequest>())).ReturnsAsync(proxied);
        _api.Setup(a => a.StatusAsync(It.IsAny<ManagedEdgeSessionRequest>(), It.IsAny<bool>())).ReturnsAsync(proxied);
        var cut = Open("""{ "authenticationType": "MicrosoftEntraId", "browserDeliveryTrust": "ApprovedMcasProxyOrigin" }""");
        OpenTab(cut, "Authentication");
        Click(cut, "Connect to managed Edge");
        cut.WaitForAssertion(() => Assert.Equal("Approved correlated MCAS proxy — trusted", Row(cut, "edge-trust-decision")));
        Assert.Equal("Microsoft Defender for Cloud Apps proxy", Row(cut, "edge-delivery"));
        Assert.Equal(ProxyOrigin, Row(cut, "edge-observed-origin"));
        Assert.Equal(Origin, Row(cut, "browser-delivery-trust-target"));           // configured target unchanged in configuration
        OpenTab(cut, "Validation");
        Assert.Equal("Microsoft Defender for Cloud Apps proxy", Row(cut, "validation-observed-delivery"));
        Assert.Equal("Verified", Row(cut, "validation-correlation"));
        Assert.Equal("Verified via approved proxy", Row(cut, "validation-trust-decision"));
        Assert.Equal(ManagedEdgeTrustModel.ApprovedMcasProxyOrigin, Persisted().Authentication.BrowserDeliveryTrust);
        Assert.DoesNotContain("access.mcas.ms", JsonSerializer.Serialize(_settings.Settings));   // observed origin never persisted
    }

    [Fact]
    public void ExactOriginEnvironmentShowsProxyRejectionInPanelAndValidation()
    {
        _api.Setup(a => a.ConnectAsync(It.IsAny<ManagedEdgeConnectRequest>())).ReturnsAsync(new ManagedEdgeStatus
        {
            State = ManagedEdgeState.ProxiedDeliveryNotPermitted, TargetOrigin = Origin, DeliveryOrigin = ProxyOrigin,
            TrustModel = ManagedEdgeTrustModel.ExactOrigin, TrustDecision = ManagedEdgeTrustDecision.ProxyNotPermitted,
            Evidence = "Browser delivery is proxied by Microsoft Defender for Cloud Apps, but this environment permits exact-origin delivery only."
        });
        var cut = Open();
        OpenTab(cut, "Authentication");
        Click(cut, "Connect to managed Edge");
        cut.WaitForAssertion(() => Assert.Contains("permits exact-origin delivery only", cut.Markup));
        Assert.Contains("not permitted by this environment", Row(cut, "edge-trust-decision"));
        Assert.Equal("Microsoft Defender for Cloud Apps proxy", Row(cut, "edge-delivery"));
        Assert.Contains("Not verified", cut.Markup);
        OpenTab(cut, "Validation");
        Assert.Equal("Exact origin", Row(cut, "validation-configured-trust"));
        Assert.Equal("Microsoft Defender for Cloud Apps proxy", Row(cut, "validation-observed-delivery"));
        Assert.Contains("not permitted", Row(cut, "validation-correlation"));
        Assert.Equal("Not trusted", Row(cut, "validation-trust-decision"));
        Assert.Equal(ManagedEdgeTrustModel.ExactOrigin, Persisted().Authentication.BrowserDeliveryTrust);   // nothing auto-migrated
        Assert.Equal(0, SaveCalls());
    }

    [Fact]
    public void ProxyCorrelationFailureIsShownAsFailedAndNotTrusted()
    {
        _api.Setup(a => a.ConnectAsync(It.IsAny<ManagedEdgeConnectRequest>())).ReturnsAsync(new ManagedEdgeStatus
        {
            State = ManagedEdgeState.ProxiedDeliveryUncorrelated, TargetOrigin = Origin, DeliveryOrigin = ProxyOrigin,
            TrustModel = ManagedEdgeTrustModel.ApprovedMcasProxyOrigin, TrustDecision = ManagedEdgeTrustDecision.ProxyCorrelationFailed,
            Evidence = "A proxied tab for this application is open, but BirkNext could not correlate it to the configured target through the browser session."
        });
        var cut = Open("""{ "authenticationType": "MicrosoftEntraId", "browserDeliveryTrust": "ApprovedMcasProxyOrigin" }""");
        OpenTab(cut, "Authentication");
        Click(cut, "Connect to managed Edge");
        cut.WaitForAssertion(() => Assert.Equal("Proxy correlation failed", Row(cut, "edge-trust-decision")));
        OpenTab(cut, "Validation");
        Assert.Equal("Failed", Row(cut, "validation-correlation"));
        Assert.Equal("Not trusted", Row(cut, "validation-trust-decision"));
    }
}
