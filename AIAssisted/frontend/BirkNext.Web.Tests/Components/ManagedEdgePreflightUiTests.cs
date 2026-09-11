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
            // Settings form stays read-only; the managed Edge runtime panel may carry its own controls (e.g. the proxy-trust opt-in).
            Assert.Empty(cut.FindAll("input, select, textarea").Where(e => e.Closest("[data-testid=managed-edge-panel]") is null));
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
    public void ProxyTrustIsOptInAndDefaultsToExactOrigin()
    {
        var runtime = new ManagedEdgeRuntime(_api.Object);
        Assert.Equal(ManagedEdgeTrustModel.ExactOrigin, runtime.TrustModel);
        var cut = Panel(runtime);
        Assert.Contains("Exact origin required", cut.Markup);
        var checkbox = cut.Find("[data-testid='edge-trust-model'] input[type=checkbox]");
        Assert.False(checkbox.HasAttribute("checked"));
        checkbox.Change(true);
        Assert.Equal(ManagedEdgeTrustModel.ApprovedMcasProxyOrigin, runtime.TrustModel);
        cut.WaitForAssertion(() => Assert.Contains("Approved MCAS proxied delivery", cut.Markup));
        checkbox.Change(false);
        Assert.Equal(ManagedEdgeTrustModel.ExactOrigin, runtime.TrustModel);
    }

    [Fact]
    public void ProxyTrustSelectionFlowsToConnectRequest()
    {
        ManagedEdgeConnectRequest? captured = null;
        _api.Setup(a => a.ConnectAsync(It.IsAny<ManagedEdgeConnectRequest>()))
            .Callback<ManagedEdgeConnectRequest>(r => captured = r)
            .ReturnsAsync(new ManagedEdgeStatus { SessionId = "s", State = ManagedEdgeState.ConnectedUnproven, OriginMatched = true });
        var runtime = new ManagedEdgeRuntime(_api.Object);
        var cut = Panel(runtime);
        cut.Find("[data-testid='edge-trust-model'] input[type=checkbox]").Change(true);
        Button(cut, "Connect to managed Edge").Click();
        cut.WaitForAssertion(() => Assert.NotNull(captured));
        Assert.Equal(ManagedEdgeTrustModel.ApprovedMcasProxyOrigin, captured!.TrustModel);
    }

    [Fact]
    public void ApprovedProxiedDeliveryShowsSeparateTargetAndBrowserOriginAndVerifiedAccess()
    {
        _api.Setup(a => a.ConnectAsync(It.IsAny<ManagedEdgeConnectRequest>())).ReturnsAsync(new ManagedEdgeStatus
        {
            SessionId = "s", State = ManagedEdgeState.ConnectedAuthenticated, OriginMatched = true,
            TargetOrigin = "https://m2lbdev.bufetat.no", DeliveryOrigin = "https://m2lbdev-bufetat-no.access.mcas.ms",
            TrustModel = ManagedEdgeTrustModel.ApprovedMcasProxyOrigin, RestAvailable = true,
            CorrelationEvidence = "User opted in; HTTPS access.mcas.ms delivery; live document origin matches; navigation history includes configured target, Entra authority.",
            Evidence = "Approved proxied delivery matched; administrator-approved protected GET returned the expected status and content type."
        });
        _api.Setup(a => a.StatusAsync(It.IsAny<ManagedEdgeSessionRequest>(), It.IsAny<bool>())).ReturnsAsync(new ManagedEdgeStatus
        {
            SessionId = "s", State = ManagedEdgeState.ConnectedAuthenticated, OriginMatched = true,
            TargetOrigin = "https://m2lbdev.bufetat.no", DeliveryOrigin = "https://m2lbdev-bufetat-no.access.mcas.ms",
            TrustModel = ManagedEdgeTrustModel.ApprovedMcasProxyOrigin, RestAvailable = true
        });
        var runtime = new ManagedEdgeRuntime(_api.Object) { TrustModel = ManagedEdgeTrustModel.ApprovedMcasProxyOrigin };
        var cut = Panel(runtime);
        Button(cut, "Connect to managed Edge").Click();
        cut.WaitForAssertion(() => Assert.Contains("Approved MCAS proxied delivery", cut.Find("[data-testid='edge-correlation']").TextContent));
        var connection = cut.Markup;
        Assert.Contains("m2lbdev.bufetat.no", connection);
        Assert.Contains("m2lbdev-bufetat-no.access.mcas.ms", connection);
        Assert.Contains("Microsoft Defender for Cloud Apps proxy", connection);
        Assert.Contains("Verified", connection);
    }

    [Fact]
    public void UncorrelatedProxiedTabIsExplainedAndNotVerified()
    {
        _api.Setup(a => a.ConnectAsync(It.IsAny<ManagedEdgeConnectRequest>())).ReturnsAsync(new ManagedEdgeStatus
        {
            State = ManagedEdgeState.ProxiedDeliveryUncorrelated, TargetOrigin = "https://m2lbdev.bufetat.no",
            DeliveryOrigin = "https://m2lbdev-bufetat-no.access.mcas.ms", TrustModel = ManagedEdgeTrustModel.ApprovedMcasProxyOrigin,
            Evidence = "A proxied tab for this application is open, but BirkNext could not correlate it to the configured target through the browser session."
        });
        var runtime = new ManagedEdgeRuntime(_api.Object) { TrustModel = ManagedEdgeTrustModel.ApprovedMcasProxyOrigin };
        var cut = Panel(runtime);
        Button(cut, "Connect to managed Edge").Click();
        cut.WaitForAssertion(() => Assert.Contains("could not correlate", cut.Markup));
        Assert.Contains("not correlated to the target", cut.Markup);
        Assert.Contains("Not verified", cut.Markup);
        Assert.True(Button(cut, "Verify authenticated access").HasAttribute("disabled"));
    }
}
