using System.Text.Json;
using AngleSharp.Dom;
using BirkNext.ManagedEdge;
using BirkNext.Web.Components;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Settings = BirkNext.Web.Components.FrontendAnalysisSettings;

namespace BirkNext.Web.Tests.Components;

public sealed class ManagedEdgePreflightUiTests : BunitContext
{
    private readonly Mock<IManagedEdgeCdpApiService> _api = new();
    private readonly FrontendAnalysisProfile _profile = new() { Id = "dev", TargetUrl = "https://m2lbdev.bufetat.no/" };
    private readonly ManagedEdgePreflightResult _compatible = new()
    {
        LocalBrowserIntegrationAvailable = true, EdgeInstalled = true, EdgeExecutablePath = @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe", EdgeVersion = "152.0.4191.66",
        RemoteDebuggingPolicyStatus = EdgeRemoteDebuggingPolicyStatus.NotConfigured, CdpEndpoint = "http://127.0.0.1:9222",
        TargetOrigin = "https://m2lbdev.bufetat.no", CanLaunchTestEdge = true, FailureReason = "Remote debugging is not active on the CDP endpoint. Start Edge for authenticated testing or start an approved Edge instance manually."
    };

    public ManagedEdgePreflightUiTests()
    {
        _api.Setup(a => a.PreflightAsync(It.IsAny<ManagedEdgePreflightRequest>())).ReturnsAsync(_compatible);
        _api.Setup(a => a.LaunchAsync(It.IsAny<ManagedEdgeLaunchRequest>())).ReturnsAsync(_compatible with
        {
            EdgeStarted = true, CdpEndpointReachable = true, CdpProtocolValid = true, BrowserProduct = "Edg", BrowserVersion = "152.0.4191.66", CanConnect = true, CanLaunchTestEdge = false, FailureReason = null
        });
        _api.Setup(a => a.ConnectAsync(It.IsAny<ManagedEdgeConnectRequest>())).ReturnsAsync(new ManagedEdgeStatus { SessionId = "runtime-only", State = ManagedEdgeState.ConnectedUnproven, OriginMatched = true, TargetOrigin = "https://m2lbdev.bufetat.no" });
        _api.Setup(a => a.StatusAsync(It.IsAny<ManagedEdgeSessionRequest>(), It.IsAny<bool>())).ReturnsAsync(new ManagedEdgeStatus { SessionId = "runtime-only", State = ManagedEdgeState.ConnectedUnproven, OriginMatched = true, TargetOrigin = "https://m2lbdev.bufetat.no" });
        _api.Setup(a => a.DisconnectAsync(It.IsAny<ManagedEdgeSessionRequest>())).Returns(Task.CompletedTask);
    }

    private IRenderedComponent<ManagedEdgeConnectionPanel> Panel(ManagedEdgeRuntime runtime) =>
        Render<ManagedEdgeConnectionPanel>(p => p.Add(x => x.Runtime, runtime).Add(x => x.Profile, _profile));

    private static IElement Button(IRenderedComponent<ManagedEdgeConnectionPanel> cut, string label) => cut.FindAll("button").Single(b => b.TextContent.Trim() == label);

    [Fact]
    public void StartEdgeIsDisabledUntilCompatibilityCheckPermitsIt()
    {
        var cut = Panel(new ManagedEdgeRuntime(_api.Object));
        Assert.True(Button(cut, "Start Edge for authenticated testing").HasAttribute("disabled"));
        Assert.False(Button(cut, "Check Edge compatibility").HasAttribute("disabled"));
        Assert.Contains("Not checked", cut.Markup);
        Button(cut, "Check Edge compatibility").Click();
        cut.WaitForAssertion(() => Assert.False(Button(cut, "Start Edge for authenticated testing").HasAttribute("disabled")));
        Assert.Contains("Not configured", cut.Markup);
        Assert.Contains("Not active", cut.Markup);
        Assert.Contains("152.0.4191.66", cut.Markup);
        Assert.Contains("Available", cut.Markup);
    }

