using AngleSharp.Dom;
using BirkNext.BrowserCompanion;
using BirkNext.Web.Components;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Moq;
using Settings = BirkNext.Web.Components.FrontendAnalysisSettings;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// What Browser Discovery is for, and what it is not.
///
/// It observes and records raw browser evidence. Frontend Quality Review interprets it. Everything in this file
/// defends that one boundary, in the three places it used to leak:
/// <list type="bullet">
/// <item>Wording — a Browser Companion check that "flagged" something is a source outcome, not a failed success
/// criterion; a check that could not decide is a source uncertainty, not a manual-review obligation.</item>
/// <item>Depth — Pages summarizes one page, Evidence explores its observations. The rule catalogue inlined into
/// both made Pages a second, worse Frontend Quality Review.</item>
/// <item>Ownership — the HTTPS inspection certificate belongs to Authentication and the profile reset belongs to
/// General. A page-wide maintenance footer offered both of them here, where neither is owned.</item>
/// </list>
/// </summary>
public sealed class BrowserDiscoveryScopeTests : BunitContext
{
    private const string Origin = "https://application.example.test";
    private readonly FrontendAnalysisProfile _profile = new() { Id = "dev", Name = "Dev", TargetUrl = Origin };
    private IEndpointDiscoveryService Discovery => Services.GetRequiredService<IEndpointDiscoveryService>();
    private static readonly DateTimeOffset Observed = new(2026, 9, 22, 8, 6, 10, TimeSpan.Zero);
    private readonly List<BrowserCompanionRuntime> _runtimes = [];

    public BrowserDiscoveryScopeTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<IEndpointDiscoveryService, EndpointDiscoveryService>();
        Services.AddSingleton<IFrontendAnalysisSettingsService, FrontendAnalysisSettingsService>();
        Services.AddSingleton(Mock.Of<ITargetEnvironmentDetectionApiService>());
        JSInterop.Setup<string?>("birkNextStorage.getItem", _ => true).SetResult($$"""
            {"activeProfileId":"dev","profiles":[{"id":"dev","name":"Dev","targetUrl":"{{Origin}}"}]}
            """);
    }

    /// <summary>A page with the full spread: a finding, flagged and uncertain checks, and axe rules.</summary>
    private void Seed(string path = "/admin/user-access") => Discovery.GetSnapshot("dev").Pages.Add(new()
    {
        PageOrigin = Origin, PagePath = path,
        BrowserEvidence = new()
        {
            PageOrigin = Origin, PagePath = path, CapturedAt = Observed, VisitStartedAt = Observed.AddSeconds(-3),
            Dom = new() { NodeCount = 67, InteractiveCount = 14, FormControlCount = 1, HiddenFocusableCount = 2 },
            Accessibility = new()
            {
                Engine = "BirkNext Accessibility Checks",
                Findings = [new() { RuleId = "a11y-image-alt", Wcag = "1.1.1", Title = "Image without text alternative", Count = 3 }],
                Checks =
                [
                    new() { CheckId = "text-contrast", Outcome = "Fail", Tested = 18, Failed = 1 },
                    new() { CheckId = "language-parts", Outcome = "ManualReviewRequired", Tested = 4, Uncertain = 1 },
                    new() { CheckId = "a11y-page-title", Outcome = "Pass", Tested = 1 },
                ],
                Axe = new() { State = "Completed", Rules = [new() { RuleId = "aria-roles", Outcome = "Pass", CriterionIds = ["4.1.2"], Count = 5 }] },
            },
            Performance = new() { ObservationType = "spa-navigation", Cls = 0, StabilizationMs = 810 },
        }
    });

    private async Task<IRenderedComponent<BrowserDiscoveryTab>> OpenAsync(
        BrowserCompanionState state = BrowserCompanionState.Connected, string? currentPath = "/admin/user-access")
    {
        var api = new Mock<IBrowserCompanionApiService>();
        api.Setup(a => a.StatusAsync("dev", It.IsAny<CancellationToken>())).ReturnsAsync(new BrowserCompanionStatus
        {
            ProfileId = "dev", State = state, ApprovedOrigins = [Origin],
            CurrentPageOrigin = currentPath is null ? null : Origin, CurrentPagePath = currentPath,
        });
        var runtime = new BrowserCompanionRuntime(api.Object, Discovery, Services.GetRequiredService<IJSRuntime>());
        _runtimes.Add(runtime);
        await runtime.FollowAsync(_profile);
        return Render<BrowserDiscoveryTab>(p => p.Add(c => c.Profile, _profile).Add(c => c.Runtime, runtime));
    }

    private static string Text(IRenderedComponent<BrowserDiscoveryTab> cut, string id) =>
        cut.Find($"[data-testid={id}]").TextContent.Trim();
    private static IReadOnlyList<IElement> All(IRenderedComponent<BrowserDiscoveryTab> cut, string id) =>
        cut.FindAll($"[data-testid={id}]");
    private static void Nav(IRenderedComponent<BrowserDiscoveryTab> cut, string view) =>
        cut.Find($"[data-testid=browser-discovery-nav-{view}]").Click();

    /// <summary>What the reader sees: hidden disclosure bodies and closed &lt;details&gt; are not on the page.</summary>
    private static string Visible(IRenderedComponent<BrowserDiscoveryTab> cut)
    {
        var clone = (IElement)cut.Find("[data-testid=browser-discovery]").Clone(true);
        foreach (var collapsed in clone.QuerySelectorAll("[hidden], details:not([open])").ToList()) collapsed.Remove();
        return clone.TextContent;
    }

    // ── §42. Live session and historical evidence stay two answers ──────────────────────────

    // 1, 2, 3, 4, 5.
    [Fact]
    public async Task LiveSessionAndHistoricalEvidenceRemainSeparateAndNeitherDerivesTheOther()
    {
        Seed();
        Seed("/admin/general-roles");
        var cut = await OpenAsync();

        // Live: what the browser is doing now.
        Text(cut, "bd-session").Should().Be("Connected");
        Text(cut, "bd-live-pages").Should().Be("1");
        Text(cut, "bd-current-page").Should().Be(Origin + "/admin/user-access");

        // Historical: what was captured before. Two pages have evidence; only one of them is open.
        Text(cut, "bd-pages-count").Should().Be("2");
        Text(cut, "bd-evidence-dom").Should().Be("2 pages captured");

        Nav(cut, "pages");
        var states = All(cut, "browser-discovery-page-liveness").Select(e => e.TextContent.Trim()).ToList();
        states.Should().Contain("Live now").And.Contain("Historical evidence only");
        states.Count(s => s == "Live now").Should().Be(1, "captured evidence is never promoted to a live page");
    }

    // 5. Live DOM is about a content script that exists right now, whatever was captured before.
    [Fact]
    public async Task LiveDomIsNeverClaimedFromHistoricalEvidence()
    {
        Seed();
        var cut = await OpenAsync(BrowserCompanionState.Disconnected, currentPath: null);

        Text(cut, "bd-live-dom").Should().Be("Not available");
        Text(cut, "bd-content-script").Should().Be("Not available");
        // …and the evidence that was captured earlier is still there, unaffected.
        Text(cut, "bd-pages-count").Should().Be("1");
        Text(cut, "bd-evidence-dom").Should().Be("1 page captured");
    }

    // ── §43. Overview stays a summary ───────────────────────────────────────────────────────

    // 6, 7, 8, 9, 10.
    [Fact]
    public async Task OverviewStatesEvidencePresencePerPageAndNothingAboutItsContent()
    {
        Seed();
        var cut = await OpenAsync();

        var row = cut.Find("[data-testid=browser-discovery-page-row]");
        row.QuerySelectorAll("[data-testid=browser-discovery-area-badge]").Should().BeEmpty("mapping metadata is not a page status");
        row.TextContent.Should().Contain("Live now").And.Contain("Captured");

        // One hand-off, once.
        All(cut, "browser-discovery-handoff").Should().ContainSingle();
        cut.FindAll("a[href='/frontend-quality-review']").Should().ContainSingle();

        // And nothing that reads as a review result. The hand-off is allowed to name what Frontend Quality Review
        // produces — findings are exactly what it goes on to do with this evidence — so the evidence table is what
        // must stay free of review vocabulary.
        var visible = Visible(cut);
        foreach (var assessment in new[]
                 {
                     "requiring attention", "Needs attention", "Manual review required", "Failed", "Passed",
                     "Compliant", "Conformant",
                 })
            visible.Should().NotContain(assessment);

        var table = cut.Find("[data-testid=browser-discovery-overview-table]").TextContent;
        foreach (var verdict in new[] { "finding", "severity", "recommendation", "score", "grade" })
            table.Should().NotContain(verdict);
    }

    // ── §44, §45. Pages summarizes; raw outcomes are named as source outcomes ───────────────

    // 11–17, 20–25.
    [Fact]
    public async Task TheSelectedPageIsACompactSummaryInSourceVocabulary()
    {
        Seed();
        var cut = await OpenAsync();
        Nav(cut, "pages");

        Text(cut, "browser-discovery-selected-page").Should().Be("/admin/user-access");
        Text(cut, "browser-discovery-selected-liveness").Should().Be("Live now");
        Text(cut, "browser-discovery-page-observed").Should().Be(Observed.ToLocalTime().ToString("HH:mm:ss"));
        Text(cut, "browser-discovery-page-source").Should().Be("Browser Companion");
        Text(cut, "browser-discovery-page-dom-line").Should().Be("67 nodes · 14 interactive elements · 1 form control");
        Text(cut, "browser-discovery-page-performance-line").Should().Be("SPA navigation · 810 ms page stabilization");

        // 5 raw observations: 1 finding, 3 checks, 1 axe rule. The collector's own outcomes are named as its own.
        var accessibility = Text(cut, "browser-discovery-page-accessibility-line");
        accessibility.Should().Contain("5 raw checks observed");
        accessibility.Should().Contain("2 source flags").And.Contain("1 source uncertainty");
        accessibility.Should().NotContain("flagged").And.NotContain("uncertain ");

        // A source flag is not a failed criterion, and a source uncertainty is not a review obligation.
        var detail = cut.Find(".bd-pagedetail").TextContent;
        foreach (var conclusion in new[]
                 {
                     "Failed criterion", "Failed", "Manual review required", "requiring attention",
                     "Needs attention", "Violation", "Compliant",
                 })
            detail.Should().NotContain(conclusion);
    }

    // 18, 19.
    [Fact]
    public async Task ViewRawEvidenceGoesToTheExplorerAndOpenFrontendQualityReviewGoesToTheReview()
    {
        Seed();
        var cut = await OpenAsync();
        Nav(cut, "pages");

        cut.Find("[data-testid=browser-discovery-open-review-page]").GetAttribute("href")
            .Should().Be("/frontend-quality-review");

        cut.Find("[data-testid=browser-discovery-page-raw]").Click();
        cut.Find("[data-testid=browser-discovery-nav-evidence]").GetAttribute("aria-selected").Should().Be("true");
        cut.Find("[data-testid=browser-discovery-evidence-dom-row]").TextContent.Should().Contain("67");
    }

    // ── §46. The explorer keeps every raw observation and adds no verdict ───────────────────

    // 26–32.
    [Fact]
    public async Task TheEvidenceExplorerFiltersRawObservationsWithoutConcludingAnything()
    {
        Seed();
        Seed("/admin/general-roles");
        var cut = await OpenAsync();
        Nav(cut, "evidence");
        cut.Find("[data-testid=browser-discovery-evidence-nav-accessibility]").Click();

        // Every column the raw evidence carries, including the mapping it was grouped by.
        var table = cut.Find("[data-testid=browser-discovery-evidence-accessibility-table]");
        table.QuerySelectorAll("thead th").Select(h => h.TextContent.Trim())
            .Should().Equal("Page", "WCAG area", "Criterion mapping", "Evidence item", "Raw observation", "Observed at");
        table.TextContent.Should().Contain("1.1.1").And.Contain("Perceivable");

        // The outcome filter narrows by what the collector reported, and says so.
        cut.Find("[data-testid=browser-discovery-filter-state]").GetAttribute("value").Should().Be("non-neutral");
        cut.Find("[data-testid=browser-discovery-filter-state]").QuerySelectorAll("option")
            .Select(o => o.TextContent.Trim()).Should().Equal(
                "Source flags and uncertainties", "All observations", "Source flags", "Source uncertainties", "Other observations");

        cut.Find("[data-testid=browser-discovery-filter-state]").Change("flagged");
        All(cut, "browser-discovery-evidence-a11y-row").Should().NotBeEmpty();
        foreach (var row in All(cut, "browser-discovery-evidence-a11y-row"))
            row.TextContent.Should().Contain("source flag");

        // The page filter narrows to one page, and the area filter to one principle.
        cut.Find("[data-testid=browser-discovery-filter-state]").Change("all");
        cut.Find("[data-testid=browser-discovery-filter-page]").Change(Origin + "/admin/user-access");
        All(cut, "browser-discovery-evidence-a11y-row").Should().OnlyContain(r => r.TextContent.Contains("/admin/user-access"));
        cut.Find("[data-testid=browser-discovery-filter-area]").Change("Perceivable");
        All(cut, "browser-discovery-evidence-a11y-row").Should().OnlyContain(r => r.TextContent.Contains("Perceivable"));

        // Nothing here is a conclusion.
        cut.Markup.Should().NotContainAny("Failed criterion", "Manual review required", "Needs attention", "severity");
    }

    // ── §48. Performance stays raw ──────────────────────────────────────────────────────────

    // 37, 38, 39, 40, 41.
    [Fact]
    public async Task UnobservedMetricsStayUnobservedAndNoThresholdIsApplied()
    {
        Seed();
        var cut = await OpenAsync();
        Nav(cut, "evidence");
        cut.Find("[data-testid=browser-discovery-evidence-nav-performance]").Click();

        var row = cut.Find("[data-testid=browser-discovery-evidence-performance-row]");
        row.TextContent.Should().Contain("SPA navigation").And.Contain("810 ms");
        // Resources and transfer were never reported for this page: absent, never zero.
        row.QuerySelectorAll("[data-testid=bd-not-observed]").Should().NotBeEmpty();
        row.TextContent.Should().NotContain("0 B");

        var table = cut.Find("[data-testid=browser-discovery-evidence-performance-table]").TextContent;
        table.Should().NotContainAny("threshold", "budget", "Good", "Needs improvement", "Poor", "Passed", "Failed");
        table.Should().Contain("never as zero");
    }

    // ── §49. Pairing controls follow the situation ──────────────────────────────────────────

    // 42, 43, 44, 45, 46, 47.
    [Theory]
    [InlineData(BrowserCompanionState.NotPaired, false)]
    [InlineData(BrowserCompanionState.Disconnected, true)]
    [InlineData(BrowserCompanionState.Connected, false)]
    public async Task SetupOpensItselfOnlyForTheSituationWhoseRecoveryActionItHolds(BrowserCompanionState state, bool expanded)
    {
        Seed();
        var cut = await OpenAsync(state, currentPath: state == BrowserCompanionState.Connected ? "/admin/user-access" : null);

        cut.Find("[data-testid=browser-discovery-companion-setup-toggle]").GetAttribute("aria-expanded")
            .Should().Be(expanded.ToString().ToLowerInvariant());

        // Pair again and Unpair are setup controls, never primary ones.
        foreach (var control in new[] { "browser-companion-repair", "browser-companion-unpair" })
            foreach (var element in All(cut, control))
                element.Closest("[data-testid=browser-discovery-companion-setup-body]").Should().NotBeNull(control);

        // The developer install is one disclosure further in, and closed.
        var install = cut.Find("[data-testid=browser-companion-install]");
        install.TagName.Should().Be("DETAILS");
        install.HasAttribute("open").Should().BeFalse();
        install.QuerySelector("summary")!.TextContent.Trim().Should().Be("Developer extension setup");
        install.Closest("[data-testid=browser-discovery-companion-setup-body]").Should().NotBeNull();

        // Whatever the session is doing, the evidence captured before it is still listed.
        Text(cut, "bd-pages-count").Should().Be("1");
        All(cut, "browser-discovery-page-row").Should().ContainSingle();
    }

    // 42. Nothing paired: the one prominent action is to pair, and it is not inside setup.
    [Fact]
    public async Task NotPairedOffersOneProminentPairActionOutsideSetup()
    {
        var cut = await OpenAsync(BrowserCompanionState.NotPaired, currentPath: null);

        var pair = cut.Find("[data-testid=browser-discovery-pair]");
        pair.TextContent.Trim().Should().Be("Pair Browser Companion");
        pair.Closest("[data-testid=browser-discovery-companion-setup-body]").Should().BeNull();
        All(cut, "browser-companion-pair").Should().BeEmpty("one problem gets one action");
    }

    // ── §50. Browser Discovery owns only its own setup and maintenance ─────────────────────

    // 48, 49, 50, 51, 52.
    [Fact]
    public void CertificateAndProfileMaintenanceBelongToTheirOwnPanesAndNotToBrowserDiscovery()
    {
        JSInterop.SetupVoid("birkNextStorage.setItem", _ => true).SetVoidResult();
        var settings = Render<Settings>();
        settings.FindAll(".fa-profile-chip").Single(b => b.TextContent.Contains("Dev")).Click();

        void OpenTab(string label) => settings.FindAll("[role=tab]").Single(t => t.TextContent.Trim() == label).Click();
        IReadOnlyList<string> Buttons() => settings.FindAll("button").Select(b => b.TextContent.Trim()).ToList();

        // 48, 49, 52. Browser Discovery offers no destructive action of any kind.
        OpenTab("Browser Discovery");
        settings.FindAll("[data-testid=profile-advanced]").Should().BeEmpty();
        Buttons().Should().NotContain("Reset Profile").And.NotContain("Remove test certificate");

        // 51. The profile reset lives on General, which owns the profile.
        OpenTab("General");
        settings.FindAll("[data-testid=profile-advanced]").Should().ContainSingle();
        settings.Find("[data-testid=profile-advanced-toggle]").Click();
        settings.Find("[data-testid=advanced-reset-profile]").TextContent.Should().Contain("Reset Target Environment profile");
        settings.FindAll("[data-testid=advanced-remove-certificate]").Should().BeEmpty("the certificate is Authentication's");

        // 50. Endpoint Discovery owns neither either.
        OpenTab("Endpoint Discovery");
        settings.FindAll("[data-testid=profile-advanced]").Should().BeEmpty();
    }

    // ── §51. Density: the default view is a summary, the explorer is the deep surface ───────

    // 53, 54, 55, 56, 57.
    [Fact]
    public async Task TheDefaultSurfacesAreCompactAndTheDeepEvidenceIsOneClickAway()
    {
        Seed();
        var cut = await OpenAsync();

        // 57. One connection card, not two: the Live session strip owns the state.
        All(cut, "browser-companion-state").Should().BeEmpty();
        Visible(cut).Should().NotContain("Collecting browser evidence");

        // 53. Pages carries no observation rows at all by default.
        Nav(cut, "pages");
        cut.FindAll(".bd-pagedetail .bd-obslist li").Should().BeEmpty();
        cut.FindAll(".bd-pagedetail .disclosure-toggle").Should().BeEmpty("a page summary has nothing left to disclose");

        // 54. Evidence remains the full raw explorer.
        Nav(cut, "evidence");
        cut.Find("[data-testid=browser-discovery-evidence-nav-accessibility]").Click();
        cut.Find("[data-testid=browser-discovery-evidence-all-rules-toggle]").Click();
        cut.Find("[data-testid=browser-discovery-evidence-all-rules-table]").TextContent
            .Should().Contain("aria-roles").And.Contain("a11y-page-title");
    }

    // ── §30. The boundary is stated once per view ───────────────────────────────────────────

    [Fact]
    public async Task TheBoundarySentenceIsNotRepeatedAfterEverySection()
    {
        Seed();
        var cut = await OpenAsync();

        foreach (var view in new[] { "overview", "pages", "evidence" })
        {
            Nav(cut, view);
            var handoffs = cut.FindAll("a[href='/frontend-quality-review']");
            handoffs.Count.Should().BeLessThanOrEqualTo(1, $"{view} states the hand-off once");
        }
    }
}
