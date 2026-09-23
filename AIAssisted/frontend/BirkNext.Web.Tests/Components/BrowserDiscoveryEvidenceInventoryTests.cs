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
/// Browser Discovery → Evidence is the cross-page inventory: which pages carry which evidence, and how their raw
/// observations compare. Pages investigates one page; Overview owns the session; this compares pages and nothing else.
///
/// It stays evidence-only. Counts are observations, "flagged" and "uncertain" are what the collector saw, and a
/// metric the browser never reported is absent rather than zero.
/// </summary>
public sealed class BrowserDiscoveryEvidenceInventoryTests : BunitContext
{
    private const string Origin = "https://application.example.test";
    private readonly FrontendAnalysisProfile _profile = new() { Id = "dev", Name = "Dev", TargetUrl = Origin };
    private IEndpointDiscoveryService Discovery => Services.GetRequiredService<IEndpointDiscoveryService>();
    private static readonly DateTimeOffset Observed = new(2026, 9, 21, 10, 9, 53, TimeSpan.Zero);

    public BrowserDiscoveryEvidenceInventoryTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<IEndpointDiscoveryService, EndpointDiscoveryService>();
        Services.AddSingleton<IFrontendAnalysisSettingsService, FrontendAnalysisSettingsService>();
        Services.AddSingleton(Mock.Of<ITargetEnvironmentDetectionApiService>());
        JSInterop.Setup<string?>("birkNextStorage.getItem", _ => true).SetResult($$"""
            {"activeProfileId":"dev","profiles":[{"id":"dev","name":"Dev","targetUrl":"{{Origin}}"}]}
            """);
    }

    /// <summary>The busy page: flagged and uncertain checks, an automated rule that matched, and rules that matched nothing.</summary>
    private void SeedRoles(string path = "/admin/child-specific-roles") => Discovery.GetSnapshot("dev").Pages.Add(new()
    {
        PageOrigin = Origin, PagePath = path,
        BrowserEvidence = new()
        {
            PageOrigin = Origin, PagePath = path, CapturedAt = Observed, VisitStartedAt = Observed.AddSeconds(-4),
            Dom = new()
            {
                NodeCount = 85, MaxDepth = 14, InteractiveCount = 17, FormControlCount = 3, ImageCount = 1,
                IframeCount = 0, DialogCount = 0, HiddenFocusableCount = 1,
                Landmarks = new() { ["main"] = 1, ["navigation"] = 1 }, HeadingCounts = new() { ["h1"] = 1, ["h2"] = 1 },
            },
            Accessibility = new()
            {
                Engine = "BirkNext Accessibility Checks",
                Checks =
                [
                    new() { CheckId = "text-contrast", Outcome = "Fail", Tested = 18, Failed = 1 },
                    new() { CheckId = "a11y-hidden-focusable", Outcome = "Fail", Tested = 6, Failed = 1 },
                    new() { CheckId = "language-parts", Outcome = "ManualReviewRequired", Tested = 4, Uncertain = 1 },
                    new() { CheckId = "a11y-page-title", Outcome = "Pass", Tested = 1 },
                ],
                Axe = new()
                {
                    State = "Completed",
                    Rules =
                    [
                        new() { RuleId = "aria-roles", Outcome = "Pass", CriterionIds = ["4.1.2"], Count = 5 },
                        new() { RuleId = "dlitem", Outcome = "Pass", CriterionIds = ["1.3.1"], Count = 0 },
                        new() { RuleId = "aria-meter-name", Outcome = "Pass", CriterionIds = ["4.1.2"], Count = 0 },
                    ]
                }
            },
            Performance = new()
            {
                ObservationType = "spa-navigation", Cls = 0, StabilizationMs = 813, ResourceCount = 1,
                TransferredBytes = 1600, LcpMs = 900,
            },
        }
    });

    /// <summary>A second page: DOM and performance only, and different numbers, so the comparison has something to compare.</summary>
    private void SeedOperations(string path = "/admin/operations") => Discovery.GetSnapshot("dev").Pages.Add(new()
    {
        PageOrigin = Origin, PagePath = path,
        BrowserEvidence = new()
        {
            PageOrigin = Origin, PagePath = path, CapturedAt = Observed.AddMinutes(-6), VisitStartedAt = Observed.AddMinutes(-6),
            Dom = new() { NodeCount = 624, MaxDepth = 20, InteractiveCount = 80, FormControlCount = 4, ImageCount = 2,
                          IframeCount = 1, DialogCount = 0, HiddenFocusableCount = 1 },
            // Stabilization only: CLS, resources and transfer were never reported for this visit.
            Performance = new() { ObservationType = "initial-load", StabilizationMs = 1446 },
        }
    });

    /// <summary>A page whose accessibility assessment ran and flagged nothing at all.</summary>
    private void SeedClean(string path = "/") => Discovery.GetSnapshot("dev").Pages.Add(new()
    {
        PageOrigin = Origin, PagePath = path,
        BrowserEvidence = new()
        {
            PageOrigin = Origin, PagePath = path, CapturedAt = Observed.AddMinutes(-9), VisitStartedAt = Observed.AddMinutes(-9),
            Dom = new() { NodeCount = 77, MaxDepth = 14, InteractiveCount = 16, FormControlCount = 2 },
            Accessibility = new()
            {
                Engine = "BirkNext Accessibility Checks",
                Checks = [new() { CheckId = "a11y-page-title", Outcome = "Pass", Tested = 1 }],
            },
        }
    });

    private readonly List<BrowserCompanionRuntime> _runtimes = [];

    private IRenderedComponent<BrowserDiscoveryTab> OpenEvidence()
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
        cut.Find("[data-testid=browser-discovery-nav-evidence]").Click();
        return cut;
    }

    private static string Text(IRenderedComponent<BrowserDiscoveryTab> cut, string id) =>
        cut.Find($"[data-testid={id}]").TextContent.Trim();

    private static void SelectType(IRenderedComponent<BrowserDiscoveryTab> cut, string type) =>
        cut.Find($"[data-testid=browser-discovery-evidence-nav-{type}]").Click();

    private static IReadOnlyList<IElement> Rows(IRenderedComponent<BrowserDiscoveryTab> cut, string testId) =>
        cut.FindAll($"[data-testid={testId}]");

    private static string Cell(IElement row, int index) =>
        row.QuerySelectorAll("th, td")[index].TextContent.Trim();

    private static void Choose(IRenderedComponent<BrowserDiscoveryTab> cut, string testId, string value) =>
        cut.Find($"[data-testid={testId}]").Change(value);

    // ── §37. Top summary ─────────────────────────────────────────────────────

    // 1, 2, 3, 4, 5.
    [Fact]
    public void TheSummaryCountsPagesPerEvidenceTypeFromRealEvidence()
    {
        SeedRoles();
        SeedOperations();
        SeedClean();
        var cut = OpenEvidence();

        // Three pages carry DOM; two carry accessibility; two carry performance. Nothing is counted that was not observed.
        Text(cut, "bd-inv-dom").Should().Be("3 pages");
        Text(cut, "bd-inv-accessibility").Should().Be("2 pages");
        Text(cut, "bd-inv-performance").Should().Be("2 pages");
        Text(cut, "bd-inv-source").Should().Be("Browser Companion");

        // The selector carries the same counts, so the numbers cannot drift from the tables behind them.
        Text(cut, "browser-discovery-evidence-nav-dom").Should().Be("DOM (3)");
        Text(cut, "browser-discovery-evidence-nav-accessibility").Should().Be("Accessibility (2)");
        Text(cut, "browser-discovery-evidence-nav-performance").Should().Be("Performance (2)");
    }

    // ── §38. DOM comparison ──────────────────────────────────────────────────

    // 6, 7, 8, 9, 10, 11, 12, 14.
    [Fact]
    public void DomIsOneRowPerPageWithTheStructuralCountsAsColumns()
    {
        SeedRoles();
        SeedOperations();
        var cut = OpenEvidence();

        var rows = Rows(cut, "browser-discovery-evidence-dom-row");
        rows.Should().HaveCount(2, "one row per page, not one row per metric per page");

        var roles = rows.First(r => r.TextContent.Contains("/admin/child-specific-roles"));
        Cell(roles, 1).Should().Be("85");     // Nodes
        Cell(roles, 2).Should().Be("17");     // Interactive
        Cell(roles, 3).Should().Be("3");      // Forms
        Cell(roles, 4).Should().Be("1");      // Images
        Cell(roles, 5).Should().Be("1");      // Hidden focusable elements
        // Depth, iFrames and Dialogs are not dropped: they sit on one line under their page.
        var extra = cut.Find("[data-testid=browser-discovery-evidence-dom-extra-row]").TextContent;
        extra.Should().Contain("Depth").And.Contain("14").And.Contain("iFrames").And.Contain("Dialogs");

        var operations = rows.First(r => r.TextContent.Contains("/admin/operations"));
        Cell(operations, 1).Should().Be("624");
        Cell(operations, 2).Should().Be("80");
        cut.FindAll("[data-testid=browser-discovery-evidence-dom-extra-row]")
            .Should().Contain(r => r.TextContent.Replace(" ", "").Contains("iFrames1"), "the iframe count stays visible under its page");
    }

    // 13.
    [Fact]
    public void LandmarksAndHeadingsAreBehindThePageRowRatherThanInTheComparison()
    {
        SeedRoles();
        var cut = OpenEvidence();
        var slug = Slug($"{Origin}/admin/child-specific-roles");

        // Not in the comparison table's own cells.
        Rows(cut, "browser-discovery-evidence-dom-row").Single().TextContent.Should().NotContain("Landmarks");
        cut.Find($"[data-testid=browser-discovery-evidence-dom-detail-{slug}-body]").HasAttribute("hidden").Should().BeTrue();

        cut.Find($"[data-testid=browser-discovery-evidence-dom-detail-{slug}-toggle]").Click();

        var detail = Text(cut, $"browser-discovery-evidence-dom-structure-{slug}");
        detail.Should().Contain("Landmarks").And.Contain("main ×1").And.Contain("navigation ×1");
        detail.Should().Contain("Headings").And.Contain("h1 ×1");
    }

    private static string Slug(string identity) =>
        new(identity.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-').ToArray());

    // ── §39. Accessibility summary ───────────────────────────────────────────

    // 15, 16, 17, 18, 20.
    [Fact]
    public void AccessibilitySummaryAggregatesObservationsFlaggedUncertainAndAreasAcrossPages()
    {
        SeedRoles();
        SeedClean();
        var cut = OpenEvidence();
        SelectType(cut, "accessibility");

        var counts = Text(cut, "browser-discovery-evidence-a11y-counts");
        counts.Should().Contain("2 pages captured");
        // 7 distinct observations on the roles page + 1 on the clean page.
        counts.Should().Contain("8 raw observations");
        // The collector's own outcomes, named as the collector's. Unqualified "flagged" read as a WCAG result.
        counts.Should().Contain("2 source-reported flags");
        counts.Should().Contain("1 source-reported uncertainty");

        var areas = Text(cut, "browser-discovery-evidence-a11y-areas");
        areas.Should().Contain("Perceivable").And.Contain("Operable").And.Contain("Understandable").And.Contain("Robust");
        Text(cut, "browser-discovery-evidence-filters").Should().NotBeEmpty();
    }

    /// <summary>
    /// Cross-page totals are per page: the same check observed on two pages is two observations, because the question
    /// the inventory answers is about pages. Within one page it is still counted once.
    /// </summary>
    [Fact]
    public void TheSameCheckOnTwoPagesIsTwoObservations()
    {
        SeedClean("/a");
        SeedClean("/b");

        var inventory = BrowserDiscoveryPresentation.Inventory(Discovery.GetSnapshot("dev"));

        inventory.AccessibilityPages.Should().Be(2);
        inventory.Observations.Should().Be(2);
        inventory.Accessibility.Should().HaveCount(2);
    }

    // 19.
    [Fact]
    public void APageThatFlaggedNothingSaysSoWithoutClaimingItPassed()
    {
        SeedClean();
        var cut = OpenEvidence();
        SelectType(cut, "accessibility");

        // Evidence exists…
        Text(cut, "browser-discovery-evidence-a11y-counts").Should().Contain("1 raw observation");
        cut.FindAll("[data-testid=browser-discovery-evidence-accessibility-empty]").Should().BeEmpty();
        // …and the default view says what the collector reported, not what a reviewer must do.
        Text(cut, "browser-discovery-evidence-a11y-none")
            .Should().Be("The source reported no flags or uncertainties in the current browser evidence.");
        foreach (var verdict in new[] { "No accessibility issues", "Passed", "Compliant", "Conformant" })
            cut.Markup.Should().NotContain(verdict);
        // The full catalogue is still one click away.
        cut.FindAll("[data-testid=browser-discovery-evidence-all-rules]").Should().ContainSingle();
    }

    // ── §40. Accessibility filters ───────────────────────────────────────────

    // 21, 22, 23.
    [Fact]
    public void NonNeutralSourceOutcomesAreTheDefaultAndTheOutcomeFilterNarrowsThem()
    {
        SeedRoles();
        var cut = OpenEvidence();
        SelectType(cut, "accessibility");

        // The default is still "what is worth looking at", named for what those outcomes are rather than for
        // what a reviewer is supposed to do about them.
        cut.Find("[data-testid=browser-discovery-filter-state]")
            .GetAttribute("value").Should().Be("non-neutral");
        var nonNeutral = Rows(cut, "browser-discovery-evidence-a11y-row").Select(r => r.TextContent).ToList();
        nonNeutral.Should().HaveCount(3, "two flagged checks and one uncertain check; evaluated rules are neither");
        nonNeutral.Should().Contain(t => t.Contains("text-contrast"))
            .And.Contain(t => t.Contains("a11y-hidden-focusable"))
            .And.Contain(t => t.Contains("language-parts"));
        nonNeutral.Should().NotContain(t => t.Contains("aria-meter-name"));
        // Source flags first, then source uncertainties.
        nonNeutral[0].Should().Contain("source-reported flag");
        nonNeutral[^1].Should().Contain("source-reported uncertaint");

        Choose(cut, "browser-discovery-filter-state", "flagged");
        Rows(cut, "browser-discovery-evidence-a11y-row").Should().HaveCount(2);

        Choose(cut, "browser-discovery-filter-state", "uncertain");
        Rows(cut, "browser-discovery-evidence-a11y-row").Single().TextContent.Should().Contain("language-parts");

        Choose(cut, "browser-discovery-filter-state", "evaluated");
        Rows(cut, "browser-discovery-evidence-a11y-row").Select(r => r.TextContent)
            .Should().Contain(t => t.Contains("aria-meter-name"));
    }

    // 24, 25.
    [Fact]
    public void PageAndWcagAreaFiltersNarrowTheInventory()
    {
        SeedRoles();
        SeedClean();
        var cut = OpenEvidence();
        SelectType(cut, "accessibility");
        Choose(cut, "browser-discovery-filter-state", "all");

        var all = Rows(cut, "browser-discovery-evidence-a11y-row").Count;

        Choose(cut, "browser-discovery-filter-page", $"{Origin}/");
        var clean = Rows(cut, "browser-discovery-evidence-a11y-row");
        clean.Should().ContainSingle().Which.TextContent.Should().Contain("a11y-page-title");
        clean.Count.Should().BeLessThan(all);

        Choose(cut, "browser-discovery-filter-page", "");
        Choose(cut, "browser-discovery-filter-area", nameof(WcagPrinciple.Understandable));
        Rows(cut, "browser-discovery-evidence-a11y-row").Select(r => r.TextContent)
            .Should().OnlyContain(t => t.Contains("Understandable"));
    }

    // 26, 27, and §12.
    [Fact]
    public void TheFullCatalogueIsCollapsedAndKeepsTheRulesThatMatchedNothing()
    {
        SeedRoles();
        var cut = OpenEvidence();
        SelectType(cut, "accessibility");

        cut.Find("[data-testid=browser-discovery-evidence-all-rules-body]").HasAttribute("hidden").Should().BeTrue();
        cut.Find("[data-testid=browser-discovery-evidence-all-rules-toggle]").Click();

        var catalogue = Rows(cut, "browser-discovery-evidence-all-rules-row").Select(r => r.TextContent).ToList();
        // Zero-element rules are evidence that the rule ran, and they live here rather than in the attention view.
        catalogue.Should().Contain(t => t.Contains("aria-meter-name") && t.Contains("evaluated"));
        catalogue.Should().Contain(t => t.Contains("dlitem"));
        catalogue.Count.Should().BeGreaterThan(Rows(cut, "browser-discovery-evidence-a11y-row").Count);
    }

    // ── §41. Performance comparison ──────────────────────────────────────────

    // 28, 29, 30, 31, 32, 33, 34.
    [Fact]
    public void PerformanceIsOneRowPerPageAndAnUnreportedMetricIsNotZero()
    {
        SeedRoles();
        SeedOperations();
        var cut = OpenEvidence();
        SelectType(cut, "performance");

        var rows = Rows(cut, "browser-discovery-evidence-performance-row");
        rows.Should().HaveCount(2);

        var roles = rows.First(r => r.TextContent.Contains("/admin/child-specific-roles"));
        Cell(roles, 1).Should().Be("SPA navigation");
        Cell(roles, 2).Should().Be("0");          // CLS genuinely measured as zero
        Cell(roles, 3).Should().Be("813 ms");
        Cell(roles, 4).Should().Be("1");
        // The byte label is culture-formatted (a Norwegian profile renders "1,6 KB"); the unit and magnitude are the point.
        Cell(roles, 5).Should().MatchRegex(@"^1[.,]6 KB$");

        // The other page reported stabilization only. Everything else is stated as not observed, never as 0.
        var operations = rows.First(r => r.TextContent.Contains("/admin/operations"));
        Cell(operations, 2).Should().Be("Not observed");
        Cell(operations, 3).Should().Be("1446 ms");
        Cell(operations, 4).Should().Be("Not observed");
        Cell(operations, 5).Should().Be("Not observed");

        var panel = cut.Find("[data-testid=browser-discovery-evidence-performance-table]").TextContent;
        panel.Should().NotContainAny("Good", "Poor", "Needs improvement", "threshold", "Passed", "Failed");
        cut.Markup.Should().Contain("Raw observations only; thresholds are applied in Frontend Quality Review.");
    }

    /// <summary>Metrics beyond the compared columns stay available without widening the comparison.</summary>
    [Fact]
    public void OtherObservedMetricsSitBehindThePageRow()
    {
        SeedRoles();
        var cut = OpenEvidence();
        SelectType(cut, "performance");
        var slug = Slug($"{Origin}/admin/child-specific-roles");

        Rows(cut, "browser-discovery-evidence-performance-row").Single().TextContent.Should().NotContain("900 ms");
        cut.Find($"[data-testid=browser-discovery-evidence-performance-detail-{slug}-toggle]").Click();
        Text(cut, $"browser-discovery-evidence-performance-detail-items-{slug}")
            .Should().Contain("Largest contentful paint").And.Contain("900 ms");
    }

    // ── §42. Tab ownership ───────────────────────────────────────────────────

    // 35, 36, 37, 38.
    [Fact]
    public void EvidenceComparesPagesAndLeavesTheSessionAndPerPageDetailToTheOtherTabs()
    {
        SeedRoles();
        var cut = OpenEvidence();

        // No session management here.
        cut.FindComponents<BrowserCompanionPanel>().Should().BeEmpty();
        cut.FindAll("[data-testid=browser-companion-pair]").Should().BeEmpty();
        cut.FindAll("[data-testid=browser-companion-details]").Should().BeEmpty();
        // No per-page deep dive here either.
        cut.FindAll("[data-testid=browser-discovery-page-summary]").Should().BeEmpty();
        cut.FindAll("[data-testid=browser-discovery-a11y-attention]").Should().BeEmpty();

        // Overview still owns the session.
        cut.Find("[data-testid=browser-discovery-nav-overview]").Click();
        cut.FindComponents<BrowserCompanionPanel>().Should().ContainSingle();
        cut.FindAll("[data-testid=browser-companion-details]").Should().ContainSingle();

        // Pages still owns the per-page detail.
        cut.Find("[data-testid=browser-discovery-nav-pages]").Click();
        cut.FindAll("[data-testid=browser-discovery-page-summary]").Should().ContainSingle();
    }

    /// <summary>A row is the way into that page's own evidence, instead of repeating it here.</summary>
    [Fact]
    public void OpeningAPageFromTheInventoryLandsOnThatPageInPages()
    {
        SeedRoles();
        SeedOperations();
        var cut = OpenEvidence();

        cut.FindAll("[data-testid=browser-discovery-evidence-open-page]")
            .First(b => b.TextContent.Contains("/admin/operations")).Click();

        cut.Find("[data-testid=browser-discovery-nav-pages]").GetAttribute("aria-selected").Should().Be("true");
        Text(cut, "browser-discovery-selected-page").Should().Be("/admin/operations");
    }

    // ── §43, §44. Semantics and handoff ──────────────────────────────────────

    // 39, 40, 41.
    [Fact]
    public void NothingInTheInventoryReadsAsAnAssessment()
    {
        SeedRoles();
        SeedOperations();
        SeedClean();
        var cut = OpenEvidence();

        // One compact hand-off card instead of a paragraph restating the boundary after every block.
        Text(cut, "browser-discovery-evidence-handoff")
            .Should().Contain("Frontend Quality Review")
            .And.Contain("Interpret captured browser evidence");
        cut.Find("[data-testid=browser-discovery-open-review-evidence]").GetAttribute("href")
            .Should().Be("/frontend-quality-review");
        cut.FindAll("a[href='/frontend-quality-review']").Should().ContainSingle("one handoff, not one per evidence type");

        foreach (var type in new[] { "dom", "accessibility", "performance" })
        {
            SelectType(cut, type);
            foreach (var verdict in new[]
                     {
                         "Passed", "Failed", "Compliant", "WCAG compliant", "Performance passed", "Performance failed",
                         "Accessibility passed", "No accessibility issues", "score", "grade", "Page health",
                     })
                cut.Markup.Should().NotContain(verdict, $"{type} must stay evidence-only");
        }

        SelectType(cut, "accessibility");
        cut.Markup.Should().Contain("WCAG areas and criterion references show how raw evidence is mapped");
        cut.Markup.Should().Contain("not WCAG assessment results");
    }
}
