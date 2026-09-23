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
/// The Browser Discovery polish pass: a compact disconnected read-out, source-reported wording, neutral DOM columns, and
/// UI state that live polling never resets. Evidence ownership is unchanged — nothing here may read as a finding.
/// </summary>
public sealed class BrowserDiscoveryPolishTests : BunitContext
{
    private const string Origin = "https://application.example.test";
    private static readonly DateTimeOffset Captured = new(2026, 9, 21, 11, 54, 31, TimeSpan.Zero);
    private static readonly string[] Ids =
    [
        "text-contrast", "a11y-control-label", "a11y-document-lang", "a11y-duplicate-id", "a11y-link-name", "a11y-positive-tabindex",
        "focus-indicator", "keyboard-traversal", "label-in-name", "language-parts", "media-captions", "navigation-structure",
        "reflow-snapshot", "resize-text", "text-spacing", "timing-presence",
    ];
    private readonly FrontendAnalysisProfile _profile = new() { Id = "dev", Name = "Dev", TargetUrl = Origin };
    private IEndpointDiscoveryService Discovery => Services.GetRequiredService<IEndpointDiscoveryService>();

    public BrowserDiscoveryPolishTests()
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

    private static BrowserCompanionStatus Connected(params string[] liveRoutes) => new()
    {
        ProfileId = "dev", State = BrowserCompanionState.Connected, ApprovedOrigins = [Origin],
        Live = new()
        {
            ProfileId = "dev", ExtensionConnected = true,
            LivePages = liveRoutes.Select((r, i) => new BrowserCompanionLivePage
            {
                PageId = $"t-{i}", Origin = Origin, Route = r, ContentScriptInstanceId = $"i-{i}", RegisteredAt = Captured, LastSeenAt = Captured,
            }).ToList(),
        },
    };

    /// <summary>The screenshot's shape: pages with DOM, accessibility (flags and uncertainties) and performance evidence.</summary>
    private void Seed(string path, int flagged = 0, int uncertain = 0, double? stabilizationMs = 802, int depth = 14, int iframes = 0, int dialogs = 1)
    {
        // One registry-known check per source flag and per source uncertainty, plus one that observed nothing
        // non-neutral: the counts shown are counts of observations, exactly as the collector reported them.
        var checks = new List<BrowserWcagCheck> { new() { CheckId = "a11y-page-title", Outcome = "Pass", Tested = 1 } };
        checks.AddRange(Ids.Take(flagged).Select(id => new BrowserWcagCheck { CheckId = id, Outcome = "Fail", Tested = 5, Failed = 1 }));
        checks.AddRange(Ids.Skip(flagged).Take(uncertain).Select(id => new BrowserWcagCheck { CheckId = id, Outcome = "ManualReviewRequired", Tested = 5, Uncertain = 1 }));
        var accessibility = new BrowserAccessibilitySummary { Engine = "BirkNext Accessibility Checks", RulesEvaluated = 98, Checks = checks };
        Discovery.GetSnapshot("dev").Pages.Add(new()
        {
            PageOrigin = Origin, PagePath = path,
            BrowserEvidence = new()
            {
                PageOrigin = Origin, PagePath = path, CapturedAt = Captured,
                Dom = new BrowserDomSummary { NodeCount = 857, MaxDepth = depth, InteractiveCount = 29, FormControlCount = 9, ImageCount = 3, IframeCount = iframes, DialogCount = dialogs, HiddenFocusableCount = 2 },
                Accessibility = accessibility,
                Performance = new BrowserPerformanceSummary { ObservationType = "spa-navigation", StabilizationMs = stabilizationMs, LcpMs = 900 },
            },
        });
    }

    private async Task<(IRenderedComponent<BrowserDiscoveryTab> Cut, BrowserCompanionRuntime Runtime, Api Api)> OpenAsync(BrowserCompanionStatus? status = null)
    {
        var api = new Api();
        if (status is not null) api.Status = status;
        var runtime = new BrowserCompanionRuntime(api, Discovery, Services.GetRequiredService<IJSRuntime>());
        await runtime.FollowAsync(_profile);
        var cut = Render<BrowserDiscoveryTab>(p => p.Add(c => c.Profile, _profile).Add(c => c.Runtime, runtime));
        return (cut, runtime, api);
    }

    private static async Task PollAsync(IRenderedComponent<BrowserDiscoveryTab> cut, BrowserCompanionRuntime runtime)
    {
        await cut.InvokeAsync(runtime.RefreshAsync); // The same completion/event path as the runtime's poll.
        cut.Render();
    }

