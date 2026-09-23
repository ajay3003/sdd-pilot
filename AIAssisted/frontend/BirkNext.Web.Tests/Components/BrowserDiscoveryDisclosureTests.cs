using BirkNext.BrowserCompanion;
using BirkNext.Web.Components;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace BirkNext.Web.Tests.Components;

public sealed class BrowserDiscoveryDisclosureTests : BunitContext
{
    private const string Toggle = "[data-testid=browser-discovery-companion-setup-toggle]";
    private const string Body = "[data-testid=browser-discovery-companion-setup-body]";
    private readonly FrontendAnalysisProfile _profile = new() { Id = "dev", Name = "Dev", TargetUrl = "https://app.test" };
    public BrowserDiscoveryDisclosureTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<IEndpointDiscoveryService, EndpointDiscoveryService>();
    }
    private BrowserCompanionRuntime Runtime(Api api) => new(api, Services.GetRequiredService<IEndpointDiscoveryService>(), Services.GetRequiredService<IJSRuntime>());
    private IRenderedComponent<BrowserDiscoveryTab> Open(BrowserCompanionRuntime runtime) => Render<BrowserDiscoveryTab>(p => p.Add(c => c.Profile, _profile).Add(c => c.Runtime, runtime));
    private static void AssertExpanded(IRenderedComponent<BrowserDiscoveryTab> cut, bool expanded)
    {
        Assert.Equal(expanded ? "true" : "false", cut.Find(Toggle).GetAttribute("aria-expanded"));
        Assert.Equal(!expanded, cut.Find(Body).HasAttribute("hidden"));
    }
    [Fact]
    public async Task UserChoiceSurvivesRepeatedRuntimePollsAndParentParameterUpdatesWithoutRemount()
    {
        var api = new Api();
        await using var runtime = Runtime(api);
        var cut = Open(runtime);
        AssertExpanded(cut, false);
        cut.Find(Toggle).Click();
        AssertExpanded(cut, true);
        var bodyId = cut.Find(Body).Id;
        var panel = cut.FindComponent<BrowserCompanionPanel>().Instance;
        for (var i = 0; i < 4; i++)
        {
            await cut.InvokeAsync(runtime.RefreshAsync); // Same completion/event path as PollAsync.
            cut.Render(p => p.Add(c => c.Profile, new FrontendAnalysisProfile { Id = "dev", TargetUrl = "https://app.test" }));
            AssertExpanded(cut, true);
            Assert.Equal(bodyId, cut.Find(Body).Id);
            Assert.Same(panel, cut.FindComponent<BrowserCompanionPanel>().Instance);
        }
        cut.Find(Toggle).Click();
        await cut.InvokeAsync(runtime.RefreshAsync);
        AssertExpanded(cut, false);
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ConnectionPageDomAndHistoryChangesDoNotOverwriteUserChoice(bool expanded)
    {
        var api = new Api { Status = new() { ProfileId = "dev", State = BrowserCompanionState.Disconnected } };
        await using var runtime = Runtime(api);
        var cut = Open(runtime);
        AssertExpanded(cut, true); // Existing recovery/onboarding default.
        cut.Find(Toggle).Click();
        if (expanded) cut.Find(Toggle).Click();
        foreach (var state in new[] { BrowserCompanionState.Connected, BrowserCompanionState.Disconnected, BrowserCompanionState.NotPaired, BrowserCompanionState.Connected })
        {
            api.Status = new()
            {
                ProfileId = "dev", State = state, CurrentPageOrigin = "https://app.test", CurrentPagePath = "/changed",
                Live = new()
                {
                    ExtensionConnected = state == BrowserCompanionState.Connected,
                    LivePages = state == BrowserCompanionState.Connected
                        ? [new() { PageId = "one", Origin = "https://app.test", Route = "/changed", ContentScriptInstanceId = "script-one" },
                           new() { PageId = "two", Origin = "https://app.test", Route = "/other", ContentScriptInstanceId = "script-two" }]
                        : []
                },
                Pages = [new() { PageOrigin = "https://app.test", PagePath = "/changed", CapturedAt = DateTimeOffset.UtcNow, Dom = new() { NodeCount = 42 } }],
                Evidence = new() { PagesWithEvidence = 1, DomEvidencePageCount = 1, LastEvidenceAt = DateTimeOffset.UtcNow }
            };
            await cut.InvokeAsync(runtime.RefreshAsync);
            AssertExpanded(cut, expanded);
        }
        Assert.Contains("1", cut.Find("[data-testid=bd-pages-count]").TextContent);
    }
    [Fact]
    public async Task InFlightResponseCannotOverwriteAnExplicitChoice()
    {
        var api = new Api();
        await using var runtime = Runtime(api);
        var cut = Open(runtime);
        api.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var refresh = cut.InvokeAsync(runtime.RefreshAsync);
        cut.Find(Toggle).Click();
        api.Pending.SetResult(new() { ProfileId = "dev", State = BrowserCompanionState.Connected });
        await refresh;
        AssertExpanded(cut, true);
    }
    [Fact]
    public async Task UserCanCloseWhileInitialStatusIsInFlightWithoutRecoveryDefaultReopening()
    {
        var api = new Api { Pending = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var runtime = Runtime(api);
        var cut = Open(runtime);
        cut.Find(Toggle).Click();
        cut.Find(Toggle).Click();
        api.Pending.SetResult(new() { ProfileId = "dev", State = BrowserCompanionState.Disconnected });
        cut.WaitForAssertion(() => Assert.Contains("Paired", cut.Find("[data-testid=bd-session]").TextContent));
        AssertExpanded(cut, false);
    }
    [Fact]
    public async Task RealTargetChangeResetsToItsInitialDefault()
    {
        var api = new Api();
        await using var runtime = Runtime(api);
        var cut = Open(runtime);
        cut.Find(Toggle).Click();
        api.Status = new() { ProfileId = "qa", State = BrowserCompanionState.NotPaired };
        cut.Render(p => p.Add(c => c.Profile, new FrontendAnalysisProfile { Id = "qa", TargetUrl = "https://qa.test" }));
        AssertExpanded(cut, false);
        cut.Find(Toggle).Click();
        await cut.InvokeAsync(runtime.RefreshAsync);
        AssertExpanded(cut, true);
    }
    private sealed class Api : IBrowserCompanionApiService
    {
        public BrowserCompanionStatus Status = new() { ProfileId = "dev" };
        public TaskCompletionSource<BrowserCompanionStatus>? Pending;
        public Task<BrowserCompanionStatus> StatusAsync(string profileId, CancellationToken cancellationToken = default) => Pending?.Task ?? Task.FromResult(Status);
        public Task<BrowserCompanionPairingChallenge> StartPairingAsync(BrowserCompanionPairingStartRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BrowserCompanionStatus> UnpairAsync(string profileId, CancellationToken cancellationToken = default) => Task.FromResult(Status);
    }
}
