using BirkNext.BrowserCompanion;
using BirkNext.Web.Components;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// The final Browser Discovery pass: timestamps that cannot be misread across days, source and rule shown from their own
/// model fields, missing performance values that never become zero, and pairing copy that names what is really paired.
/// </summary>
public sealed class BrowserDiscoveryFinalPolishTests : BunitContext
{
    private const string Origin = "https://application.example.test";
    private readonly FrontendAnalysisProfile _profile = new() { Id = "dev", Name = "Dev", TargetUrl = Origin };
    private IEndpointDiscoveryService Discovery => Services.GetRequiredService<IEndpointDiscoveryService>();

    public BrowserDiscoveryFinalPolishTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<IEndpointDiscoveryService, EndpointDiscoveryService>();
    }

    private sealed class Api : IBrowserCompanionApiService
    {
        public BrowserCompanionStatus Status { get; set; } = new() { ProfileId = "dev", State = BrowserCompanionState.NotPaired };
        public Task<BrowserCompanionPairingChallenge> StartPairingAsync(BrowserCompanionPairingStartRequest r, CancellationToken ct = default) => Task.FromResult(new BrowserCompanionPairingChallenge());
        public Task<BrowserCompanionStatus> StatusAsync(string profileId, CancellationToken ct = default) => Task.FromResult(Status);
        public Task<BrowserCompanionStatus> UnpairAsync(string profileId, CancellationToken ct = default) => Task.FromResult(new BrowserCompanionStatus());
    }

    private void Seed(string path, DateTimeOffset captured, BrowserPerformanceSummary? performance = null, BrowserAccessibilitySummary? accessibility = null) =>
        Discovery.GetSnapshot("dev").Pages.Add(new()
        {
            PageOrigin = Origin, PagePath = path,
            BrowserEvidence = new()
            {
                PageOrigin = Origin, PagePath = path, CapturedAt = captured,
                Dom = new BrowserDomSummary { NodeCount = 120, MaxDepth = 20, IframeCount = 0, DialogCount = 2 },
                Performance = performance ?? new BrowserPerformanceSummary { ObservationType = "spa-navigation", StabilizationMs = 802 },
                Accessibility = accessibility,
            },
        });

    private async Task<(IRenderedComponent<BrowserDiscoveryTab> Cut, BrowserCompanionRuntime Runtime)> OpenAsync(BrowserCompanionState state = BrowserCompanionState.NotPaired)
    {
        var api = new Api { Status = new() { ProfileId = "dev", State = state, ApprovedOrigins = [Origin], Live = new() { ProfileId = "dev", ExtensionConnected = state == BrowserCompanionState.Connected } } };
        var runtime = new BrowserCompanionRuntime(api, Discovery, Services.GetRequiredService<IJSRuntime>());
        await runtime.FollowAsync(_profile);
        return (Render<BrowserDiscoveryTab>(p => p.Add(c => c.Profile, _profile).Add(c => c.Runtime, runtime)), runtime);
    }

    private static string Text(IRenderedComponent<BrowserDiscoveryTab> cut, string testId) => cut.Find($"[data-testid={testId}]").TextContent.Trim();

    // §54 Same-day evidence: time only. Another day: date and time.
    [Fact]
    public void TimestampsShowTheDateWheneverTheEvidenceIsNotFromToday()
    {
        var now = new DateTimeOffset(2026, 9, 23, 13, 40, 0, TimeSpan.FromHours(2));
        var today = now.AddHours(-2);
        var yesterday = new DateTimeOffset(2026, 9, 22, 11, 54, 31, TimeSpan.FromHours(2));

        BrowserDiscoveryPresentation.EvidenceTimestamp(today, now).Should().Be(today.ToLocalTime().ToString("HH:mm:ss"));
        var earlier = BrowserDiscoveryPresentation.EvidenceTimestamp(yesterday, now);
        earlier.Should().Be(yesterday.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture));
        earlier.Should().Contain("2026");
    }

    [Fact]
    public async Task EveryHistoricalTimestampOnTheSurfaceCarriesTheDateForOlderEvidence()
    {
        var old = new DateTimeOffset(2026, 9, 2, 11, 54, 31, TimeSpan.Zero);
        Seed("/admin/roles", old);
        var (cut, _) = await OpenAsync();
        var expected = BrowserDiscoveryPresentation.EvidenceTimestamp(old, DateTimeOffset.Now);
        expected.Should().Contain(".2026 ", "fixture evidence is from an earlier day");

        Text(cut, "bd-last-evidence").Should().Be(expected);
        cut.Find("[data-testid=browser-discovery-page-row]").TextContent.Should().Contain(expected);
        cut.Find("[data-testid=browser-discovery-nav-pages]").Click();
        Text(cut, "browser-discovery-page-observed").Should().Be(expected);
        cut.Find("[data-testid=browser-discovery-nav-overview]").Click();
        cut.Find("[data-testid=browser-discovery-companion-setup-toggle]").Click();
        Text(cut, "browser-companion-last-evidence").Should().Be(expected, "setup and the summary now use one format");

        Discovery.GetSnapshot("dev").Pages.Clear();
        Seed("/admin/roles", DateTimeOffset.Now.AddMinutes(-1));
        var (fresh, _) = await OpenAsync();
        Text(fresh, "bd-last-evidence").Should().MatchRegex(@"^\d\d:\d\d:\d\d$", "today's evidence keeps the time-only convention");
    }

    // §62 / §36 / §37 Observed zero stays zero; missing stays Not observed.
    [Fact]
    public async Task PerformanceZeroAndMissingAreNeverConfused()
    {
        Seed("/zero", DateTimeOffset.Now, new BrowserPerformanceSummary { ObservationType = "initial-load", Cls = 0, StabilizationMs = 500, ResourceCount = 12, TransferredBytes = 2048 });
        Seed("/missing", DateTimeOffset.Now, new BrowserPerformanceSummary { ObservationType = "spa-navigation", Cls = null, StabilizationMs = 802 });
        var (cut, _) = await OpenAsync();
        cut.Find("[data-testid=browser-discovery-nav-evidence]").Click();
        cut.Find("[data-testid=browser-discovery-evidence-nav-performance]").Click();

        var rows = cut.FindAll("[data-testid=browser-discovery-evidence-performance-row]");
        string Cell(string route, int index) => rows.Single(r => r.TextContent.Contains(route)).Children[index].TextContent.Trim();
        Cell("/zero", 2).Should().Be("0", "an observed CLS of zero is a measurement");
        Cell("/missing", 2).Should().Be("Not observed", "CLS was not reported");
        Cell("/missing", 4).Should().Be("Not observed", "resources were not reported");
        Cell("/missing", 5).Should().Be("Not observed", "transfer size was not reported");
        Cell("/zero", 4).Should().Be("12");
        cut.Find("#bd-evidence-panel-performance").TextContent.Should().Contain("thresholds are applied in Frontend Quality Review")
            .And.NotContainAny("Good", "Poor", "Pass", "Fail", "Needs improvement");
    }

    // §30 / §61 Source and rule come from their own fields: the summary's Engine for checks, axe for axe rules.
    [Fact]
    public async Task AccessibilityRowsShowSourceAndRuleFromTheModel()
    {
        Seed("/admin/roles", DateTimeOffset.Now, accessibility: new BrowserAccessibilitySummary
        {
            Engine = "BirkNext Accessibility Checks",
            Checks = [new() { CheckId = "text-contrast", Outcome = "Fail", Tested = 18, Failed = 1 }],
            Axe = new BrowserAxeEvidence { Rules = [new() { RuleId = "color-contrast", Outcome = "Fail", Count = 2, CriterionIds = ["1.4.3"] }] },
        });
        var (cut, _) = await OpenAsync();
        cut.Find("[data-testid=browser-discovery-nav-evidence]").Click();
        cut.Find("[data-testid=browser-discovery-evidence-nav-accessibility]").Click();

        var rows = cut.FindAll("[data-testid=browser-discovery-evidence-a11y-row]");
        (string Source, string Item) Of(AngleSharp.Dom.IElement r) =>
            (r.QuerySelector("[data-testid=browser-discovery-a11y-source]")!.TextContent.Trim(), r.QuerySelector("[data-testid=browser-discovery-a11y-item]")!.TextContent.Trim());
        rows.Select(Of).Should().Contain(("axe", "color-contrast")).And.Contain(("BirkNext Accessibility Checks", "text-contrast"));
        rows.Select(r => r.TextContent).Should().NotContain(t => t.Contains("axe · ") || t.Contains("Check · "), "source and rule are separate, not one composed label");

        var table = cut.Find("[data-testid=browser-discovery-evidence-accessibility-table]");
        Text(cut, "browser-discovery-a11y-table-note").Should().Be("Rows are source observations mapped to related WCAG references. They are not criterion outcomes.");
        table.QuerySelectorAll("thead th").Select(h => h.TextContent.Trim()).Should().Contain("Related WCAG reference").And.Contain("Raw observation");
        table.QuerySelectorAll("thead th").Single(h => h.TextContent.Trim() == "Raw observation").GetAttribute("title").Should().Contain("not a BirkNext finding");
        table.QuerySelector("tbody")!.TextContent.Should().NotContainAny("Failed criterion", "WCAG failure", "Compliant", "Non-compliant",
            "Manual review required", "Severity", "Recommendation");
    }

    // §20 / §21 The secondary DOM counts read as labelled metadata, not another table row.
    [Fact]
    public async Task DomSecondaryStructureIsALabelledMetadataGroup()
    {
        Seed("/admin/roles", DateTimeOffset.Now);
        var (cut, _) = await OpenAsync();
        cut.Find("[data-testid=browser-discovery-nav-evidence]").Click();

        var extra = cut.Find("[data-testid=browser-discovery-evidence-dom-extra-row] .bd-dom-extra");
        extra.QuerySelector(".bd-dom-extra-label")!.TextContent.Should().Be("Additional structure:");
        extra.TextContent.Should().Contain("Depth").And.Contain("20").And.Contain("iFrames").And.Contain("Dialogs").And.Contain("·");
        extra.QuerySelector("[title]")!.GetAttribute("title").Should().StartWith("Maximum observed DOM depth");
        extra.TextContent.Should().NotContainAny("issue", "concern", "failure");
    }

    // §40 / §63 The pairing copy names what is paired: the extension, with this Target Environment.
    [Fact]
    public async Task SetupNamesTheExtensionAndTheTargetEnvironmentAsWhatIsPaired()
    {
        Seed("/admin/roles", DateTimeOffset.Now);
        var (cut, _) = await OpenAsync();
        cut.Find("[data-testid=browser-discovery-companion-setup-toggle]").Click();

        var action = Text(cut, "browser-companion-connection");
        action.Should().Be("Pair the Browser Companion extension in your managed Edge browser with this Target Environment to collect evidence from its approved origins.");
        action.Should().NotContain("Pair the managed Edge browser");
        // Setup states the pairing and the next step; it does not repeat the banner's title sentence.
        cut.Find("[data-testid=browser-discovery-companion-setup]").TextContent.Should().NotContain("Browser Companion not paired");
        Text(cut, "browser-companion-pairing").Should().Be("Not paired");
    }

    // §55 Banner, Live Session and setup each say their own thing.
    [Fact]
    public async Task BannerLiveSessionAndSetupHaveDistinctResponsibilities()
    {
        Seed("/admin/roles", DateTimeOffset.Now);
        var (cut, _) = await OpenAsync();
        cut.Find("[data-testid=browser-discovery-companion-setup-toggle]").Click();

        var banner = cut.Find("[data-testid=bd-companion-alert]").TextContent;
        var live = cut.Find("[data-testid=browser-discovery-live]").TextContent;
        var setup = cut.Find("[data-testid=browser-discovery-companion-setup]").TextContent;
        banner.Should().Contain("Browser Companion not paired").And.Contain("historical evidence remain");
        live.Should().Contain("Not connected").And.NotContain("not paired");
        setup.Should().Contain("Pair the Browser Companion extension").And.NotContain("historical evidence remain");
    }

    // §47 / §64 The live-details disclosure keeps the user's choice through polls.
    [Fact]
    public async Task LiveDetailsExpansionSurvivesPolling()
    {
        Seed("/admin/roles", DateTimeOffset.Now);
        var (cut, runtime) = await OpenAsync();
        const string toggle = "[data-testid=browser-discovery-live-details-toggle]";

        cut.Find(toggle).Click();
        for (var i = 0; i < 3; i++) { await cut.InvokeAsync(runtime.RefreshAsync); cut.Render(); }
        cut.Find(toggle).GetAttribute("aria-expanded").Should().Be("true");
        cut.Find(toggle).Click();
        await cut.InvokeAsync(runtime.RefreshAsync); cut.Render();
        cut.Find(toggle).GetAttribute("aria-expanded").Should().Be("false");
    }
}
