using BirkNext.BrowserCompanion;
using BirkNext.Web.Components;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Moq;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// Navigation is immediate, data readiness follows: switching to Browser Discovery paints a loading state before the stored evidence store is
/// read and classified (that synchronous pass froze the tab for seconds with a large store), then the stored-evidence summary, then the live
/// session. Re-visiting does not reclassify an unchanged store.
/// </summary>
public sealed class BrowserDiscoveryLoadingTests : BunitContext
{
    private const string Origin = "https://application.example.test";
    private readonly FrontendAnalysisProfile _profile = new() { Id = "dev", Name = "Dev", TargetUrl = Origin };

    public BrowserDiscoveryLoadingTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<IEndpointDiscoveryService, EndpointDiscoveryService>();
        Services.AddSingleton<IFrontendAnalysisSettingsService, FrontendAnalysisSettingsService>();
        Services.AddSingleton(Mock.Of<ITargetEnvironmentDetectionApiService>());
    }

    [Fact]
    public void The_tab_paints_a_loading_state_first_then_the_summary_without_waiting_for_evidence()
    {
        var storage = JSInterop.Setup<string?>("birkNextStorage.getDiscovery"); // pending: the stored evidence is still being read
        var status = new TaskCompletionSource<BrowserCompanionStatus>();
        var api = new Mock<IBrowserCompanionApiService>();
        api.Setup(a => a.StatusAsync("dev", It.IsAny<CancellationToken>())).Returns(status.Task);
        var runtime = new BrowserCompanionRuntime(api.Object, Services.GetRequiredService<IEndpointDiscoveryService>(), Services.GetRequiredService<IJSRuntime>());

        var cut = Render<BrowserDiscoveryTab>(p => p.Add(c => c.Profile, _profile).Add(c => c.Runtime, runtime));

        cut.WaitForAssertion(() => cut.Find("[data-testid=bd-loading]").TextContent.Should().Contain("Loading browser evidence…")
            .And.Contain("Loading captured pages").And.Contain("Loading live session state").And.Contain("Loading historical evidence"));
        cut.Find("[data-testid=browser-discovery]").GetAttribute("aria-busy").Should().Be("true");

        storage.SetResult(null);
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=browser-discovery-summary]").Should().ContainSingle("stored evidence is summarised before the live status arrives"));
        cut.Find("[data-testid=bd-live-loading]").TextContent.Should().Be("Loading live session state…");
        cut.Find("[data-testid=browser-discovery]").GetAttribute("data-loaded").Should().Be("false");

        status.SetResult(new BrowserCompanionStatus { ProfileId = "dev", State = BrowserCompanionState.NotPaired });
        cut.WaitForAssertion(() => cut.Find("[data-testid=browser-discovery]").GetAttribute("data-loaded").Should().Be("true"));
        cut.FindAll("[data-testid=bd-live-loading]").Should().BeEmpty();
        runtime.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    [Fact]
    public void Configuring_the_same_target_again_does_not_reclassify_an_unchanged_store()
    {
        var discovery = new EndpointDiscoveryService();
        var snapshot = discovery.GetSnapshot("dev");
        discovery.ConfigureTarget(_profile);
        var version = EndpointDiscoveryService.Version(snapshot);
        snapshot.ApplicationOrigins = ["https://sentinel.example.test"]; // would be overwritten if ConfigureTarget ran again

        discovery.ConfigureTarget(_profile);
        snapshot.ApplicationOrigins.Should().Equal(["https://sentinel.example.test"], "an unchanged store with unchanged origins is not reclassified on every visit");
        EndpointDiscoveryService.Version(snapshot).Should().Be(version);

        discovery.ConfigureTarget(new FrontendAnalysisProfile { Id = "dev", Name = "Dev", TargetUrl = "https://other.example.test" });
        snapshot.ApplicationOrigins.Should().Equal(["https://other.example.test"], "changed origins reclassify");
    }
}