    private static string Text(IRenderedComponent<BrowserDiscoveryTab> cut, string testId) => cut.Find($"[data-testid={testId}]").TextContent.Trim();

    // ── Banner ─────────────────────────────────────────────────────────────────────────────────────────────────

    // §61 1-3: compact, says history remains, and history stays fully usable.
    [Fact]
    public async Task TheUnpairedBannerIsCompactAndHistoryStaysUsable()
    {
        Seed("/admin/emergency-access"); Seed("/admin/roles"); Seed("/admin/users");
        var (cut, _, _) = await OpenAsync();

        Text(cut, "bd-companion-alert-title").Should().Be("Browser Companion not paired");
        cut.Find("[data-testid=bd-companion-alert] .bd-alert-text").TextContent.Trim().Should().Be("Live capture unavailable.");
        Text(cut, "bd-companion-alert-history").Should().Be("3 pages of historical evidence remain available.");

        // Historical evidence is first-class: nothing is disabled or dimmed because live capture is unavailable.
        cut.FindAll("[data-testid=browser-discovery-open-page]").Should().HaveCount(3).And.OnlyContain(b => !b.HasAttribute("disabled"));
        cut.Find("[data-testid=browser-discovery-open-page]").Click();
        Text(cut, "browser-discovery-selected-page").Should().NotBeEmpty();
    }

    // §44: the banner's one action opens setup instead of duplicating the Pair button that lives inside it.
    [Fact]
    public async Task TheBannerOpensSetupRatherThanDuplicatingPair()
    {
        Seed("/admin/roles");
        var (cut, _, _) = await OpenAsync();

        cut.FindAll("[data-testid=browser-discovery-pair]").Should().BeEmpty();
        cut.Find("[data-testid=browser-discovery-companion-setup-toggle]").GetAttribute("aria-expanded").Should().Be("false");
        cut.Find("[data-testid=bd-companion-alert-setup]").Click();
        cut.Find("[data-testid=browser-discovery-companion-setup-toggle]").GetAttribute("aria-expanded").Should().Be("true");
        cut.FindAll("[data-testid=browser-companion-pair]").Should().HaveCount(1, "exactly one Pair control, inside setup");
    }

    // ── Live session ───────────────────────────────────────────────────────────────────────────────────────────

    // §62 4-7: compact when disconnected; the detailed facts are one click away and never claim anything live.
    [Fact]
    public async Task DisconnectedLiveSessionIsCompactAndClaimsNothingLive()
    {
        Seed("/admin/roles");
        var (cut, _, _) = await OpenAsync();

        var live = cut.Find("[data-testid=browser-discovery-live]");
        live.QuerySelectorAll(".bd-ov-item").Should().HaveCount(3, "Target, Status and Live capture — not six rows of None");
        Text(cut, "bd-target").Should().Be("Dev");
        Text(cut, "bd-session").Should().Be("Not connected");
        Text(cut, "bd-live-capture").Should().Be("Unavailable");
        Text(cut, "bd-live-note").Should().Be("No approved live page is currently available.");

        var toggle = cut.Find("[data-testid=browser-discovery-live-details-toggle]");
        toggle.GetAttribute("aria-expanded").Should().Be("false");
        cut.Find("[data-testid=browser-discovery-live-details-body]").HasAttribute("hidden").Should().BeTrue();
        // The detailed facts still exist and still say nothing is live — historical evidence never fills them in.
        Text(cut, "bd-current-page").Should().Be("None");
        Text(cut, "bd-live-dom").Should().Be("Not available");
        Text(cut, "bd-content-script").Should().Be("Not available");
        toggle.Click();
        cut.Find("[data-testid=browser-discovery-live-details-body]").HasAttribute("hidden").Should().BeFalse();
    }

    // §63 8-9: connected keeps every liveness fact, and connected alone never implies a page or a DOM.
    [Fact]
    public async Task ConnectedLiveSessionKeepsEveryLivenessFact()
    {
        Seed("/admin/roles");
        var (cut, _, _) = await OpenAsync(Connected());

        cut.FindAll("[data-testid=browser-discovery-live-details]").Should().BeEmpty();
        Text(cut, "bd-session").Should().Be("Connected");
        Text(cut, "bd-live-pages").Should().Be("0");
        Text(cut, "bd-current-page").Should().Be("None");
        Text(cut, "bd-content-script").Should().Be("Not available");
        Text(cut, "bd-live-dom").Should().Be("Not available");
    }

    // ── Historical summary ─────────────────────────────────────────────────────────────────────────────────────

