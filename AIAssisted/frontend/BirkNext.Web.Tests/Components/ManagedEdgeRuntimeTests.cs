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

public sealed class ManagedEdgeRuntimeTests : BunitContext
{
    private readonly Mock<IManagedEdgeCdpApiService> _api = new();
    private readonly FrontendAnalysisProfile _profile = new() { Id = "dev", TargetUrl = "https://m2lbdev.bufetat.no/" };
    private readonly ManagedEdgeStatus _connected = new() { SessionId = "runtime-only", State = ManagedEdgeState.ConnectedUnproven, OriginMatched = true, TargetOrigin = "https://m2lbdev.bufetat.no" };
    public ManagedEdgeRuntimeTests()
    {
        _api.Setup(a => a.ConnectAsync(It.IsAny<ManagedEdgeConnectRequest>())).ReturnsAsync(_connected);
        _api.Setup(a => a.StatusAsync(It.IsAny<ManagedEdgeSessionRequest>(), false)).ReturnsAsync(_connected);
        _api.Setup(a => a.StatusAsync(It.IsAny<ManagedEdgeSessionRequest>(), true)).ReturnsAsync(_connected with { State = ManagedEdgeState.ConnectedAuthenticated });
        _api.Setup(a => a.DisconnectAsync(It.IsAny<ManagedEdgeSessionRequest>())).Returns(Task.CompletedTask);
    }

    [Fact]
    public async Task ConnectionAndVerificationNeverChangeSerialization()
    {
        await using var runtime = new ManagedEdgeRuntime(_api.Object);
        var json = JsonSerializer.Serialize(_profile);
        await runtime.ConnectAsync(_profile);
        Assert.False(runtime.Status.AuthenticatedBrowserAvailable);
        await runtime.VerifyAsync();
        Assert.True(runtime.Status.AuthenticatedBrowserAvailable);
        Assert.Equal(json, JsonSerializer.Serialize(_profile));
        await runtime.DisconnectAsync();
        Assert.Equal(json, JsonSerializer.Serialize(_profile));
    }

    [Theory]
    [InlineData("profile")]
    [InlineData("url")]
    [InlineData("auth")]
    public async Task IdentityChangeImmediatelyStalesAndDisconnects(string change)
    {
        await using var runtime = new ManagedEdgeRuntime(_api.Object);
        await runtime.ConnectAsync(_profile);
        await runtime.VerifyAsync();
        if (change == "profile") _profile.Id = "qa";
        if (change == "url") _profile.TargetUrl = "https://other.test";
        if (change == "auth") _profile.Authentication.ExpectedTenant = "changed";
        Assert.Equal(ManagedEdgeState.Stale, runtime.For(_profile).State);
        Assert.False(runtime.For(_profile).AuthenticatedBrowserAvailable);
        await runtime.SynchronizeAsync(_profile);
        _api.Verify(a => a.DisconnectAsync(It.IsAny<ManagedEdgeSessionRequest>()), Times.Once);
    }

    [Fact]
    public async Task DisconnectAndTransportFailureRevokeAvailability()
    {
        await using var runtime = new ManagedEdgeRuntime(_api.Object);
        await runtime.ConnectAsync(_profile);
        await runtime.VerifyAsync();
        _api.Setup(a => a.StatusAsync(It.IsAny<ManagedEdgeSessionRequest>(), false)).ThrowsAsync(new HttpRequestException("secret"));
        await runtime.RefreshAsync();
        Assert.Equal(ManagedEdgeState.Stale, runtime.Status.State);
        Assert.False(runtime.Status.SecurityBrowserAvailable);
        Assert.DoesNotContain("secret", runtime.Status.Evidence);
        await runtime.DisconnectAsync();
        Assert.Equal(ManagedEdgeState.NotConnected, runtime.Status.State);
    }

    [Fact]
    public async Task LateConnectionCannotRestorePreviousEnvironment()
    {
        var pending = new TaskCompletionSource<ManagedEdgeStatus>();
        _api.Setup(a => a.ConnectAsync(It.IsAny<ManagedEdgeConnectRequest>())).Returns(pending.Task);
        await using var runtime = new ManagedEdgeRuntime(_api.Object);
        var task = runtime.ConnectAsync(_profile);
        _profile.Id = "qa";
        await runtime.SynchronizeAsync(_profile);
        pending.SetResult(_connected);
        await task;
        Assert.Equal(ManagedEdgeState.Stale, runtime.Status.State);
        _api.Verify(a => a.DisconnectAsync(It.IsAny<ManagedEdgeSessionRequest>()), Times.Once);
    }

    [Fact]
    public void DetectConnectVerifyDisconnectKeepSettingsReadOnlyAndDetectionCurrent()
    {
        var settings = new FrontendAnalysisSettingsService();
        Services.AddSingleton<IFrontendAnalysisSettingsService>(settings);
        Services.AddSingleton(new ManagedEdgeRuntime(_api.Object));
        var detector = new Mock<ITargetEnvironmentDetectionApiService>();
        detector.Setup(a => a.DetectFromUrlAsync(It.IsAny<string>(), default)).ReturnsAsync(new TargetEnvironmentDetectionResult
        {
            Success = true, OriginalUrl = _profile.TargetUrl!, Reachability = TargetReachability.Reachable,
            DetectedClientFramework = ClientFrameworkType.BlazorWebAssembly,
            ManualAuthenticationVerificationRequired = true, State = DetectionState.ManualAuthenticationVerificationRequired
        });
        Services.AddSingleton(detector.Object);
        JSInterop.Setup<string?>("birkNextStorage.getItem", _ => true).SetResult("""
            {"activeProfileId":"dev","profiles":[{"id":"dev","name":"Dev","targetUrl":"https://m2lbdev.bufetat.no/"}]}
            """);
        var cut = Render<Component>();
        Click(cut, "Detect settings");
        cut.WaitForAssertion(() => Assert.Contains("Detected from target", cut.Markup));
        Click(cut, "Authentication");
        var persisted = JsonSerializer.Serialize(settings.Settings);
        foreach (var action in new[] { "Connect to managed Edge", "Verify authenticated access", "Disconnect BirkNext from Edge" })
        {
            Click(cut, action);
            cut.WaitForAssertion(() => Assert.DoesNotContain(cut.FindAll("button"), b => b.TextContent.Trim() is "Save changes" or "Cancel"));
            // Settings form stays read-only; the managed Edge runtime panel may carry its own controls (e.g. the proxy-trust opt-in).
            Assert.Empty(cut.FindAll("input, select, textarea").Where(e => e.Closest("[data-testid=managed-edge-panel]") is null));
            Assert.Equal(persisted, JsonSerializer.Serialize(settings.Settings));
            Assert.DoesNotContain(JSInterop.Invocations, i => i.Identifier == "birkNextStorage.setItem");
            Assert.DoesNotContain("Needs re-check", cut.Markup);
        }
    }

    private static void Click(IRenderedComponent<Component> cut, string label) => cut.FindAll("button").Single(b => b.TextContent.Trim() == label).Click();
}
