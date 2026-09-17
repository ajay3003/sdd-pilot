using AngleSharp.Html.Dom;
using BirkNext.BrowserCompanion;
using BirkNext.Web.Components;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using BirkNext.Web.Shared.Components.Cards;
using BirkNext.Web.Shared.Components.Layout;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace BirkNext.Web.Tests.Components;

public sealed class BrowserQualityRedesignTests : BunitContext
{
    private IEndpointDiscoveryService Discovery => Services.GetRequiredService<IEndpointDiscoveryService>();
    private static FrontendAnalysisProfile Profile => new() { Id = "dev", TargetUrl = "https://m2lbdev.bufetat.no" };
    public BrowserQualityRedesignTests()
    {
        Services.AddSingleton<IEndpointDiscoveryService, EndpointDiscoveryService>();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }
    private IRenderedComponent<BrowserQualityWorkspace> RenderQuality() => Render<BrowserQualityWorkspace>(p => p.Add(c => c.Profile, Profile));

    [Theory]
    [InlineData(WcagProfiles.NorwegianId, "2.1", 48)]
    [InlineData(WcagProfiles.ExtendedId, "2.2", 55)]
    [InlineData("legacy-22-AA", "2.2", 56)]
    public void InitialSelectorConfigurationResultsAndCriteriaAgree(string id, string version, int count)
    {
        Discovery.GetSnapshot("dev").Wcag = new() { ProfileId = id };
        var cut = RenderQuality();
        Assert.Equal(id, ((IHtmlSelectElement)cut.Find("[data-testid=wcag-profile]")).Value);
        Assert.Equal(id, cut.Find("[data-testid=wcag-coverage]").GetAttribute("data-profile"));
        Assert.Contains("WCAG " + version, cut.Find("[data-testid=wcag-assessment-title]").TextContent);
        Assert.DoesNotContain("Legacy", cut.Markup);
        cut.Find("[data-testid=wcag-toggle-criteria]").Click();
        var ids = cut.FindAll("[data-criterion]").Select(e => e.GetAttribute("data-criterion")).ToArray();
        Assert.Equal(count, ids.Length);
        Assert.Equal(WcagProfiles.Resolve(id).CriterionIds.Order(), ids.Order());
    }

    [Fact]
    public async Task ReplacingStoredSnapshotDoesNotLeaveSelectorOnOldProfile()
    {
        var beforeLoad = Discovery.GetSnapshot("dev");
        beforeLoad.Wcag = new() { ProfileId = "legacy-22-AA" };
        var cut = RenderQuality();
        JSInterop.Setup<string?>("birkNextStorage.getDiscovery").SetResult("{\"dev\":{\"wcag\":{\"profileId\":\"no-public-wcag21-48-v1\"},\"pages\":[]}}");
        await Discovery.LoadAsync(Services.GetRequiredService<IJSRuntime>());
        Assert.NotSame(beforeLoad, Discovery.GetSnapshot("dev"));
        cut.Render();
        Assert.Equal(WcagProfiles.NorwegianId, ((IHtmlSelectElement)cut.Find("[data-testid=wcag-profile]")).Value);
        Assert.Contains("Norwegian legal baseline", cut.Find("[data-testid=wcag-assessment-title]").TextContent);
        cut.Find("[data-testid=wcag-profile]").Change(WcagProfiles.ExtendedId);
        Assert.Equal(WcagVersion.Wcag22, Discovery.GetAssessment("dev").Version);
        Assert.Contains("WCAG 2.2", cut.Find("[data-testid=wcag-assessment-title]").TextContent);
        Assert.Contains("Show all 55 criteria", cut.Markup);
    }