    [Fact]
    public void StartEdgeShowsReadinessWithoutClaimingAuthentication()
    {
        var cut = Panel(new ManagedEdgeRuntime(_api.Object));
        Button(cut, "Check Edge compatibility").Click();
        cut.WaitForAssertion(() => Assert.False(Button(cut, "Start Edge for authenticated testing").HasAttribute("disabled")));
        Button(cut, "Start Edge for authenticated testing").Click();
        cut.WaitForAssertion(() => Assert.Contains("Active", cut.Markup));
        var compatibility = cut.Find("[data-testid='edge-compatibility']").TextContent;
        Assert.Contains("Ready", compatibility);
        Assert.Contains("Not opened yet", compatibility);
        Assert.Contains("Edge started by BirkNextYes", compatibility.Replace("\n", "").Replace("  ", ""));
        Assert.Contains("Not verified", cut.Markup);
        Assert.Contains("Required", cut.Find("[data-testid='edge-manual-signin']").TextContent);
        Assert.True(Button(cut, "Start Edge for authenticated testing").HasAttribute("disabled"));
        _api.Verify(a => a.ConnectAsync(It.IsAny<ManagedEdgeConnectRequest>()), Times.Never);
    }

    [Fact]
    public void BlockedPolicyDisablesLaunchAndExplains()
    {
        _api.Setup(a => a.PreflightAsync(It.IsAny<ManagedEdgePreflightRequest>())).ReturnsAsync(_compatible with
        {
            RemoteDebuggingPolicyStatus = EdgeRemoteDebuggingPolicyStatus.Blocked, CanLaunchTestEdge = false, CanConnect = false,
            FailureReason = "Remote debugging is blocked by Microsoft Edge policy (RemoteDebuggingAllowed = 0). BirkNext does not bypass organization policy."
        });
        var cut = Panel(new ManagedEdgeRuntime(_api.Object));
        Button(cut, "Check Edge compatibility").Click();
        cut.WaitForAssertion(() => Assert.Contains("Blocked", cut.Find("[data-testid='edge-compatibility']").TextContent));
        Assert.Contains("blocked by Microsoft Edge policy", cut.Find("[data-testid='edge-preflight-reason']").TextContent);
        Assert.True(Button(cut, "Start Edge for authenticated testing").HasAttribute("disabled"));
    }

    [Fact]
    public void RemoteDeploymentHidesLocalBrowserIntegrationInsteadOfLaunchingOnServer()
    {
        _api.Setup(a => a.PreflightAsync(It.IsAny<ManagedEdgePreflightRequest>())).ReturnsAsync(new ManagedEdgePreflightResult
        {
            LocalBrowserIntegrationAvailable = false, TargetOrigin = "https://m2lbdev.bufetat.no",
            FailureReason = "Local browser integration is unavailable in this deployment mode: BirkNext.Api is not running as a local workstation runtime, so it cannot start or attach to a browser on your PC."
        });
        var cut = Panel(new ManagedEdgeRuntime(_api.Object));
        Button(cut, "Check Edge compatibility").Click();
        cut.WaitForAssertion(() => Assert.Contains("Unavailable in this deployment mode", cut.Markup));
        Assert.True(Button(cut, "Start Edge for authenticated testing").HasAttribute("disabled"));
        Assert.True(Button(cut, "Connect to managed Edge").HasAttribute("disabled"));
    }

    [Fact]
    public void NonInspectableTargetTabIsExplainedAndNeverVerified()
    {
        _api.Setup(a => a.ConnectAsync(It.IsAny<ManagedEdgeConnectRequest>())).ReturnsAsync(new ManagedEdgeStatus
        {
            State = ManagedEdgeState.TargetTabNotInspectable, TargetOrigin = "https://m2lbdev.bufetat.no", DiscoveredTargetTabs = 1,
            Evidence = "The target tab is open, but Edge refused debugger attachment to it."
        });
        var cut = Panel(new ManagedEdgeRuntime(_api.Object));
        Button(cut, "Connect to managed Edge").Click();
        cut.WaitForAssertion(() => Assert.Contains("refuses debugger attachment", cut.Markup));
        Assert.Contains("Not verified", cut.Markup);
        Assert.Contains("cannot be verified through CDP", cut.Find("[data-testid='edge-manual-signin']").TextContent);
        Assert.True(Button(cut, "Verify authenticated access").HasAttribute("disabled"));
    }

