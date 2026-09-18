using BirkNext.BrowserCompanion;
using BirkNext.LocalHttpsProxy;
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
/// Browser Discovery collects and presents browser evidence; Frontend Quality Review interprets it.
/// These tests hold that boundary in the DOM: Browser Discovery has Overview / Pages / Evidence only,
/// its WCAG areas are evidence groupings rather than conformance results, and no review verdict,
/// profile selector or threshold judgement is duplicated here.
/// </summary>
public sealed class BrowserDiscoveryTabTests : BunitContext
{
    private const string Origin = "https://application.example.test";
    private readonly FrontendAnalysisProfile _profile = new() { Id = "dev", Name = "Dev", TargetUrl = Origin };
    private IEndpointDiscoveryService Discovery => Services.GetRequiredService<IEndpointDiscoveryService>();

    public BrowserDiscoveryTabTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<IEndpointDiscoveryService, EndpointDiscoveryService>();
        Services.AddSingleton<IFrontendAnalysisSettingsService, FrontendAnalysisSettingsService>();
        Services.AddSingleton(Mock.Of<ITargetEnvironmentDetectionApiService>());
        JSInterop.Setup<string?>("birkNextStorage.getItem", _ => true).SetResult("""
            {"activeProfileId":"dev","profiles":[{"id":"dev","name":"Dev","targetUrl":"https://application.example.test"}]}
            """);
    }

    private static readonly DateTimeOffset Observed = new(2026, 9, 18, 13, 42, 10, TimeSpan.Zero);

    /// <summary>A page with evidence in all four WCAG principles, DOM evidence and performance evidence.</summary>
    private void SeedFullPage(string path = "/dashboard") => Discovery.GetSnapshot("dev").Pages.Add(new()
    {
        PageOrigin = Origin, PagePath = path,
        BrowserEvidence = new()
        {
            PageOrigin = Origin, PagePath = path, CapturedAt = Observed,
            Dom = new() { NodeCount = 812, MaxDepth = 17, InteractiveCount = 44, FormControlCount = 6, ImageCount = 12,
                          Landmarks = new() { ["main"] = 1 }, HeadingCounts = new() { ["h1"] = 1 } },
            Accessibility = new()
            {
                Engine = "BirkNext Accessibility Checks",
                Findings = [new() { RuleId = "a11y-image-alt", Wcag = "1.1.1", Title = "Image without text alternative", Count = 3, Selectors = ["img.logo"] }],
                Checks = [new() { CheckId = "a11y-page-title", Outcome = "Pass", Tested = 1 },
                          new() { CheckId = "a11y-document-lang", Outcome = "Pass", Tested = 1 }],
                Axe = new() { State = "Available", Rules = [new() { RuleId = "aria-roles", Outcome = "Violation", CriterionIds = ["4.1.2"], Count = 2 }] }
            },
            Performance = new() { ObservationType = "initial-load", LcpMs = 1234, TtfbMs = 210, Cls = 0.03 }
        }
    });

    /// <summary>A page observed by the companion with no accessibility and no performance evidence at all.</summary>
    private void SeedBarePage(string path = "/person/123") => Discovery.GetSnapshot("dev").Pages.Add(new()
    {
        PageOrigin = Origin, PagePath = path,
        BrowserEvidence = new() { PageOrigin = Origin, PagePath = path, CapturedAt = Observed.AddMinutes(-2), Dom = new() { NodeCount = 100 } }
    });

    /// <summary>Proxy traffic only: a page Endpoint Discovery knows about that has produced no browser evidence.</summary>
    private void SeedProxyOnlyPage() => Discovery.GetSnapshot("dev").Pages.Add(new()
    {
        PageOrigin = Origin, PagePath = "/proxy-only",
        Endpoints = [new() { Scheme = "https", Host = "api.example.test", Port = 443, Path = "/api/items",
                             Category = ObservedTrafficCategory.Rest, LastObservedAt = Observed }]
    });

    private IRenderedComponent<BrowserDiscoveryTab> Open(BrowserCompanionRuntime? runtime = null) =>
        Render<BrowserDiscoveryTab>(p => p.Add(c => c.Profile, _profile).Add(c => c.Runtime, runtime));

    private static void Nav(IRenderedComponent<BrowserDiscoveryTab> cut, string key) =>
        cut.Find($"[data-testid=browser-discovery-nav-{key}]").Click();

    private static string Text(IRenderedComponent<BrowserDiscoveryTab> cut, string testId) =>
        cut.Find($"[data-testid={testId}]").TextContent.Trim();

    // ── 27. Navigation ───────────────────────────────────────────────────────

    [Fact]
    public void BrowserDiscoveryHasOverviewPagesAndEvidenceOnly()
    {
        SeedFullPage();
        var cut = Open();

        cut.FindAll(".bd-nav [role=tab]").Select(t => t.TextContent.Trim())
            .Should().Equal("Overview", "Pages (1)", "Evidence");

        // Review-oriented and network-oriented views are not part of Browser Discovery.
        foreach (var absent in new[] { "wcag", "performance", "shared", "integrations" })
            cut.FindAll($"[data-testid=browser-discovery-nav-{absent}]").Should().BeEmpty(absent);
        cut.FindAll("[data-testid=browser-quality-wcag-tab]").Should().BeEmpty();
        cut.FindAll("[data-testid=browser-quality-performance-tab]").Should().BeEmpty();
        cut.FindComponents<WcagWorkspace>().Should().BeEmpty();
        cut.FindComponents<BrowserQualityWorkspace>().Should().BeEmpty();
    }

    [Fact]
    public void OverviewIsSelectedByDefaultAndPagesAndEvidenceAreReachable()
    {
        SeedFullPage();
        var cut = Open();

        cut.Find("[data-testid=browser-discovery-nav-overview]").GetAttribute("aria-selected").Should().Be("true");
        cut.FindAll("[data-testid=browser-discovery-overview-table]").Should().ContainSingle();

        Nav(cut, "pages");
        cut.Find("[data-testid=browser-discovery-nav-pages]").GetAttribute("aria-selected").Should().Be("true");
        cut.FindAll("[data-testid=browser-discovery-page-summary]").Should().ContainSingle();

        Nav(cut, "evidence");
        cut.Find("[data-testid=browser-discovery-nav-evidence]").GetAttribute("aria-selected").Should().Be("true");
        cut.FindAll("[data-testid=browser-discovery-evidence-dom]").Should().ContainSingle();
    }

    // ── 28. Overview table ───────────────────────────────────────────────────

    [Fact]
    public void OverviewHasOneRowPerObservedBrowserPageWithTheBrowserOrientedColumns()
    {
        SeedFullPage();
        SeedBarePage();
        SeedProxyOnlyPage();
        var cut = Open();

        var table = cut.Find("[data-testid=browser-discovery-overview-table]");
        table.QuerySelectorAll("thead th").Select(h => h.TextContent.Trim())
            .Should().Equal("Page / Route", "WCAG areas", "DOM", "Performance evidence", "Last seen", "Source");

        var rows = cut.FindAll("[data-testid=browser-discovery-page-row]");
        rows.Count.Should().Be(2, "the proxy-only page produced no browser evidence");
        cut.Markup.Should().NotContain("/proxy-only");
        Text(cut, "bd-pages-count").Should().Be("2");

        // Source is the companion; proxy is never relabelled as browser evidence.
        table.QuerySelectorAll(".bd-source").Select(s => s.TextContent.Trim()).Should().AllBe("Browser Companion");
        table.TextContent.Should().NotContain("Proxy");
    }

    [Fact]
    public void WcagAreaBadgesGroupEvidenceAndNeverRenderPassOrFail()
    {
        SeedFullPage();
        var cut = Open();

        var row = cut.Find("[data-testid=browser-discovery-page-row]");
        row.QuerySelectorAll("[data-testid=browser-discovery-area-badge]").Select(b => b.TextContent.Trim())
            .Should().Equal("Perceivable", "Operable", "Understandable", "Robust");

        foreach (var badge in row.QuerySelectorAll("[data-testid=browser-discovery-area-badge]"))
            badge.TextContent.Should().NotContainAny("Pass", "Fail", "Compliant", "Conform");

        // The grouping states its own meaning, in text.
        Text(cut, "browser-discovery-area-disclaimer").Should().Contain("not an assessment");
    }

    [Fact]
    public void EvidenceStatesAreAvailabilityNotVerdictsAndUnavailableIsNeverZero()
    {
        SeedBarePage();
        var cut = Open();

        var row = cut.Find("[data-testid=browser-discovery-page-row]");
        var cells = row.QuerySelectorAll("td").Select(c => c.TextContent.Trim()).ToList();
        cells[1].Should().Be("Available", "DOM evidence exists");
        cells[2].Should().Be("Unavailable", "no performance evidence was observed");
        cells[2].Should().NotBe("0");
        row.TextContent.Should().NotContainAny("Passed", "Failed", "Good", "Poor", "Needs improvement");
        row.QuerySelector("[data-testid=browser-discovery-areas-none]")!.TextContent
            .Should().Contain("No accessibility evidence");
    }

    // ── 29. Pages ────────────────────────────────────────────────────────────

    [Fact]
    public void PageDetailRendersDomWcagAreaGroupingAndPerformanceEvidence()
    {
        SeedFullPage();
        var cut = Open();
        Nav(cut, "pages");

        Text(cut, "browser-discovery-selected-page").Should().Be("/dashboard");
        Text(cut, "browser-discovery-page-dom-state").Should().Be("Available");
        Text(cut, "browser-discovery-page-performance-state").Should().Be("Available");

        // Accessibility evidence is grouped under the principle each item belongs to.
        cut.Find("[data-testid=browser-discovery-area-perceivable]").TextContent
            .Should().Contain("Image without text alternative").And.Contain("1.1.1");
        cut.Find("[data-testid=browser-discovery-area-operable]").TextContent.Should().Contain("a11y-page-title");
        cut.Find("[data-testid=browser-discovery-area-understandable]").TextContent.Should().Contain("a11y-document-lang");
        cut.Find("[data-testid=browser-discovery-area-robust]").TextContent.Should().Contain("aria-roles");

        cut.Find("[data-testid=browser-discovery-page-dom]").TextContent.Should().Contain("812");
        cut.Find("[data-testid=browser-discovery-page-performance]").TextContent
            .Should().Contain("1234 ms").And.Contain("Largest contentful paint");
    }

    [Fact]
    public void PageDetailOmitsPerformanceWhenNoneWasObservedAndCarriesNoComplianceVerdict()
    {
        SeedBarePage();
        var cut = Open();
        Nav(cut, "pages");

        cut.FindAll("[data-testid=browser-discovery-page-performance]").Should().BeEmpty();
        Text(cut, "browser-discovery-page-performance-empty").Should().Contain("No performance evidence");
        Text(cut, "browser-discovery-page-areas-empty").Should().Contain("No accessibility evidence");

        var detail = cut.Find(".bd-pagedetail").TextContent;
        detail.Should().NotContainAny("Conformance", "Compliant", "WCAG score", "Passed", "Failed", "0 ms");
    }

    [Fact]
    public void OpeningAPageFromTheOverviewSelectsItInPages()
    {
        SeedFullPage();
        SeedBarePage();
        var cut = Open();

        cut.FindAll("[data-testid=browser-discovery-open-page]").Single(b => b.TextContent.Trim() == "/person/123").Click();

        cut.Find("[data-testid=browser-discovery-nav-pages]").GetAttribute("aria-selected").Should().Be("true");
        Text(cut, "browser-discovery-selected-page").Should().Be("/person/123");
    }

    // ── 30. Evidence explorer ────────────────────────────────────────────────

    [Fact]
    public void EvidenceExplorerGroupsByTypeAndKeepsSourceAndTimestamp()
    {
        SeedFullPage();
        var cut = Open();
        Nav(cut, "evidence");

        foreach (var group in new[] { "dom", "accessibility", "performance" })
            cut.FindAll($"[data-testid=browser-discovery-evidence-{group}]").Should().ContainSingle(group);

        var accessibility = cut.Find("[data-testid=browser-discovery-evidence-accessibility-table]");
        accessibility.QuerySelectorAll("thead th").Select(h => h.TextContent.Trim())
            .Should().Equal("Page", "WCAG area", "Criterion", "Evidence item", "Observed at", "Source");
        accessibility.TextContent.Should().Contain("Perceivable").And.Contain("1.1.1").And.Contain("Browser Companion");

        var performance = cut.Find("[data-testid=browser-discovery-evidence-performance]").TextContent;
        performance.Should().Contain("Largest contentful paint").And.Contain("1234 ms").And.Contain("Browser Companion");
        cut.Find("[data-testid=browser-discovery-evidence-dom]").TextContent.Should().Contain("Nodes").And.Contain("812");

        // Observation timestamps are rendered where the evidence carries them.
        cut.Markup.Should().MatchRegex(@"\d{2}:\d{2}:\d{2}");
    }

    [Fact]
    public void EvidenceExplorerReportsUnavailableRatherThanFabricatingRows()
    {
        SeedBarePage();
        var cut = Open();
        Nav(cut, "evidence");

        cut.Find("[data-testid=browser-discovery-evidence-performance]").TextContent
            .Should().Contain("No performance evidence observed yet").And.NotContain("0 ms");
        cut.Find("[data-testid=browser-discovery-evidence-accessibility]").TextContent
            .Should().Contain("No accessibility evidence observed yet");
    }

    // ── 21. Empty state ──────────────────────────────────────────────────────

    [Fact]
    public void EmptyStateExplainsHowToCollectEvidenceAndShowsNoFakeRows()
    {
        var cut = Open();

        Text(cut, "browser-discovery-empty").Should()
            .Contain("No browser evidence yet")
            .And.Contain("Pair the managed Edge browser and open an approved application page");
        cut.FindAll("[data-testid=browser-discovery-page-row]").Should().BeEmpty();
        cut.FindAll("[data-testid=browser-discovery-overview-table]").Should().BeEmpty();
        Text(cut, "bd-pages-count").Should().Be("0");
        Text(cut, "bd-last-evidence").Should().Be("None");
        // The companion card stays available so the user can act on the empty state.
        cut.FindComponents<BrowserCompanionPanel>().Should().ContainSingle();
    }

    // ── 20. Connection states ────────────────────────────────────────────────

    [Theory]
    [InlineData(BrowserCompanionState.NotPaired, "Not connected", "Pair the managed Edge browser")]
    [InlineData(BrowserCompanionState.Disconnected, "Paired · not reporting", "resume reporting")]
    [InlineData(BrowserCompanionState.Connected, "Connected", "Collecting browser evidence")]
    public async Task ConnectionStatesAreDistinctAndNeverImplyEvidence(BrowserCompanionState state, string label, string message)
    {
        var api = new Mock<IBrowserCompanionApiService>();
        api.Setup(a => a.StatusAsync("dev", It.IsAny<CancellationToken>())).ReturnsAsync(new BrowserCompanionStatus
        {
            ProfileId = "dev", State = state, ApprovedOrigins = [Origin],
            CurrentPageOrigin = Origin, CurrentPagePath = "/dashboard"
        });
        await using var runtime = new BrowserCompanionRuntime(api.Object, Discovery, Services.GetRequiredService<IJSRuntime>());
        var cut = Open(runtime);

        Text(cut, "bd-session").Should().Be(label);
        Text(cut, "browser-companion-connection").Should().Contain(message);
        // Approved origins moved out of the primary summary into the companion details.
        cut.FindAll("[data-testid=bd-origins]").Should().BeEmpty();
        Text(cut, "browser-companion-origins").Should().Contain(Origin);
        Text(cut, "bd-current-page").Should().Be(Origin + "/dashboard");

        // Connected is a session state, never evidence.
        Text(cut, "bd-pages-count").Should().Be("0");
        Text(cut, "bd-last-evidence").Should().Be("None");
        cut.FindAll("[data-testid=browser-discovery-empty]").Should().ContainSingle();

        // Pairing actions follow the session state, not the evidence state. When pairing is the next step the
        // empty state owns the single Pair control; an existing session keeps Pair again on the companion card.
        cut.FindAll(state == BrowserCompanionState.NotPaired
            ? "[data-testid=browser-discovery-pair]"
            : "[data-testid=browser-companion-repair]").Should().ContainSingle();
        if (state == BrowserCompanionState.NotPaired)
            cut.FindAll("[data-testid=browser-companion-pair]").Should().BeEmpty("one problem gets one action");
    }

    // ── 19. Browser Companion card ───────────────────────────────────────────

    [Fact]
    public void CompanionCardIsActionOrientedAndDoesNotRepeatTheSummaryReadouts()
    {
        SeedFullPage();
        var cut = Open();

        cut.FindComponents<BrowserCompanionPanel>().Should().ContainSingle();
        // The duplicated read-outs live in the Browser Discovery summary row only.
        foreach (var duplicated in new[] { "target", "pages", "dom", "accessibility", "performance" })
            cut.FindAll($"[data-testid=browser-companion-{duplicated}]").Should().BeEmpty(duplicated);
        // Connection state and message remain on the card.
        cut.FindAll("[data-testid=browser-companion-state]").Should().ContainSingle();
        cut.FindAll("[data-testid=browser-companion-connection]").Should().ContainSingle();
    }

    // ── 31. No review duplication ────────────────────────────────────────────

    [Fact]
    public void BrowserDiscoveryNeverRendersReviewJudgementOrItsControls()
    {
        SeedFullPage();
        var cut = Open();

        foreach (var view in new[] { "overview", "pages", "evidence" })
        {
            Nav(cut, view);
            cut.FindAll("[data-testid=wcag-profile]").Should().BeEmpty(view);
            cut.FindAll("[data-testid=wcag-criterion-row]").Should().BeEmpty(view);
            cut.FindComponents<WcagCoverage>().Should().BeEmpty(view);
            cut.FindComponents<PerformanceQualityPageView>().Should().BeEmpty(view);
            foreach (var word in new[] { "Norwegian public-sector requirements", "WCAG 2.1", "WCAG 2.2", "Assessment profile",
                                          "Manual-only", "Require manual review", "Failed criteria", "conformance",
                                          "Needs improvement", "Threshold" })
                cut.Markup.Should().NotContain(word, $"{word} belongs to Frontend Quality Review ({view})");
        }
    }

    // ── 24. Network concepts stay in Endpoint Discovery ──────────────────────

    [Fact]
    public void BrowserDiscoveryNeverAdoptsNetworkOrientedConcepts()
    {
        SeedFullPage();
        SeedProxyOnlyPage();
        var cut = Open();

        foreach (var view in new[] { "overview", "pages", "evidence" })
        {
            Nav(cut, view);
            foreach (var word in new[] { "Backend integrations", "Shared / background", "Service / Host",
                                          "GraphQL", "REST", "Bearer", "Proxy" })
                cut.Markup.Should().NotContain(word, $"{word} belongs to Endpoint Discovery ({view})");
        }
    }

    // ── 32. Accessibility of the navigation itself ───────────────────────────

    [Fact]
    public void ViewTabsAreKeyboardOperableAndExposeSelectionAndPanels()
    {
        SeedFullPage();
        var cut = Open();

        // Roving tabindex: only the selected tab is in the tab order.
        cut.FindAll(".bd-nav [role=tab]").Count(t => t.GetAttribute("tabindex") == "0").Should().Be(1);

        cut.Find("[data-testid=browser-discovery-nav-overview]").KeyDown("ArrowRight");
        cut.Find("[data-testid=browser-discovery-nav-pages]").GetAttribute("aria-selected").Should().Be("true");
        cut.Find("[data-testid=browser-discovery-nav-pages]").KeyDown("End");
        cut.Find("[data-testid=browser-discovery-nav-evidence]").GetAttribute("aria-selected").Should().Be("true");
        cut.Find("[data-testid=browser-discovery-nav-evidence]").KeyDown("Home");
        cut.Find("[data-testid=browser-discovery-nav-overview]").GetAttribute("aria-selected").Should().Be("true");

        // Each tab points at the panel it controls, and the table uses semantic headers.
        foreach (var tab in cut.FindAll(".bd-nav [role=tab]"))
            cut.FindAll("#" + tab.GetAttribute("aria-controls")).Should().NotBeEmpty(tab.TextContent);
        cut.Find("[data-testid=browser-discovery-overview-table]").QuerySelectorAll("thead th")
            .Should().OnlyContain(h => h.GetAttribute("scope") == "col");
        cut.Find("[data-testid=browser-discovery-page-row]").QuerySelector("th")!
            .GetAttribute("scope").Should().Be("row");
    }

    // ── 16. Hand-off to Frontend Quality Review ──────────────────────────────

    [Fact]
    public void OverviewLinksToFrontendQualityReviewWithEvidenceCounts()
    {
        SeedFullPage();
        SeedBarePage();
        var cut = Open();

        var handoff = cut.Find("[data-testid=browser-discovery-handoff]").TextContent;
        handoff.Should().Contain("Accessibility evidence available for 1 page(s)");
        handoff.Should().Contain("Performance evidence available for 1 page(s)");
        Text(cut, "browser-discovery-dom-count").Should().Contain("2 page(s)");
        cut.Find("[data-testid=browser-discovery-open-review-accessibility]").GetAttribute("href")
            .Should().Be("/frontend-quality-review");
    }

    // ── 17. Endpoint Discovery keeps its own taxonomy ────────────────────────

    [Theory]
    [InlineData("overview")]
    [InlineData("pages")]
    [InlineData("shared")]
    [InlineData("integrations")]
    public void EndpointViewsRetainNetworkOwnershipAndNeverRenderBrowserAssessments(string view)
    {
        SeedFullPage();
        var cut = Render<EndpointDiscoveryTab>(p => p.Add(c => c.Profile, _profile).Add(c => c.ProxyStatus,
            new LocalHttpsProxyStatus
            {
                State = LocalHttpsProxyState.Ready, AuthenticatedCredentialAvailable = true,
                ObservedNetworkEndpoints = [new() { Scheme = "https", Host = "api.example.test", Port = 443, Path = "/api/items",
                    PageOrigin = Origin, PagePath = "/dashboard", Category = ObservedTrafficCategory.Rest, LastObservedAt = DateTimeOffset.UtcNow }]
            }));

        cut.Find("[data-testid=discovery-active]").TextContent.Should().Be("Active");
        cut.Markup.Should().Contain("What this application communicates with");
        foreach (var id in new[] { "pages", "shared", "integrations" })
            cut.FindAll($"[data-testid=discovery-nav-{id}]").Should().ContainSingle();

        cut.Find($"[data-testid=discovery-nav-{view}]").Click();
        cut.FindComponents<BrowserCompanionPanel>().Should().BeEmpty();
        cut.FindComponents<WcagWorkspace>().Should().BeEmpty();
        cut.FindComponents<PerformanceQualityPageView>().Should().BeEmpty();
        cut.FindAll("[data-testid=browser-discovery-overview-table]").Should().BeEmpty();
        cut.Markup.Should().NotContain("WCAG areas");
    }

    // ── 23. Evidence survives navigation; it is not session state ────────────

    [Fact]
    public void StoredEvidenceIsRetainedAcrossViewChangesAndIsNotTiedToTheLiveSession()
    {
        SeedFullPage();
        var snapshot = Discovery.GetSnapshot("dev");
        var cut = Open();

        Text(cut, "bd-pages-count").Should().Be("1");
        Text(cut, "bd-session").Should().Be("Not connected", "evidence is stored, the session is not connected");

        Nav(cut, "evidence");
        Nav(cut, "overview");

        Discovery.GetSnapshot("dev").Should().BeSameAs(snapshot);
        Text(cut, "bd-pages-count").Should().Be("1");
    }
}