    [Fact]
    public void TabsAreLabelledDefaultToWcagAndPerformanceNeedsNoMatrixExpansion()
    {
        var cut = RenderQuality();
        var tabs = cut.FindAll("[role=tab]");
        Assert.Equal(new[] { "WCAG", "Performance" }, tabs.Select(t => t.TextContent));
        Assert.Equal("true", tabs[0].GetAttribute("aria-selected"));
        Assert.Equal("0", tabs[0].GetAttribute("tabindex"));
        Assert.Equal("-1", tabs[1].GetAttribute("tabindex"));
        Assert.All(tabs, tab => Assert.Contains("tab-btn", tab.ClassList));
        tabs[1].Click();
        Assert.Equal("true", cut.FindAll("[role=tab]")[1].GetAttribute("aria-selected"));
        Assert.True(cut.FindAll("[role=tabpanel]")[0].HasAttribute("hidden"));
        Assert.Contains("No browser performance evidence", cut.Find("[data-testid=browser-quality-performance]").TextContent);
        Assert.Empty(cut.FindAll("[data-testid=wcag-criterion-row]"));
        cut.FindAll("[role=tab]")[1].KeyDown("ArrowLeft");
        Assert.Equal("true", cut.FindAll("[role=tab]")[0].GetAttribute("aria-selected"));
        cut.FindAll("[role=tab]")[0].KeyDown("End");
        Assert.Equal("true", cut.FindAll("[role=tab]")[1].GetAttribute("aria-selected"));
    }

    [Fact]
    public void EmptyStateUsesSharedCardsSubsetCountsAndAnAccessibleCollapsedMatrix()
    {
        var cut = RenderQuality();
        Assert.Contains("No browser evidence collected yet", cut.Markup);
        Assert.NotEmpty(cut.FindComponents<Card>());
        Assert.NotEmpty(cut.FindComponents<EmptyState>());
        Assert.Equal(5, cut.FindComponents<MetricCard>().Count);
        var cards = cut.FindComponents<MetricCard>().Select(c => c.Instance).ToList();
        Assert.Equal("0", cards.Single(c => c.Label == "Failed criteria").Value);
        Assert.Equal("0", cards.Single(c => c.Label == "Require manual review").Value);
        Assert.Equal("10 of 48", cards.Single(c => c.Label == "Manual assessment required").Value);
        Assert.Contains("subset", cards.Single(c => c.Label == "Manual assessment required").Sublabel);
        Assert.Equal("48", cards.Single(c => c.Label == "Not yet assessed").Value);
        Assert.Equal("0", cards.Single(c => c.Label == "Criteria with evidence").Value);
        Assert.Empty(cut.FindAll("[data-testid=wcag-filters]"));
        var toggle = cut.Find("[data-testid=wcag-toggle-criteria]");
        Assert.Contains("btn-secondary", toggle.ClassList);
        Assert.Equal("false", toggle.GetAttribute("aria-expanded"));
        Assert.Equal("Show all 48 criteria", toggle.TextContent.Trim());
        Assert.Contains("Assessment profile", cut.Find("[data-testid=wcag-profile]").ParentElement!.TextContent);
        toggle.Click();
        Assert.Equal("true", cut.Find("[data-testid=wcag-toggle-criteria]").GetAttribute("aria-expanded"));
        Assert.Equal(48, cut.FindAll("[data-testid=wcag-criterion-row]").Count);
        Assert.Equal(4, cut.FindAll("details.wcag-principle > summary").Count);
        Assert.Equal(new[] { "Status", "Level", "Automation", "Search" }, cut.FindAll(".wcag-filters label").Select(l => l.ChildNodes.First().TextContent.Trim()));
        Assert.All(cut.FindAll("tbody .status-chip"), badge => Assert.Equal("Not tested", badge.TextContent));
        Assert.All(cut.FindAll(".wcag-evidence"), cell => Assert.Equal("No evidence", cell.TextContent.Trim()));
        Assert.Equal(48, cut.FindAll("[data-criterion]").Select(e => e.GetAttribute("data-criterion")).Distinct().Count());
    }

