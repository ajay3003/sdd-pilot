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

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// Browser Discovery → Pages is an evidence browser, not a rule catalogue. The selected page leads with what it is,
/// what evidence exists and what was flagged; the individual checks and axe rules stay behind progressive disclosure.
///
/// None of it is an assessment. "Flagged" and "uncertain" describe what the collector observed, never a failed success
/// criterion, and no count here is a score, a rate or a conformance statement — Frontend Quality Review owns that.
/// </summary>
public sealed class BrowserDiscoveryPagesDensityTests : BunitContext
{
    private const string Origin = "https://application.example.test";
    private readonly FrontendAnalysisProfile _profile = new() { Id = "dev", Name = "Dev", TargetUrl = Origin };
    private IEndpointDiscoveryService Discovery => Services.GetRequiredService<IEndpointDiscoveryService>();
    private static readonly DateTimeOffset Observed = new(2026, 9, 21, 10, 3, 41, TimeSpan.Zero);

    public BrowserDiscoveryPagesDensityTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<IEndpointDiscoveryService, EndpointDiscoveryService>();
        Services.AddSingleton<IFrontendAnalysisSettingsService, FrontendAnalysisSettingsService>();
        Services.AddSingleton(Mock.Of<ITargetEnvironmentDetectionApiService>());
        JSInterop.Setup<string?>("birkNextStorage.getItem", _ => true).SetResult($$"""
            {"activeProfileId":"dev","profiles":[{"id":"dev","name":"Dev","targetUrl":"{{Origin}}"}]}
            """);
    }

    /// <summary>
    /// A page carrying the full spread: flagged findings and checks, uncertain checks, a check that maps to two
    /// principles, automated rules that matched nodes, and automated rules that ran and matched nothing.
    /// </summary>
    private void SeedRichPage(string path = "/admin/child-specific-roles") => Discovery.GetSnapshot("dev").Pages.Add(new()
    {
        PageOrigin = Origin, PagePath = path,
        BrowserEvidence = new()
        {
            PageOrigin = Origin, PagePath = path, CapturedAt = Observed, VisitStartedAt = Observed.AddSeconds(-4),
            Dom = new()
            {
                NodeCount = 85, MaxDepth = 14, InteractiveCount = 17, FormControlCount = 3, ImageCount = 1,
                IframeCount = 0, DialogCount = 0, DuplicateIdCount = 2, HiddenFocusableCount = 1,
                Landmarks = new() { ["banner"] = 1, ["main"] = 1, ["navigation"] = 2 },
                HeadingCounts = new() { ["h1"] = 1, ["h2"] = 1 },
            },
            Accessibility = new()
            {
                Engine = "BirkNext Accessibility Checks",
                Findings = [new() { RuleId = "a11y-image-alt", Wcag = "1.1.1", Title = "Image without text alternative", Count = 3 }],
                Checks =
                [
                    new() { CheckId = "text-contrast", Outcome = "Fail", Tested = 18, Failed = 1 },
                    new() { CheckId = "a11y-hidden-focusable", Outcome = "Fail", Tested = 6, Failed = 1 },
                    new() { CheckId = "language-parts", Outcome = "ManualReviewRequired", Tested = 4, Uncertain = 1 },
                    // Supports 2.4.5 and 3.2.3: one observation, two principles.
                    new() { CheckId = "navigation-structure", Outcome = "ManualReviewRequired", Tested = 2, Uncertain = 1 },
                    new() { CheckId = "a11y-page-title", Outcome = "Pass", Tested = 1 },
                ],
                Axe = new()
                {
                    State = "Completed",
                    Rules =
                    [
                        new() { RuleId = "color-contrast", Outcome = "Fail", CriterionIds = ["1.4.3"], Count = 2 },
                        new() { RuleId = "aria-roles", Outcome = "Pass", CriterionIds = ["4.1.2"], Count = 5 },
                        new() { RuleId = "definition-list", Outcome = "Pass", CriterionIds = ["1.3.1"], Count = 0 },
                        new() { RuleId = "dlitem", Outcome = "Pass", CriterionIds = ["1.3.1"], Count = 0 },
                        new() { RuleId = "aria-meter-name", Outcome = "NotApplicable", CriterionIds = ["4.1.2"], Count = 0 },
                    ]
                }
            },
            Performance = new() { ObservationType = "spa-navigation", Cls = 0, StabilizationMs = 813 },
        }
    });

    /// <summary>A second page with different evidence, so switching pages can be shown to actually change the detail.</summary>
    private void SeedOtherPage(string path = "/admin/general-roles") => Discovery.GetSnapshot("dev").Pages.Add(new()
    {
        PageOrigin = Origin, PagePath = path,
        BrowserEvidence = new()
        {
            PageOrigin = Origin, PagePath = path, CapturedAt = Observed.AddMinutes(-3), VisitStartedAt = Observed.AddMinutes(-3),
            Dom = new() { NodeCount = 240, MaxDepth = 9, InteractiveCount = 4, FormControlCount = 0 },
            Accessibility = new()
            {
                Engine = "BirkNext Accessibility Checks",
                Checks = [new() { CheckId = "a11y-page-title", Outcome = "Pass", Tested = 1 }],
            },
        }
    });

    /// <summary>An assessment that ran and flagged nothing at all. Still evidence.</summary>
    private void SeedCleanPage(string path = "/") => Discovery.GetSnapshot("dev").Pages.Add(new()
    {
        PageOrigin = Origin, PagePath = path,
        BrowserEvidence = new()
        {
            PageOrigin = Origin, PagePath = path, CapturedAt = Observed.AddMinutes(-5), VisitStartedAt = Observed.AddMinutes(-5),
            Dom = new() { NodeCount = 40 },
            Accessibility = new()
            {
                Engine = "BirkNext Accessibility Checks",
                Checks = [new() { CheckId = "a11y-page-title", Outcome = "Pass", Tested = 1 },
                          new() { CheckId = "a11y-document-lang", Outcome = "Pass", Tested = 1 }],
                Axe = new() { State = "Completed", Rules = [new() { RuleId = "aria-roles", Outcome = "Pass", CriterionIds = ["4.1.2"], Count = 3 }] }
            },
        }
    });

    private readonly List<BrowserCompanionRuntime> _runtimes = [];

    private IRenderedComponent<BrowserDiscoveryTab> OpenPages()
    {
        var api = new Mock<IBrowserCompanionApiService>();
        api.Setup(a => a.StatusAsync("dev", It.IsAny<CancellationToken>())).ReturnsAsync(new BrowserCompanionStatus
        {
            ProfileId = "dev", State = BrowserCompanionState.Connected, ApprovedOrigins = [Origin],
            CurrentPageOrigin = Origin, CurrentPagePath = "/admin/operations",
        });
        var runtime = new BrowserCompanionRuntime(api.Object, Discovery, Services.GetRequiredService<IJSRuntime>());
        _runtimes.Add(runtime);
        var cut = Render<BrowserDiscoveryTab>(p => p.Add(c => c.Profile, _profile).Add(c => c.Runtime, runtime));
        cut.Find("[data-testid=browser-discovery-nav-pages]").Click();
        return cut;
    }

    private static string Text(IRenderedComponent<BrowserDiscoveryTab> cut, string id) =>
        cut.Find($"[data-testid={id}]").TextContent.Trim();

    /// <summary>A collapsed disclosure keeps its body in the DOM but out of rendering, focus order and the a11y tree.</summary>
    private static bool IsCollapsed(IRenderedComponent<BrowserDiscoveryTab> cut, string testId) =>
        cut.Find($"[data-testid={testId}-body]").HasAttribute("hidden");

    private static void Toggle(IRenderedComponent<BrowserDiscoveryTab> cut, string testId) =>
        cut.Find($"[data-testid={testId}-toggle]").Click();

    /// <summary>What the reader actually sees: collapsed disclosures are not part of the visible surface.</summary>
    private static string VisibleText(IElement element)
    {
        var clone = (IElement)element.Clone(true);
        foreach (var collapsed in clone.QuerySelectorAll("[hidden]").ToList()) collapsed.Remove();
        return clone.TextContent;
    }

    private static string VisibleDetail(IRenderedComponent<BrowserDiscoveryTab> cut) =>
        VisibleText(cut.Find(".bd-pagedetail"));

    private static void SelectPage(IRenderedComponent<BrowserDiscoveryTab> cut, string route) =>
        cut.FindAll("[data-testid=browser-discovery-page-link]")
            .First(b => b.TextContent.Contains(route)).Click();

    // ── §34. The selected page states itself first ───────────────────────────

    // 1, 2, 3, 4, 5, 6.
    [Fact]
    public void SelectedPageLeadsWithRouteObservationTimeSourceAndEvidenceAvailability()
    {
        SeedRichPage();
        var cut = OpenPages();

        Text(cut, "browser-discovery-selected-page").Should().Be("/admin/child-specific-roles");
        Text(cut, "browser-discovery-page-observed").Should().Be(Observed.ToLocalTime().ToString("HH:mm:ss"));
        Text(cut, "browser-discovery-page-source").Should().Be("Browser Companion");

        Text(cut, "browser-discovery-page-dom-state").Should().Be("Available");
        Text(cut, "browser-discovery-page-accessibility-state").Should().Be("Available");
        Text(cut, "browser-discovery-page-performance-state").Should().Be("Available");
    }

    /// <summary>Unavailable evidence says so. A zero would read as a measurement that was taken.</summary>
    [Fact]
    public void EvidenceThatWasNeverObservedIsUnavailableRatherThanZero()
    {
        SeedOtherPage();
        var cut = OpenPages();

        Text(cut, "browser-discovery-page-performance-state").Should().Be("Unavailable");
        Text(cut, "browser-discovery-page-performance-empty").Should().Contain("No performance evidence");
        cut.FindAll("[data-testid=browser-discovery-page-performance]").Should().BeEmpty();
    }

    // ── §35. The accessibility summary is derived, not decorative ────────────

    // 7, 8, 9, 10.
    [Fact]
    public void AccessibilitySummaryCountsPrinciplesEvaluatedFlaggedAndUncertain()
    {
        SeedRichPage();
        var cut = OpenPages();

        var counts = Text(cut, "browser-discovery-a11y-counts");
        // Four principles: 1.x, 2.x, 3.x and 4.x all have evidence.
        counts.Should().Contain("4 WCAG area(s)");
        // 11 distinct observations: 1 finding + 5 checks + 5 axe rules. navigation-structure relates to two
        // principles and is still one observation.
        counts.Should().Contain("11 check(s) evaluated");
        // a11y-image-alt, text-contrast, a11y-hidden-focusable, axe color-contrast.
        counts.Should().Contain("4 flagged");
        // language-parts, navigation-structure.
        counts.Should().Contain("2 uncertain");
    }

    /// <summary>
    /// Per-principle counts include an observation under every principle it relates to, so they deliberately do not
    /// sum to the page total. The model documents this; the UI never presents the two as the same number.
    /// </summary>
    [Fact]
    public void AnObservationRelatedToTwoPrinciplesIsCountedOncePerPageAndOncePerPrinciple()
    {
        var accessibility = new BrowserAccessibilitySummary
        {
            Checks = [new() { CheckId = "navigation-structure", Outcome = "ManualReviewRequired", Tested = 2, Uncertain = 1 }]
        };

        var overview = BrowserDiscoveryPresentation.Accessibility(accessibility);

        overview.Evaluated.Should().Be(1, "it is one observation");
        overview.Uncertain.Should().Be(1);
        overview.Attention.Should().ContainSingle();
        overview.Principles.Should().HaveCount(2, "it is evidence about two principles");
        overview.Principles.Sum(p => p.Observations.Count).Should().Be(2);
    }

    // 11.
    [Fact]
    public void AnAssessmentThatFlaggedNothingIsStillEvidence()
    {
        SeedCleanPage();
        var cut = OpenPages();

        Text(cut, "browser-discovery-page-accessibility-state").Should().Be("Available");
        var counts = Text(cut, "browser-discovery-a11y-counts");
        counts.Should().Contain("3 check(s) evaluated");
        counts.Should().NotContain("flagged").And.NotContain("uncertain");
        cut.FindAll("[data-testid=browser-discovery-a11y-attention]").Should().BeEmpty();
        cut.FindAll("[data-testid=browser-discovery-page-areas-empty]").Should().BeEmpty();
    }

    // 12 (and §40).
    [Fact]
    public void NothingOnTheSelectedPageReadsAsAnAssessment()
    {
        SeedRichPage();
        var cut = OpenPages();
        // Expanded as well as collapsed: the forbidden vocabulary must not be hiding inside a disclosure either.
        Toggle(cut, "browser-discovery-principle-perceivable");
        Toggle(cut, "browser-discovery-a11y-rules");

        var markup = cut.Find(".bd-pagedetail").TextContent;
        foreach (var verdict in new[]
                 {
                     "WCAG compliant", "Compliant", "Conformant", "Conformance", "Passed", "Failed",
                     "Page health", "Performance passed", "Accessibility passed", "score", "grade", "pass rate",
                 })
            markup.Should().NotContain(verdict);

        Text(cut, "browser-discovery-page-handoff").Should().Contain("Frontend Quality Review interprets it");
    }

    // ── §36. Progressive disclosure ──────────────────────────────────────────

    // 13.
    [Fact]
    public void TheRuleCatalogueIsNotVisibleUntilTheReaderAsksForIt()
    {
        SeedRichPage();
        var cut = OpenPages();

        var visible = VisibleDetail(cut);
        // Individual axe rules and per-principle check rows are the bulk that made this page unreadable.
        visible.Should().NotContain("definition-list").And.NotContain("dlitem").And.NotContain("aria-meter-name");
        visible.Should().NotContain("axe · aria-roles");

        IsCollapsed(cut, "browser-discovery-principle-perceivable").Should().BeTrue();
        IsCollapsed(cut, "browser-discovery-principle-operable").Should().BeTrue();
        IsCollapsed(cut, "browser-discovery-principle-understandable").Should().BeTrue();
        IsCollapsed(cut, "browser-discovery-principle-robust").Should().BeTrue();
        IsCollapsed(cut, "browser-discovery-a11y-rules").Should().BeTrue();
    }

    // 14.
    [Fact]
    public void APrincipleStatesItsCountsCollapsedAndListsItsChecksWhenExpanded()
    {
        SeedRichPage();
        var cut = OpenPages();

        // Collapsed, it still says what it holds, so the reader can decide whether to open it.
        var perceivable = cut.Find("[data-testid=browser-discovery-area-perceivable]").TextContent;
        perceivable.Should().Contain("Perceivable").And.Contain("5 observed").And.Contain("3 flagged");

        Toggle(cut, "browser-discovery-principle-perceivable");

        IsCollapsed(cut, "browser-discovery-principle-perceivable").Should().BeFalse();
        var rows = cut.FindAll("[data-testid=browser-discovery-principle-perceivable-item]");
        rows.Select(r => r.TextContent).Should()
            .Contain(t => t.Contains("text-contrast"))
            .And.Contain(t => t.Contains("Image without text alternative"))
            .And.Contain(t => t.Contains("color-contrast"));
        // The criterion this listing sits under is shown, without claiming the criterion was assessed.
        rows.First().TextContent.Should().Contain("WCAG 1.4.3");
    }

    // 15, 22.
    [Fact]
    public void RulesThatRanAndMatchedNothingStayAvailableBehindTheirOwnDisclosure()
    {
        SeedRichPage();
        var cut = OpenPages();
        Toggle(cut, "browser-discovery-principle-perceivable");

        // Zero-element rules are not in the principle's primary rows…
        cut.FindAll("[data-testid=browser-discovery-principle-perceivable-item]")
            .Select(r => r.TextContent).Should().NotContain(t => t.Contains("definition-list"));
        // …they are one level further in, and they are still there.
        IsCollapsed(cut, "browser-discovery-principle-perceivable-evaluated").Should().BeTrue();
        Toggle(cut, "browser-discovery-principle-perceivable-evaluated");
        cut.FindAll("[data-testid=browser-discovery-principle-perceivable-evaluated-item]")
            .Select(r => r.TextContent).Should()
            .Contain(t => t.Contains("definition-list")).And.Contain(t => t.Contains("dlitem"));
    }

    // 15 (automated block).
    [Fact]
    public void AutomatedEvidenceIsSummarisedBeforeItsRuleListIsOffered()
    {
        SeedRichPage();
        var cut = OpenPages();

        Text(cut, "browser-discovery-a11y-automated-counts").Should().Contain("5 rule(s) evaluated").And.Contain("1 flagged");

        Toggle(cut, "browser-discovery-a11y-rules");
        var rules = cut.FindAll("[data-testid=browser-discovery-a11y-rule]").Select(r => r.TextContent).ToList();
        rules.Should().HaveCount(5);
        rules[0].Should().Contain("color-contrast").And.Contain("flagged");
    }

    // 16, and §6/§20/§21.
    [Fact]
    public void FlaggedAndUncertainObservationsComeBeforeAnyRuleList()
    {
        SeedRichPage();
        var cut = OpenPages();

        var attention = cut.Find("[data-testid=browser-discovery-a11y-attention]").TextContent;
        attention.Should().Contain("Observed items requiring attention");

        var items = cut.FindAll("[data-testid=browser-discovery-a11y-attention-item]").Select(i => i.TextContent).ToList();
        // Flagged first, most elements first; uncertain after. Six qualify, five are previewed.
        items.Should().HaveCount(5);
        items[0].Should().Contain("text-contrast").And.Contain("1 flagged");
        items[1].Should().Contain("a11y-hidden-focusable").And.Contain("1 flagged");
        items[4].Should().Contain("language-parts").And.Contain("1 uncertain");
        Text(cut, "browser-discovery-a11y-attention-more").Should().Contain("1 more");

        // And it is visible without opening anything.
        VisibleDetail(cut).Should().Contain("text-contrast").And.Contain("language-parts");
    }

    // 17, and §26.
    [Fact]
    public void SelectingAnotherPageReplacesTheDetailAndStartsFromTheCollapsedSummary()
    {
        SeedRichPage();
        SeedOtherPage();
        var cut = OpenPages();
        Toggle(cut, "browser-discovery-principle-perceivable");
        IsCollapsed(cut, "browser-discovery-principle-perceivable").Should().BeFalse();

        SelectPage(cut, "/admin/general-roles");

        Text(cut, "browser-discovery-selected-page").Should().Be("/admin/general-roles");
        Text(cut, "browser-discovery-a11y-counts").Should().Contain("1 check(s) evaluated");
        cut.Find("[data-testid=browser-discovery-page-dom]").TextContent.Should().Contain("240");
        // The previous page's open disclosure does not carry over.
        IsCollapsed(cut, "browser-discovery-principle-operable").Should().BeTrue();

        SelectPage(cut, "/admin/child-specific-roles");
        Text(cut, "browser-discovery-selected-page").Should().Be("/admin/child-specific-roles");
        IsCollapsed(cut, "browser-discovery-principle-perceivable").Should().BeTrue();
    }

    // ── §37. DOM ─────────────────────────────────────────────────────────────

    // 18, 19, 20, 21, 22, 23, 24.
    [Fact]
    public void DomEvidenceIsACompactBlockWithTheStructuralExtrasBehindDisclosure()
    {
        SeedRichPage();
        var cut = OpenPages();

        var dom = Text(cut, "browser-discovery-page-dom");
        dom.Should().Contain("Nodes").And.Contain("85");
        dom.Should().Contain("Maximum depth").And.Contain("14");
        dom.Should().Contain("Interactive elements").And.Contain("17");
        dom.Should().Contain("Form controls").And.Contain("3");
        dom.Should().Contain("Landmarks").And.Contain("banner ×1").And.Contain("navigation ×2");
        dom.Should().Contain("Headings").And.Contain("h1 ×1");

        // The signals the collector only reports when it saw them are one click away, and the button counts them.
        IsCollapsed(cut, "browser-discovery-page-dom-detail").Should().BeTrue();
        dom.Should().NotContain("Duplicate ids");
        Toggle(cut, "browser-discovery-page-dom-detail");
        var extra = Text(cut, "browser-discovery-page-dom-extra");
        extra.Should().Contain("Duplicate ids").And.Contain("2");
        extra.Should().Contain("Hidden focusable elements");
    }

    /// <summary>Compact must not mean lossy: everything the cross-page Evidence tab reports is still reachable.</summary>
    [Fact]
    public void TheCompactDomBlockAndItsDisclosureTogetherCoverAllDomEvidence()
    {
        var dom = new BrowserDomSummary
        {
            NodeCount = 85, MaxDepth = 14, DuplicateIdCount = 2, HiddenFocusableCount = 1, PositiveTabIndexCount = 4,
            Landmarks = new() { ["main"] = 1 }, HeadingCounts = new() { ["h1"] = 1 },
        };

        BrowserDiscoveryPresentation.DomPrimary(dom).Concat(BrowserDiscoveryPresentation.DomDetail(dom))
            .Select(i => i.Label).Should().BeEquivalentTo(BrowserDiscoveryPresentation.DomEvidence(dom).Select(i => i.Label));
        BrowserDiscoveryPresentation.DomDetail(dom).Select(i => i.Label).Should()
            .BeEquivalentTo(["Duplicate ids", "Hidden focusable elements", "Positive tabindex"]);
    }

    // ── §38. Performance ─────────────────────────────────────────────────────

    // 25, 26, 27, 28.
    [Fact]
    public void PerformanceEvidenceStaysRawObservationsWithNoThreshold()
    {
        SeedRichPage();
        var cut = OpenPages();

        var performance = Text(cut, "browser-discovery-page-performance");
        performance.Should().Contain("Cumulative layout shift").And.Contain("0");
        performance.Should().Contain("Page stabilization").And.Contain("813 ms");
        // Metrics the browser never reported are absent, not zero.
        performance.Should().NotContain("Largest contentful paint").And.NotContain("Time to first byte");

        var detail = cut.Find(".bd-pagedetail").TextContent;
        detail.Should().Contain("Observation: SPA navigation");
        detail.Should().Contain("Raw observations only; thresholds are applied in Frontend Quality Review.");
        detail.Should().NotContainAny("threshold exceeded", "within budget", "Good", "Needs improvement", "Poor");
    }

    // ── §39. Session management belongs to Overview ──────────────────────────

    // 29, 30, 31, 32.
    [Fact]
    public void PagesShowsEvidenceAndOverviewKeepsTheSession()
    {
        SeedRichPage();
        var cut = OpenPages();

        // The session is still stated, in the summary strip that every tab shares.
        Text(cut, "bd-session").Should().Be("Connected");
        Text(cut, "bd-current-page").Should().Be(Origin + "/admin/operations");
        // …but the card that owns pairing, approved origins and what to do about a silent session is not repeated here.
        cut.FindComponents<BrowserCompanionPanel>().Should().BeEmpty();

        cut.Find("[data-testid=browser-discovery-nav-overview]").Click();
        cut.FindComponents<BrowserCompanionPanel>().Should().ContainSingle();
        cut.FindAll("[data-testid=browser-companion-details]").Should().ContainSingle();
        Text(cut, "browser-companion-origins").Should().Contain(Origin);
    }

    // ── §23. The default view stays readable ─────────────────────────────────

    [Fact]
    public void TheDefaultSelectedPageViewIsASummaryNotACatalogue()
    {
        SeedRichPage();
        var cut = OpenPages();

        // Every observation row in the page detail is inside a collapsed disclosure by default.
        VisibleDetail(cut).Should().NotContain("axe · ");
        cut.FindAll(".bd-pagedetail .bd-obslist li").Count.Should().BeGreaterThan(5, "the detail exists");
        VisibleText(cut.Find(".bd-pagedetail")).Split("\n").Should().NotBeEmpty();

        // What the reader does see: the page, its evidence, its counts and what was flagged.
        var visible = VisibleDetail(cut);
        visible.Should().Contain("/admin/child-specific-roles");
        visible.Should().Contain("Available");
        visible.Should().Contain("11 check(s) evaluated");
        visible.Should().Contain("Observed items requiring attention");
        visible.Should().Contain("WCAG areas show which principles the observed evidence relates to");
    }

}
