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
/// Browser Discovery → Pages answers one page at a time, compactly: which page, whether it is live, when it was
/// observed, and one line per evidence type. The individual checks, axe rules and per-principle groupings are the
/// Evidence explorer's — they used to be inlined here as well, which made a selected page several screens long and
/// turned a page summary into a rule catalogue.
///
/// None of it is an assessment. A "source flag" is the Browser Companion marking something, never a failed success
/// criterion; a "source uncertainty" is the collector being unable to decide, never a manual-review obligation. No
/// count here is a score, a rate or a conformance statement — Frontend Quality Review owns that.
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

    /// <summary>Into the raw explorer the way the reader gets there: the selected page's own action.</summary>
    private static void OpenRawEvidence(IRenderedComponent<BrowserDiscoveryTab> cut, string type = "accessibility")
    {
        cut.Find("[data-testid=browser-discovery-page-raw]").Click();
        cut.Find($"[data-testid=browser-discovery-evidence-nav-{type}]").Click();
    }

    // ── §34. The selected page states itself first ───────────────────────────

    // 1, 2, 3, 4, 5, 6.
    [Fact]
    public void SelectedPageLeadsWithRouteObservationTimeSourceAndEvidenceAvailability()
    {
        SeedRichPage();
        var cut = OpenPages();

        Text(cut, "browser-discovery-selected-page").Should().Be("/admin/child-specific-roles");
        Text(cut, "browser-discovery-page-observed").Should().Be(BrowserDiscoveryPresentation.EvidenceTimestamp(Observed, DateTimeOffset.Now));
        Text(cut, "browser-discovery-page-source").Should().Be("Browser Companion");

        // Stored evidence was captured in the past; "Available" is the live session's word.
        Text(cut, "browser-discovery-page-dom-state").Should().Be("Captured");
        Text(cut, "browser-discovery-page-accessibility-state").Should().Be("Captured");
        Text(cut, "browser-discovery-page-performance-state").Should().Be("Captured");
        Text(cut, "browser-discovery-selected-liveness").Should().Be("Historical evidence only");

        // One compact line per type: raw counts in the collector's own terms, no verdict and no rule list.
        Text(cut, "browser-discovery-page-dom-line").Should().Be("85 nodes · 17 interactive elements · 3 form controls");
        Text(cut, "browser-discovery-page-accessibility-line")
            .Should().Contain("11 raw checks observed").And.Contain("4 source-reported flags").And.Contain("2 source-reported uncertainties");
        Text(cut, "browser-discovery-page-performance-line").Should().Be("SPA navigation · 813 ms page stabilization");
    }

    /// <summary>Unavailable evidence says so. A zero would read as a measurement that was taken.</summary>
    [Fact]
    public void EvidenceThatWasNeverObservedIsUnavailableRatherThanZero()
    {
        SeedOtherPage();
        var cut = OpenPages();

        Text(cut, "browser-discovery-page-performance-state").Should().Be("Not captured");
        Text(cut, "browser-discovery-page-performance-line").Should().BeEmpty("a line would imply a measurement");
        cut.FindAll("[data-testid=browser-discovery-page-performance]").Should().BeEmpty();
    }

    // ── §35. The accessibility summary is derived, not decorative ────────────

    // 7, 8, 9, 10.
    [Fact]
    public void AccessibilitySummaryCountsPrinciplesEvaluatedFlaggedAndUncertain()
    {
        SeedRichPage();
        var cut = OpenPages();

        // 11 distinct observations: 1 finding + 5 checks + 5 axe rules. navigation-structure relates to two
        // principles and is still one observation.
        var line = Text(cut, "browser-discovery-page-accessibility-line");
        line.Should().Contain("11 raw checks observed");
        // a11y-image-alt, text-contrast, a11y-hidden-focusable, axe color-contrast.
        line.Should().Contain("4 source-reported flags");
        // language-parts, navigation-structure.
        line.Should().Contain("2 source-reported uncertainties");
        // The WCAG-area count is mapping metadata and belongs to the explorer, not to a page summary.
        line.Should().NotContain("WCAG area");
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
        overview.NonNeutral.Should().ContainSingle();
        overview.Principles.Should().HaveCount(2, "it is evidence about two principles");
        overview.Principles.Sum(p => p.Observations.Count).Should().Be(2);
    }

    // 11.
    [Fact]
    public void AnAssessmentThatFlaggedNothingIsStillEvidence()
    {
        SeedCleanPage();
        var cut = OpenPages();

        Text(cut, "browser-discovery-page-accessibility-state").Should().Be("Captured");
        var line = Text(cut, "browser-discovery-page-accessibility-line");
        line.Should().Be("3 raw checks observed", "an assessment that flagged nothing is still evidence");
        line.Should().NotContain("source flag").And.NotContain("source uncertaint");
    }

    // 12 (and §40).
    [Fact]
    public void NothingOnTheSelectedPageReadsAsAnAssessment()
    {
        SeedRichPage();
        var cut = OpenPages();

        var markup = cut.Find(".bd-pagedetail").TextContent;
        foreach (var verdict in new[]
                 {
                     "WCAG compliant", "Compliant", "Conformant", "Conformance", "Passed", "Failed",
                     "Page health", "Performance passed", "Accessibility passed", "score", "grade", "pass rate",
                     // The wording this pass removed: a raw source outcome is not a review obligation.
                     "requiring attention", "Needs attention", "Manual review required",
                 })
            markup.Should().NotContain(verdict);

        cut.Find("[data-testid=browser-discovery-open-review-page]").TextContent
            .Should().Contain("Open Frontend Quality Review");
    }

    // ── §36. The deep detail moved to the Evidence explorer ─────────────────

    /// <summary>
    /// The rule catalogue, the per-principle grouping and the automated-rule block are not on Pages at all any more —
    /// not even collapsed. Pages summarizes a page; Evidence explores its observations.
    /// </summary>
    [Fact]
    public void TheRuleCatalogueIsNotOnPagesAtAll()
    {
        SeedRichPage();
        var cut = OpenPages();

        var detail = cut.Find(".bd-pagedetail").TextContent;
        detail.Should().NotContain("definition-list").And.NotContain("dlitem").And.NotContain("aria-meter-name");
        detail.Should().NotContain("axe · aria-roles").And.NotContain("text-contrast");

        foreach (var absent in new[]
                 {
                     "browser-discovery-a11y-counts", "browser-discovery-a11y-attention", "browser-discovery-a11y-principles",
                     "browser-discovery-a11y-automated", "browser-discovery-a11y-rules",
                     "browser-discovery-area-perceivable", "browser-discovery-page-dom", "browser-discovery-page-performance",
                 })
            cut.FindAll($"[data-testid={absent}]").Should().BeEmpty(absent);
    }

    /// <summary>Compact must not mean lost: every one of those observations is one click away, in the explorer.</summary>
    [Fact]
    public void TheSamePageEvidenceIsReachableThroughViewRawEvidence()
    {
        SeedRichPage();
        var cut = OpenPages();

        OpenRawEvidence(cut);

        var table = cut.Find("[data-testid=browser-discovery-evidence-all-rules-table]").TextContent;
        foreach (var observation in new[] { "text-contrast", "color-contrast", "definition-list", "dlitem", "aria-meter-name" })
            table.Should().Contain(observation);
        // And it arrives scoped to the page the reader was looking at.
        cut.Find("[data-testid=browser-discovery-filter-page]").GetAttribute("value")
            .Should().Contain("/admin/child-specific-roles");
    }

    /// <summary>
    /// The heading "Observed items requiring attention" is gone, and so is every other word that turns a collector
    /// outcome into a reviewer's obligation. What the collector reported is named as what it is.
    /// </summary>
    [Fact]
    public void RawSourceOutcomesAreNamedAsSourceOutcomesAndNeverAsReviewObligations()
    {
        SeedRichPage();
        var cut = OpenPages();
        OpenRawEvidence(cut);

        var markup = cut.Markup;
        foreach (var assessment in new[]
                 {
                     "Observed items requiring attention", "requiring attention", "Needs attention",
                     "Manual review required", "Failed criterion", "Failed", "Violation",
                 })
            markup.Should().NotContain(assessment);

        Text(cut, "browser-discovery-evidence-a11y-counts")
            .Should().Contain("source-reported flags").And.Contain("source-reported uncertainties");
        // One boundary statement, not two saying the same thing: the intro names the raw status and the interpreter.
        Text(cut, "browser-discovery-evidence-a11y-intro")
            .Should().Contain("not WCAG findings or compliance results").And.Contain("Frontend Quality Review interprets them");
        cut.FindAll("[data-testid=browser-discovery-raw-outcome-disclaimer]").Should().BeEmpty();
    }

    [Fact]
    public void SelectingAnotherPageReplacesTheCompactSummary()
    {
        SeedRichPage();
        SeedOtherPage();
        var cut = OpenPages();

        SelectPage(cut, "/admin/general-roles");
        Text(cut, "browser-discovery-selected-page").Should().Be("/admin/general-roles");
        Text(cut, "browser-discovery-page-accessibility-line").Should().Be("1 raw check observed");
        Text(cut, "browser-discovery-page-dom-line").Should().Contain("240 nodes");

        SelectPage(cut, "/admin/child-specific-roles");
        Text(cut, "browser-discovery-selected-page").Should().Be("/admin/child-specific-roles");
        Text(cut, "browser-discovery-page-dom-line").Should().Contain("85 nodes");
    }

    // ── §37. DOM ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The DOM block on Pages is three counts. Everything else — landmarks, headings, duplicate ids, hidden
    /// focusable elements — is structural detail and lives in Evidence → DOM, behind its own disclosure.
    /// </summary>
    [Fact]
    public void PagesSummarisesDomAndTheStructuralDetailLivesInTheExplorer()
    {
        SeedRichPage();
        var cut = OpenPages();

        var line = Text(cut, "browser-discovery-page-dom-line");
        line.Should().Be("85 nodes · 17 interactive elements · 3 form controls");
        cut.Find(".bd-pagedetail").TextContent.Should().NotContain("Landmarks").And.NotContain("Duplicate ids");

        cut.Find("[data-testid=browser-discovery-page-raw]").Click();
        var row = cut.Find("[data-testid=browser-discovery-evidence-dom-row]").TextContent;
        row.Should().Contain("85").And.Contain("17");
        cut.Find("[data-testid=browser-discovery-evidence-dom-extra-row]").TextContent.Should().Contain("14", "depth stays visible under its page");
        // A structural count is a structural count: nothing here is styled or worded as a defect.
        var table = cut.Find("[data-testid=browser-discovery-evidence-dom-table]");
        table.QuerySelectorAll("thead th").Select(h => h.TextContent.Trim())
            .Should().Contain("Hidden focusable elements");
        table.QuerySelector("tbody")!.TextContent.Should().NotContainAny("Passed", "Failed", "defect", "issue");
        table.QuerySelector("caption")!.TextContent.Should().Contain("they are not findings or pass/fail results");
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

        // The compact line: how the page was observed, and the one timing that describes the visit.
        Text(cut, "browser-discovery-page-performance-line").Should().Be("SPA navigation · 813 ms page stabilization");
        cut.Find(".bd-pagedetail").TextContent
            .Should().NotContainAny("threshold exceeded", "within budget", "Good", "Needs improvement", "Poor");

        // The raw field measurements, unchanged, in the explorer that owns them.
        cut.Find("[data-testid=browser-discovery-page-raw]").Click();
        cut.Find("[data-testid=browser-discovery-evidence-nav-performance]").Click();
        var row = cut.Find("[data-testid=browser-discovery-evidence-performance-row]").TextContent;
        row.Should().Contain("SPA navigation").And.Contain("813 ms");
        // A metric the browser never reported stays Not observed, never zero.
        row.Should().Contain("Not observed");
        cut.Find("[data-testid=browser-discovery-evidence-performance-table]").TextContent
            .Should().NotContainAny("threshold", "budget", "Good", "Needs improvement", "Poor");
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
        // And it is setup, not a second status card: a collapsed disclosure holding pairing and its controls.
        cut.Find("[data-testid=browser-discovery-companion-setup-toggle]").GetAttribute("aria-expanded").Should().Be("false");
        cut.FindAll("[data-testid=browser-companion-details]").Should().ContainSingle();
        Text(cut, "browser-companion-origins").Should().Contain(Origin);
    }

    // ── §23. The default view stays readable ─────────────────────────────────

    [Fact]
    public void TheDefaultSelectedPageViewIsASummaryNotACatalogue()
    {
        SeedRichPage();
        var cut = OpenPages();

        // No observation rows at all: the catalogue is not collapsed here, it is elsewhere.
        cut.FindAll(".bd-pagedetail .bd-obslist li").Should().BeEmpty();

        // What the reader does see: the page, whether it is live, when it was observed, and one line per type.
        var visible = VisibleDetail(cut);
        visible.Should().Contain("/admin/child-specific-roles");
        visible.Should().Contain("Captured");
        visible.Should().Contain("11 raw checks observed");
        visible.Should().Contain("85 nodes");
        visible.Should().Contain("SPA navigation");
        visible.Should().Contain("View raw evidence");
        visible.Should().Contain("Open Frontend Quality Review");

        // And the boundary sentence is not repeated after every block; Pages carries the action, not a paragraph.
        visible.Should().NotContain("WCAG areas and criterion references show how raw evidence is mapped");
    }

}