    [Fact]
    public async Task CompatibilityAndLaunchNeverTouchProfileAndFollowTargetOrigin()
    {
        var runtime = new ManagedEdgeRuntime(_api.Object);
        var json = JsonSerializer.Serialize(_profile);
        await runtime.CheckCompatibilityAsync(_profile);
        Assert.NotNull(runtime.PreflightFor(_profile));
        await runtime.LaunchEdgeAsync(_profile);
        Assert.True(runtime.PreflightFor(_profile)!.EdgeStarted);
        Assert.Equal(json, JsonSerializer.Serialize(_profile));
        _profile.TargetUrl = "https://other.test/";
        Assert.Null(runtime.PreflightFor(_profile));
        _profile.TargetUrl = "https://M2LBDEV.bufetat.no/some/path";
        Assert.NotNull(runtime.PreflightFor(_profile));
    }

    [Fact]
    public async Task TransportFailureDuringCompatibilityCheckIsReportedWithoutSecrets()
    {
        _api.Setup(a => a.PreflightAsync(It.IsAny<ManagedEdgePreflightRequest>())).ThrowsAsync(new HttpRequestException("secret transport detail"));
        var runtime = new ManagedEdgeRuntime(_api.Object);
        await runtime.CheckCompatibilityAsync(_profile);
        var result = runtime.PreflightFor(_profile);
        Assert.NotNull(result);
        Assert.False(result!.CanLaunchTestEdge);
        Assert.DoesNotContain("secret", result.FailureReason);
        Assert.Contains("did not complete", result.FailureReason);
    }

    [Fact] // S, T, U
    public void CompatibilityCheckAndLaunchKeepSettingsReadOnlyAndDetectionCurrent()
    {
        var settings = new FrontendAnalysisSettingsService();
        Services.AddSingleton<IFrontendAnalysisSettingsService>(settings);
        Services.AddSingleton(new ManagedEdgeRuntime(_api.Object));
        var detector = new Mock<ITargetEnvironmentDetectionApiService>();
        detector.Setup(a => a.DetectFromUrlAsync(It.IsAny<string>(), default)).ReturnsAsync(new TargetEnvironmentDetectionResult
        {
            Success = true, OriginalUrl = _profile.TargetUrl!, Reachability = TargetReachability.Reachable,
            DetectedClientFramework = ClientFrameworkType.BlazorWebAssembly, DetectedAuthenticationType = FrontendAuthenticationType.MicrosoftEntraId,
            DetectedAuthority = "https://login.microsoftonline.com/tenant-id", DetectedTenantId = "tenant-id", DetectedClientId = "client-id",
            ManualAuthenticationVerificationRequired = true, State = DetectionState.ManualAuthenticationVerificationRequired
        });
        Services.AddSingleton(detector.Object);
        JSInterop.Setup<string?>("birkNextStorage.getItem", _ => true).SetResult("""
            {"activeProfileId":"dev","profiles":[{"id":"dev","name":"Dev","targetUrl":"https://m2lbdev.bufetat.no/"}]}
            """);
        var cut = Render<Settings>();
        Click(cut, "Detect settings");
        cut.WaitForAssertion(() => Assert.Contains("Detected from target", cut.Markup));
        Click(cut, "Authentication");
        var persisted = JsonSerializer.Serialize(settings.Settings);
        var discovery = cut.Find("[data-testid='edge-auth-discovery']").TextContent;
        Assert.Contains("https://login.microsoftonline.com/tenant-id", discovery);
        Assert.Contains("client-id", discovery);
        Assert.Contains("Public discovery of the target", discovery);
        foreach (var action in new[] { "Check Edge compatibility", "Start Edge for authenticated testing", "Check Edge compatibility" })
        {
            cut.WaitForAssertion(() => Assert.False(cut.FindAll("button").Single(b => b.TextContent.Trim() == action).HasAttribute("disabled")));
            Click(cut, action);
            cut.WaitForAssertion(() => Assert.False(cut.FindAll("button").Single(b => b.TextContent.Trim() == "Check Edge compatibility").HasAttribute("disabled")));
            Assert.DoesNotContain(cut.FindAll("button"), b => b.TextContent.Trim() is "Save changes" or "Cancel");
            // Settings form stays read-only, and the managed Edge runtime panel carries no editable control at all (no runtime trust opt-in).
            Assert.Empty(cut.FindAll("input, select, textarea"));
            Assert.Equal(persisted, JsonSerializer.Serialize(settings.Settings));
            Assert.DoesNotContain(JSInterop.Invocations, i => i.Identifier == "birkNextStorage.setItem");
            Assert.DoesNotContain("Needs re-check", cut.Markup);
        }
        Assert.Contains("Edge started by BirkNext", cut.Markup);
        Click(cut, "Validation");
        var freshness = cut.FindAll(".fa-dl-row").Single(r => r.TextContent.Contains("Detection freshness")).TextContent;
        Assert.Contains("Current", freshness);
        Assert.DoesNotContain("Stale", freshness);
        Assert.DoesNotContain(cut.FindAll("button"), b => b.TextContent.Trim() is "Save changes" or "Cancel");
    }