    // §64 10-14.
    [Fact]
    public async Task HistoricalSummaryCountsAreExactAndCompact()
    {
        Seed("/a"); Seed("/b"); Seed("/c", stabilizationMs: null);
        var (cut, _, _) = await OpenAsync();

        Text(cut, "bd-pages-count").Should().Be("3");
        Text(cut, "bd-last-evidence").Should().Be(BrowserDiscoveryPresentation.EvidenceTimestamp(Captured, DateTimeOffset.Now));
        Text(cut, "bd-evidence-dom").Should().Be("3 pages");
        Text(cut, "bd-evidence-accessibility").Should().Be("3 pages");
        Text(cut, "bd-evidence-performance").Should().Be("3 pages");
        cut.Find("[data-testid=browser-discovery-summary]").TextContent.Should().NotContain("pages captured");
    }

    // ── Overview ───────────────────────────────────────────────────────────────────────────────────────────────

    // §65 15-18.
    [Fact]
    public async Task OverviewRowsAreEvidenceLabelsWithSourceAndNoJudgement()
    {
        Seed("/admin/roles", flagged: 7, uncertain: 8);
        var (cut, _, _) = await OpenAsync();

        Text(cut, "browser-discovery-page-liveness").Should().Be("Historical evidence only");
        var row = cut.Find("[data-testid=browser-discovery-page-row]").TextContent;
        row.Should().Contain("Captured").And.Contain("Browser Companion");
        cut.Find("[data-testid=browser-discovery-overview-table] thead").TextContent.Should().Contain("Last observed");
        // The rows are evidence. (The FQR hand-off's own sentence names findings as FQR's job, and is not a result here.)
        NoInterpretation(cut.Find("[data-testid=browser-discovery-overview-table] tbody").TextContent);
    }

    // ── Pages ──────────────────────────────────────────────────────────────────────────────────────────────────

    // §66 19-23, §67 24-26, §68 27-29.
    [Fact]
    public async Task SelectedPageIsSourceReportedRawEvidenceWithTheLocalActionFirst()
    {
        Seed("/admin/emergency-access", flagged: 7, uncertain: 8);
        var (cut, _, _) = await OpenAsync();
        cut.Find("[data-testid=browser-discovery-nav-pages]").Click();

        Text(cut, "browser-discovery-selected-liveness").Should().Be("Historical evidence only");
        Text(cut, "browser-discovery-page-source").Should().Be("Browser Companion");
        Text(cut, "browser-discovery-page-dom-line").Should().Be("857 nodes · 29 interactive elements · 9 form controls");
        var items = cut.FindAll("[data-testid=browser-discovery-page-accessibility-line] li").Select(li => li.TextContent.Trim()).ToList();
        items.Should().Equal("16 raw checks observed", "7 source-reported flags", "8 source-reported uncertainties");
        cut.Find("[data-testid=browser-discovery-page-flags]").GetAttribute("title").Should().Contain("It is not a BirkNext finding");
        cut.Find("[data-testid=browser-discovery-page-uncertainties]").GetAttribute("title").Should().Contain("does not automatically require manual review");
        Text(cut, "browser-discovery-page-performance-line").Should().Be("SPA navigation · 802 ms page stabilization");

        // The local action leads and is the primary control; the hand-off follows as a quieter, named link.
        var actions = cut.Find(".bd-pageactions").Children;
        actions[0].GetAttribute("data-testid").Should().Be("browser-discovery-page-raw");
        actions[0].ClassList.Should().Contain("btn-primary");
        actions[1].GetAttribute("data-testid").Should().Be("browser-discovery-open-review-page");
        actions[1].GetAttribute("href").Should().Be("/frontend-quality-review");
        actions[1].GetAttribute("aria-label").Should().Contain("Frontend Quality Review");
        NoInterpretation(cut.Find("[data-testid=browser-discovery-page-evidence]").TextContent);

        actions[0].Click();
        cut.Find("[data-testid=browser-discovery-nav-evidence]").GetAttribute("aria-selected").Should().Be("true");
    }

    [Fact]
    public async Task AnAbsentStabilizationIsNotObservedNeverZero()
    {
        Seed("/admin/roles", stabilizationMs: null);
        var (cut, _, _) = await OpenAsync();
        cut.Find("[data-testid=browser-discovery-nav-evidence]").Click();
        cut.Find("[data-testid=browser-discovery-evidence-nav-performance]").Click();

        var row = cut.Find("[data-testid=browser-discovery-evidence-performance-row]");
        row.TextContent.Should().Contain("Not observed").And.NotContain("0 ms");
        row.QuerySelectorAll("[class*=warning], [class*=danger], [class*=error], [class*=pass], [class*=fail]").Should().BeEmpty("raw values are neutral");
        NoInterpretation(cut.Find("#bd-evidence-panel-performance").TextContent);
    }

