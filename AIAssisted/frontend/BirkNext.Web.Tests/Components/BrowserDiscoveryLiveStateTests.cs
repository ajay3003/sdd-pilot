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
/// Browser Discovery answers two separate questions: what is live in the browser right now, and what evidence we have
/// captured before. They used to share one strip, and that is how "Connected · 4 pages with evidence · DOM available"
/// could describe a browser with nothing open.
///
/// These tests assert the WORDING, because the wording is the contract. "DOM Available" next to a closed browser is
/// wrong however it is laid out.
/// </summary>
public sealed class BrowserDiscoveryLiveStateTests : BunitContext
{
    private const string Origin = "https://application.example.test";
    private readonly FrontendAnalysisProfile _profile = new() { Id = "dev", Name = "Dev", TargetUrl = Origin };
    private IEndpointDiscoveryService Discovery => Services.GetRequiredService<IEndpointDiscoveryService>();

    public BrowserDiscoveryLiveStateTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<IEndpointDiscoveryService, EndpointDiscoveryService>();
        Services.AddSingleton<IFrontendAnalysisSettingsService, FrontendAnalysisSettingsService>();
        Services.AddSingleton(Mock.Of<ITargetEnvironmentDetectionApiService>());
        JSInterop.Setup<string?>("birkNextStorage.getItem", _ => true).SetResult("""
            {"activeProfileId":"dev","profiles":[{"id":"dev","name":"Dev","targetUrl":"https://application.example.test"}]}
            """);
    }

    private static readonly DateTimeOffset Captured = new(2026, 9, 21, 10, 9, 53, TimeSpan.Zero);

    /// <summary>Historical evidence for one page. Deliberately never accompanied by a live page unless a test adds one.</summary>
    private void SeedEvidence(string path = "/saker") => Discovery.GetSnapshot("dev").Pages.Add(new()
    {
        PageOrigin = Origin, PagePath = path,
        BrowserEvidence = new()
        {
            PageOrigin = Origin, PagePath = path, CapturedAt = Captured,
            Dom = new BrowserDomSummary { NodeCount = 120 },
            Accessibility = new BrowserAccessibilitySummary { RulesEvaluated = 9 },
            Performance = new BrowserPerformanceSummary { LcpMs = 900 },
        },
    });

    private sealed class StubRuntime(BrowserCompanionStatus status) : IBrowserCompanionApiService
    {
        public Task<BrowserCompanionPairingChallenge> StartPairingAsync(BrowserCompanionPairingStartRequest r, CancellationToken ct = default) => Task.FromResult(new BrowserCompanionPairingChallenge());
        public Task<BrowserCompanionStatus> StatusAsync(string profileId, CancellationToken ct = default) => Task.FromResult(status);
        public Task<BrowserCompanionStatus> UnpairAsync(string profileId, CancellationToken ct = default) => Task.FromResult(new BrowserCompanionStatus());
    }

    private static BrowserCompanionStatus Status(BrowserCompanionState state = BrowserCompanionState.Connected, params (string Route, string Instance)[] livePages) => new()
    {
        State = state, ProfileId = "dev", ApprovedOrigins = [Origin],
        Live = new BrowserCompanionLiveSession
        {
            ProfileId = "dev",
            ExtensionConnected = state == BrowserCompanionState.Connected,
            LivePages = livePages.Select(p => new BrowserCompanionLivePage
            {
                PageId = $"t-{p.Instance}", Origin = Origin, Route = p.Route, ContentScriptInstanceId = p.Instance,
                RegisteredAt = Captured, LastSeenAt = Captured,
            }).ToList(),
        },
    };

    private async Task<IRenderedComponent<BrowserDiscoveryTab>> OpenAsync(BrowserCompanionStatus status)
    {
        var runtime = new BrowserCompanionRuntime(new StubRuntime(status));
        await runtime.FollowAsync(_profile);
        return Render<BrowserDiscoveryTab>(p => p.Add(c => c.Profile, _profile).Add(c => c.Runtime, runtime));
    }

    private static string Text(IRenderedComponent<BrowserDiscoveryTab> cut, string testId) =>
        cut.Find($"[data-testid={testId}]").TextContent.Trim();

    [Fact]
    public async Task ConnectedWithEvidenceAndNothingOpenNeverClaimsAnythingIsAvailableNow()
    {
        SeedEvidence();
        var cut = await OpenAsync(Status());

        Text(cut, "bd-live-session").Should().Be("Connected");
        Text(cut, "bd-live-pages").Should().Be("0");
        Text(cut, "bd-current-page").Should().Be("None", "the newest evidence route is not the current page");
        Text(cut, "bd-content-script").Should().Be("Not available");
        Text(cut, "bd-live-dom").Should().Be("Not available");

        // The evidence is still there and still says so — in its own section, in past tense.
        Text(cut, "bd-evidence-pages").Should().Be("1");
        Text(cut, "bd-last-evidence").Should().Be(Captured.ToLocalTime().ToString("HH:mm:ss"));
        Text(cut, "bd-evidence-dom").Should().Contain("captured");
    }

    [Fact]
    public async Task NoEvidenceLabelReadsAsSomethingBeingAvailableRightNow()
    {
        SeedEvidence();
        var cut = await OpenAsync(Status());

        foreach (var testId in new[] { "bd-evidence-dom", "bd-evidence-accessibility", "bd-evidence-performance" })
        {
            var value = Text(cut, testId);
            value.Should().Contain("captured");
            value.Should().NotBe("Available", "\"Available\" on its own reads as available now");
        }
        // And the labels themselves say evidence, so a reader skimming the column never sees a bare "DOM".
        cut.Find("[data-testid=browser-discovery-summary]").TextContent
            .Should().Contain("DOM evidence").And.Contain("Historical evidence");
    }

    [Fact]
    public async Task APageThatJustOpenedIsLiveWithNoEvidenceAtAll()
    {
        var cut = await OpenAsync(Status(livePages: ("/admin/operations", "abc")));

        Text(cut, "bd-live-pages").Should().Be("1");
        Text(cut, "bd-current-page").Should().Be($"{Origin}/admin/operations");
        Text(cut, "bd-content-script").Should().Be("Live");
        Text(cut, "bd-live-dom").Should().Be("Available now");
        Text(cut, "bd-evidence-pages").Should().Be("0", "this is a valid state, not a broken one");
        Text(cut, "bd-last-evidence").Should().Be("None");
    }

    [Fact]
    public async Task TheLiveRouteWinsOverTheEvidenceRoute()
    {
        SeedEvidence("/saker");
        var cut = await OpenAsync(Status(livePages: ("/arkiv", "abc")));

        Text(cut, "bd-current-page").Should().EndWith("/arkiv");
        Text(cut, "bd-evidence-pages").Should().Be("1", "the other page's evidence is still listed");
    }

    [Fact]
    public async Task SeveralOpenPagesAreSaidToBeSeveralRatherThanOneOfThem()
    {
        var cut = await OpenAsync(Status(livePages: [("/saker", "abc"), ("/arkiv", "def")]));

        Text(cut, "bd-live-pages").Should().Be("2");
        Text(cut, "bd-current-page").Should().Be("2 pages open");
    }

    [Fact]
    public async Task ADisconnectedCompanionHasNoLiveFactsLeftToReport()
    {
        SeedEvidence();
        var cut = await OpenAsync(Status(BrowserCompanionState.Disconnected));

        Text(cut, "bd-live-pages").Should().Be("0");
        Text(cut, "bd-current-page").Should().Be("None");
        Text(cut, "bd-live-dom").Should().Be("Not available");
        Text(cut, "bd-evidence-pages").Should().Be("1", "the history is not a casualty of the browser going away");
    }

    [Fact]
    public async Task ThePagesTabSaysWhichRowsAreOpenAndWhichAreOnlyHistory()
    {
        SeedEvidence("/saker");
        var cut = await OpenAsync(Status(livePages: ("/arkiv", "abc")));
        cut.Find("[data-testid=browser-discovery-nav-pages]").Click();

        var rows = cut.FindAll("[data-testid=browser-discovery-page-liveness]").Select(r => r.TextContent.Trim()).ToList();
        rows.Should().Contain("Historical evidence only", "a page with evidence is not a page that is open");
        // The open page has produced nothing yet, and would be invisible in an evidence-only list.
        rows.Should().Contain(r => r.StartsWith("Live now"));
    }

    [Fact]
    public async Task TheTwoSectionsAreLabelledSoNeitherCanBeReadAsTheOther()
    {
        SeedEvidence();
        var cut = await OpenAsync(Status());

        cut.Find("[data-testid=browser-discovery-live]").TextContent.Should().Contain("Live session");
        cut.Find("[data-testid=browser-discovery-summary]").TextContent.Should().Contain("Historical evidence");
        // "Pages with evidence" belongs to the historical group only.
        cut.Find("[data-testid=browser-discovery-live]").TextContent.Should().NotContain("Pages with evidence");
    }
}