    private static void Click(IRenderedComponent<Settings> cut, string label) => cut.FindAll("button").Single(b => b.TextContent.Trim() == label).Click();

    [Fact]
    public void RuntimePanelHasNoTrustOptInAndShowsSavedPolicyReadOnly()
    {
        // ExactOrigin is the saved default for a profile without the field.
        Assert.Equal(ManagedEdgeTrustModel.ExactOrigin, _profile.Authentication.BrowserDeliveryTrust);
        var cut = Panel(new ManagedEdgeRuntime(_api.Object));
        Assert.Empty(cut.FindAll("input, select, textarea"));
        Assert.DoesNotContain("Accept an approved Microsoft Defender for Cloud Apps proxied delivery", cut.Markup);
        Assert.Empty(cut.FindAll("[data-testid='edge-trust-model']"));
        Assert.Contains("Exact origin only", cut.Find("[data-testid='edge-configured-trust']").TextContent);
        Assert.Equal("Not observed", cut.Find("[data-testid='edge-observed-origin']").TextContent);
        Assert.Equal("Not observed", cut.Find("[data-testid='edge-delivery']").TextContent);
        Assert.Equal("Not evaluated", cut.Find("[data-testid='edge-trust-decision']").TextContent);
        // The panel only reflects the saved policy; it does not own it.
        _profile.Authentication.BrowserDeliveryTrust = ManagedEdgeTrustModel.ApprovedMcasProxyOrigin;
        cut.Render();
        Assert.Contains("Approved MCAS proxy permitted", cut.Find("[data-testid='edge-configured-trust']").TextContent);
        Assert.Contains("exact origin still preferred", cut.Find("[data-testid='edge-configured-trust']").TextContent);
        Assert.Empty(cut.FindAll("input, select, textarea"));
    }

    [Theory]
    [InlineData(ManagedEdgeTrustModel.ExactOrigin)]
    [InlineData(ManagedEdgeTrustModel.ApprovedMcasProxyOrigin)]
    public void SavedTrustPolicyFlowsToConnectRequest(ManagedEdgeTrustModel saved)
    {
        ManagedEdgeConnectRequest? captured = null;
        _api.Setup(a => a.ConnectAsync(It.IsAny<ManagedEdgeConnectRequest>()))
            .Callback<ManagedEdgeConnectRequest>(r => captured = r)
            .ReturnsAsync(new ManagedEdgeStatus { SessionId = "s", State = ManagedEdgeState.ConnectedUnproven, OriginMatched = true });
        _profile.Authentication.BrowserDeliveryTrust = saved;
        var json = JsonSerializer.Serialize(_profile);
        var cut = Panel(new ManagedEdgeRuntime(_api.Object));
        Button(cut, "Connect to managed Edge").Click();
        cut.WaitForAssertion(() => Assert.NotNull(captured));
        Assert.Equal(saved, captured!.TrustModel);
        Assert.Equal(json, JsonSerializer.Serialize(_profile));   // connecting never writes back to the profile
    }