    // ── Evidence ───────────────────────────────────────────────────────────────────────────────────────────────

    // §69 30-32, §70 33-37.
    [Fact]
    public async Task DomTableIsSevenNeutralColumnsWithTheRestUnderEachPage()
    {
        Seed("/admin/roles", depth: 14, iframes: 2, dialogs: 1);
        var (cut, _, _) = await OpenAsync();
        cut.Find("[data-testid=browser-discovery-nav-evidence]").Click();

        Text(cut, "bd-inv-dom").Should().Be("1 page");
        Text(cut, "bd-inv-source").Should().Be("Browser Companion");
        cut.Find("[data-testid=browser-discovery-evidence-handoff]").Should().NotBeNull();

        var table = cut.Find("[data-testid=browser-discovery-evidence-dom-table]");
        table.QuerySelectorAll("thead th").Select(h => h.TextContent.Trim())
            .Should().Equal("Page", "Nodes", "Interactive", "Forms", "Images", "Hidden focusable elements", "Observed at");
        table.QuerySelector("caption")!.TextContent.Should().Be("Structural observations per page. These counts are evidence only; they are not findings or pass/fail results.");
        var extra = cut.Find("[data-testid=browser-discovery-evidence-dom-extra-row]");
        extra.TextContent.Replace(" ", "").Should().Contain("Depth14").And.Contain("iFrames2").And.Contain("Dialogs1");
        extra.QuerySelector("[title]")!.GetAttribute("title").Should().StartWith("Maximum observed DOM depth");
        NoInterpretation(table.QuerySelector("tbody")!.TextContent);
    }

    // §71 38-41: landmarks collapsed by default, and an opened one survives polls.
    [Fact]
    public async Task LandmarksDisclosureStartsCollapsedAndSurvivesPolling()
    {
        Discovery.GetSnapshot("dev").Pages.Add(new()
        {
            PageOrigin = Origin, PagePath = "/admin/roles",
            BrowserEvidence = new()
            {
                PageOrigin = Origin, PagePath = "/admin/roles", CapturedAt = Captured,
                Dom = new BrowserDomSummary { NodeCount = 85, Landmarks = new() { ["main"] = 1, ["navigation"] = 1 }, HeadingCounts = new() { ["h1"] = 1, ["h2"] = 2 } },
            },
        });
        var (cut, runtime, _) = await OpenAsync(Connected());
        cut.Find("[data-testid=browser-discovery-nav-evidence]").Click();

        var toggle = cut.Find("[data-testid$=-toggle][data-testid^=browser-discovery-evidence-dom-detail-]");
        toggle.GetAttribute("aria-expanded").Should().Be("false");
        toggle.Click();
        for (var i = 0; i < 3; i++) await PollAsync(cut, runtime);
        cut.Find("[data-testid$=-toggle][data-testid^=browser-discovery-evidence-dom-detail-]").GetAttribute("aria-expanded").Should().Be("true");
        var structure = cut.Find("[data-testid^=browser-discovery-evidence-dom-structure-]").TextContent;
        NoInterpretation(structure);
        structure.Should().NotContainAny("missing", "invalid", "remediation", "WCAG");
    }

    // §72 42-46.
    [Fact]
    public async Task AccessibilityTabIsRawSourceEvidence()
    {
        Seed("/admin/roles", flagged: 2, uncertain: 1);
        var (cut, _, _) = await OpenAsync();
        cut.Find("[data-testid=browser-discovery-nav-evidence]").Click();
        cut.Find("[data-testid=browser-discovery-evidence-nav-accessibility]").Click();

        Text(cut, "browser-discovery-evidence-a11y-intro").Should().Be(
            "Raw accessibility observations from Browser Companion. These are evidence only and are not WCAG findings or compliance results; Frontend Quality Review interprets them.");
        var counts = Text(cut, "browser-discovery-evidence-a11y-counts");
        counts.Should().Contain("2 source-reported flags").And.Contain("1 source-reported uncertainty");
        Text(cut, "browser-discovery-uncertainty-note").Should().Contain("does not automatically create a manual-review requirement");
        cut.Find("[data-testid=browser-discovery-evidence-accessibility-table] thead").TextContent.Should().Contain("Related WCAG reference").And.NotContain("Failed");
        cut.Find("[data-testid=browser-discovery-evidence-a11y-counts] .bd-count-flagged").GetAttribute("title").Should().Contain("not a BirkNext finding");
        NoInterpretation(cut.Find("[data-testid=browser-discovery-evidence-accessibility-table] tbody").TextContent);
    }