    [Fact]
    public void ApplicationSlotsAreNotBrowserEvidenceAndLiveInDetails()
    {
        var cut = RenderQuality();
        var assessment = Discovery.GetAssessment("dev");
        Assert.Equal(3, assessment.Results.Count);
        Assert.All(assessment.Results, r => { Assert.True(r.Definition.RequiresCrossPageEvidence); Assert.Null(r.LastTested); });
        Assert.Equal(0, assessment.PagesWithEvidence);
        Assert.DoesNotContain("assessment instances", cut.Markup);
        Assert.DoesNotContain("Pages / evidence", cut.Markup);
        cut.Find("[data-testid=wcag-toggle-criteria]").Click();
        cut.Find("[data-criterion='3.2.3'] button").Click();
        var detail = cut.Find("[data-testid=wcag-criterion-detail]");
        Assert.Contains("Application / process assessment slot", detail.TextContent);
        Assert.Contains("This is not browser evidence", detail.TextContent);
        Assert.Contains("No evidence", cut.Find("[data-criterion='3.2.3'] .wcag-evidence").TextContent);
    }

    [Fact]
    public void SavedManualReviewDoesNotCreateBrowserPageOrExecutionEvidence()
    {
        var snapshot = Discovery.GetSnapshot("dev");
        snapshot.WcagApplicationReviews.Add(new() { CriterionId = "3.2.3", Version = WcagVersion.Wcag21,
            AssessmentProfileId = WcagProfiles.NorwegianId, Result = WcagStatus.Pass, EvidenceNote = "Reviewed navigation", ReviewedBy = "tester" });
        var cut = RenderQuality();
        Assert.Contains("Awaiting browser evidence", cut.Markup);
        Assert.Contains("0 criteria with execution evidence", cut.Markup);
        Assert.Contains("1 manual reviews", cut.Find("[data-criterion='3.2.3'] .wcag-evidence").TextContent);
        Assert.DoesNotContain("browser pages", cut.Find("[data-criterion='3.2.3'] .wcag-evidence").TextContent);
        Assert.Equal("1", cut.FindComponents<MetricCard>().Single(c => c.Instance.Label == "Criteria with evidence").Instance.Value);
    }

    [Fact]
    public void UnavailablePerformanceIsNotNumericZeroAndSupportedZeroIsPreserved()
    {
        Discovery.GetSnapshot("dev").Pages.Add(new() { PageOrigin = Profile.TargetUrl, PagePath = "/", BrowserEvidence = new()
        { Performance = new() { LcpMs = 1800, Cls = 0, Interaction = new() { Status = "insufficient-samples" } } } });
        var cut = RenderQuality();
        cut.Find("[data-testid=browser-quality-performance-tab]").Click();
        Assert.Equal("Not available", cut.Find("[data-testid=bq-core-inp] .metric-value").TextContent);
        Assert.Equal("Not available", cut.Find("[data-testid=bq-core-ttfb] .metric-value").TextContent);
        Assert.DoesNotContain("Not available", cut.Find("[data-testid=bq-core-lcp] .metric-value").TextContent);
        Assert.Contains("0", cut.Find("[data-testid=bq-core-cls] .metric-value").TextContent);
    }

    [Fact]
    public void NoAutomatedViolationDoesNotBecomeConformanceOrFailure()
    {
        Discovery.GetSnapshot("dev").Pages.Add(new() { PageOrigin = Profile.TargetUrl, PagePath = "/", BrowserEvidence = new()
        { CapturedAt = DateTimeOffset.UtcNow, Accessibility = new() { Checks = [new() { CheckId = "a11y-image-alt", Outcome = "Pass", Tested = 1 }] } } });
        var cut = RenderQuality();
        cut.Find("[data-testid=wcag-toggle-criteria]").Click();
        Assert.Equal("Not tested", cut.Find("[data-criterion='1.1.1'] .status-chip").TextContent);
        Assert.Contains("1 browser pages with execution", cut.Find("[data-criterion='1.1.1'] .wcag-evidence").TextContent);
        Assert.Contains("does not establish WCAG conformance", cut.Markup);
        Assert.DoesNotContain("WCAG compliant", cut.Markup);
        Assert.Equal("0", cut.FindComponents<MetricCard>().Single(c => c.Instance.Label == "Failed criteria").Instance.Value);
    }
}