    [Fact]
    public void DirectDeliveryUnderProxyPermittedPolicyIsShownAsExactOriginTrusted()
    {
        // The "MCAS removed later" case: saved policy still permits the proxy, but the browser delivers the exact origin directly.
        _profile.Authentication.BrowserDeliveryTrust = ManagedEdgeTrustModel.ApprovedMcasProxyOrigin;
        var direct = new ManagedEdgeStatus
        {
            SessionId = "s", State = ManagedEdgeState.ConnectedAuthenticated, OriginMatched = true,
            TargetOrigin = "https://m2lbdev.bufetat.no", DeliveryOrigin = "https://m2lbdev.bufetat.no",
            TrustModel = ManagedEdgeTrustModel.ApprovedMcasProxyOrigin, TrustDecision = ManagedEdgeTrustDecision.ExactOriginTrusted, RestAvailable = true,
            Evidence = "Expected origin matched; administrator-approved protected GET returned the expected status and content type."
        };
        _api.Setup(a => a.ConnectAsync(It.IsAny<ManagedEdgeConnectRequest>())).ReturnsAsync(direct);
        _api.Setup(a => a.StatusAsync(It.IsAny<ManagedEdgeSessionRequest>(), It.IsAny<bool>())).ReturnsAsync(direct);
        var cut = Panel(new ManagedEdgeRuntime(_api.Object));
        Button(cut, "Connect to managed Edge").Click();
        cut.WaitForAssertion(() => Assert.Equal("Exact origin — trusted", cut.Find("[data-testid='edge-trust-decision']").TextContent));
        Assert.Contains("Approved MCAS proxy permitted", cut.Find("[data-testid='edge-configured-trust']").TextContent);
        Assert.Equal("Direct", cut.Find("[data-testid='edge-delivery']").TextContent);
        Assert.Equal("https://m2lbdev.bufetat.no", cut.Find("[data-testid='edge-observed-origin']").TextContent);
        Assert.Equal("—", cut.Find("[data-testid='edge-correlation']").TextContent);
        Assert.Contains("Verified", cut.Markup);
        Assert.DoesNotContain("access.mcas.ms", cut.Markup);
    }

    [Fact]
    public void ApprovedProxiedDeliveryShowsSeparateTargetAndBrowserOriginAndVerifiedAccess()
    {
        _profile.Authentication.BrowserDeliveryTrust = ManagedEdgeTrustModel.ApprovedMcasProxyOrigin;
        var proxied = new ManagedEdgeStatus
        {
            SessionId = "s", State = ManagedEdgeState.ConnectedAuthenticated, OriginMatched = true,
            TargetOrigin = "https://m2lbdev.bufetat.no", DeliveryOrigin = "https://m2lbdev-bufetat-no.access.mcas.ms",
            TrustModel = ManagedEdgeTrustModel.ApprovedMcasProxyOrigin, TrustDecision = ManagedEdgeTrustDecision.ApprovedProxyTrusted, RestAvailable = true,
            CorrelationEvidence = "Saved policy permits approved MCAS proxy; HTTPS access.mcas.ms delivery; live document origin matches; navigation history includes configured target, Entra authority.",
            Evidence = "Approved proxied delivery matched; administrator-approved protected GET returned the expected status and content type."
        };
        _api.Setup(a => a.ConnectAsync(It.IsAny<ManagedEdgeConnectRequest>())).ReturnsAsync(proxied);
        _api.Setup(a => a.StatusAsync(It.IsAny<ManagedEdgeSessionRequest>(), It.IsAny<bool>())).ReturnsAsync(proxied);
        var cut = Panel(new ManagedEdgeRuntime(_api.Object));
        Button(cut, "Connect to managed Edge").Click();
        cut.WaitForAssertion(() => Assert.Equal("Approved correlated MCAS proxy — trusted", cut.Find("[data-testid='edge-trust-decision']").TextContent));
        var delivery = cut.Find("[data-testid='edge-browser-delivery']").TextContent;
        Assert.Contains("https://m2lbdev.bufetat.no", delivery);                       // configured target unchanged
        Assert.Contains("https://m2lbdev-bufetat-no.access.mcas.ms", delivery);        // observed browser origin
        Assert.Equal("Microsoft Defender for Cloud Apps proxy", cut.Find("[data-testid='edge-delivery']").TextContent);
        Assert.Contains("navigation history includes", cut.Find("[data-testid='edge-correlation']").TextContent);
        Assert.Contains("Verified", cut.Markup);
    }