    // ── Setup and state across polls ───────────────────────────────────────────────────────────────────────────

    // §74 51-55.
    [Fact]
    public async Task SetupIsCollapsedOwnsNoCertificateOrResetAndKeepsTheUsersChoice()
    {
        Seed("/admin/roles");
        var (cut, runtime, _) = await OpenAsync(Connected());
        var toggle = "[data-testid=browser-discovery-companion-setup-toggle]";

        cut.Find(toggle).GetAttribute("aria-expanded").Should().Be("false");
        cut.Markup.Should().NotContainAny("Remove test certificate", "Reset Profile");
        cut.Find(toggle).Click();
        await PollAsync(cut, runtime);
        cut.Find(toggle).GetAttribute("aria-expanded").Should().Be("true");
        cut.Find(toggle).Click();
        await PollAsync(cut, runtime);
        cut.Find(toggle).GetAttribute("aria-expanded").Should().Be("false");
        cut.FindAll("[data-testid=browser-companion-pair]").Should().HaveCountLessThan(2);
    }

    // §75 56-58: polls never reset the view, the selected page or the evidence sub-tab.
    [Fact]
    public async Task PollingKeepsTheViewSelectedPageAndEvidenceTab()
    {
        Seed("/admin/roles"); Seed("/admin/users");
        var (cut, runtime, _) = await OpenAsync(Connected());

        cut.Find("[data-testid=browser-discovery-nav-pages]").Click();
        cut.FindAll("[data-testid=browser-discovery-page-link]").Single(l => l.TextContent.Contains("/admin/users")).Click();
        for (var i = 0; i < 3; i++) await PollAsync(cut, runtime);
        cut.Find("[data-testid=browser-discovery-nav-pages]").GetAttribute("aria-selected").Should().Be("true");
        Text(cut, "browser-discovery-selected-page").Should().Be("/admin/users");

        cut.Find("[data-testid=browser-discovery-nav-evidence]").Click();
        cut.Find("[data-testid=browser-discovery-evidence-nav-performance]").Click();
        for (var i = 0; i < 3; i++) await PollAsync(cut, runtime);
        cut.Find("[data-testid=browser-discovery-evidence-nav-performance]").GetAttribute("aria-selected").Should().Be("true");
    }

    [Fact]
    public async Task ASelectedPageThatDisappearsFallsBackAndSaysSo()
    {
        Seed("/admin/roles"); Seed("/admin/users");
        var (cut, runtime, _) = await OpenAsync(Connected());
        cut.Find("[data-testid=browser-discovery-nav-pages]").Click();
        cut.FindAll("[data-testid=browser-discovery-page-link]").Single(l => l.TextContent.Contains("/admin/users")).Click();

        Discovery.GetSnapshot("dev").Pages.RemoveAll(p => p.PagePath == "/admin/users");
        await PollAsync(cut, runtime);

        Text(cut, "browser-discovery-selected-page").Should().Be("/admin/roles");
        Text(cut, "browser-discovery-selection-lost").Should().Contain("no longer in the captured evidence");
    }

    // §77 62-65.
    [Fact]
    public async Task TabsExposeSelectionAndStateIsNeverColourOnly()
    {
        Seed("/admin/roles");
        var (cut, _, _) = await OpenAsync();

        cut.FindAll(".bd-nav [role=tab]").Should().OnlyContain(t => t.HasAttribute("aria-selected"));
        cut.Find("[data-testid=browser-discovery-nav-overview]").GetAttribute("aria-selected").Should().Be("true");
        cut.FindAll(".bd-state").Should().OnlyContain(b => b.TextContent.Trim().Length > 0);
        cut.FindAll(".disclosure-toggle").Should().OnlyContain(b => b.HasAttribute("aria-expanded"));
    }

    // §78: the rendered text never carries Frontend Quality Review's vocabulary as a Browser Discovery result.
    private static void NoInterpretation(string text) =>
        text.Should().NotContainAny("finding", "Finding", "failed criterion", "Failed criterion", "compliant", "Compliant",
            "recommendation", "Recommendation", "severity", "Severity", "manual review required", "Manual review required",
            "Passed", "Failed", "violation", "Violation");
}