    [Fact]
    public void UncorrelatedProxiedTabIsExplainedAndNotVerified()
    {
        _profile.Authentication.BrowserDeliveryTrust = ManagedEdgeTrustModel.ApprovedMcasProxyOrigin;
        _api.Setup(a => a.ConnectAsync(It.IsAny<ManagedEdgeConnectRequest>())).ReturnsAsync(new ManagedEdgeStatus
        {
            State = ManagedEdgeState.ProxiedDeliveryUncorrelated, TargetOrigin = "https://m2lbdev.bufetat.no",
            DeliveryOrigin = "https://m2lbdev-bufetat-no.access.mcas.ms", TrustModel = ManagedEdgeTrustModel.ApprovedMcasProxyOrigin,
            TrustDecision = ManagedEdgeTrustDecision.ProxyCorrelationFailed,
            Evidence = "A proxied tab for this application is open, but BirkNext could not correlate it to the configured target through the browser session."
        });
        var cut = Panel(new ManagedEdgeRuntime(_api.Object));
        Button(cut, "Connect to managed Edge").Click();
        cut.WaitForAssertion(() => Assert.Contains("could not correlate", cut.Markup));
        Assert.Equal("Proxy correlation failed", cut.Find("[data-testid='edge-trust-decision']").TextContent);
        Assert.Contains("not correlated to the target", cut.Markup);
        Assert.Contains("Not verified", cut.Markup);
        Assert.True(Button(cut, "Verify authenticated access").HasAttribute("disabled"));
    }

    [Fact]
    public void ExactOriginEnvironmentExplainsProxiedDeliveryAndStaysUnverified()
    {
        // Saved ExactOrigin (default) while the tenant now proxies the application through Defender for Cloud Apps.
        _api.Setup(a => a.ConnectAsync(It.IsAny<ManagedEdgeConnectRequest>())).ReturnsAsync(new ManagedEdgeStatus
        {
            State = ManagedEdgeState.ProxiedDeliveryNotPermitted, TargetOrigin = "https://m2lbdev.bufetat.no",
            DeliveryOrigin = "https://m2lbdev-bufetat-no.access.mcas.ms", TrustModel = ManagedEdgeTrustModel.ExactOrigin,
            TrustDecision = ManagedEdgeTrustDecision.ProxyNotPermitted,
            Evidence = "Browser delivery is proxied by Microsoft Defender for Cloud Apps, but this environment permits exact-origin delivery only."
        });
        var cut = Panel(new ManagedEdgeRuntime(_api.Object));
        Button(cut, "Connect to managed Edge").Click();
        cut.WaitForAssertion(() => Assert.Contains("permits exact-origin delivery only", cut.Markup));
        Assert.Contains("Exact origin only", cut.Find("[data-testid='edge-configured-trust']").TextContent);
        Assert.Equal("Microsoft Defender for Cloud Apps proxy", cut.Find("[data-testid='edge-delivery']").TextContent);
        Assert.Contains("not permitted by this environment", cut.Find("[data-testid='edge-trust-decision']").TextContent);
        Assert.Contains("Blocked", cut.Find("[data-testid='edge-manual-signin']").TextContent);
        Assert.Contains("Not verified", cut.Markup);
        Assert.True(Button(cut, "Verify authenticated access").HasAttribute("disabled"));
        Assert.Empty(cut.FindAll("input, select, textarea"));   // no way to opt in from the runtime panel
    }
}
