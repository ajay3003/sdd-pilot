using BirkNext.BrowserCompanion;
using BirkNext.Web.Components;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace BirkNext.Web.Tests.Components;

public sealed class BrowserQualityArchitectureUITests : BunitContext
{
    public BrowserQualityArchitectureUITests()
    {
        Services.AddSingleton<IEndpointDiscoveryService, EndpointDiscoveryService>();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public void OverviewIsCompactAndDedicatedViewSwitchesHeadingAndRowsTogether()
    {
        var profile = new FrontendAnalysisProfile { Id = "dev", TargetUrl = "https://m2lbdev.bufetat.no" };
        var cut = Render<EndpointDiscoveryTab>(p => p.Add(c => c.Profile, profile));
        Assert.Single(cut.FindAll("[data-testid=browser-quality-summary]"));
        Assert.Empty(cut.FindAll("[data-testid=wcag-coverage]"));
        Assert.Contains("Awaiting browser evidence", cut.Markup);
        Assert.Contains("Saved application analyses", cut.Markup);
        Assert.Contains("Last observed traffic", cut.Markup);
        Assert.Contains("Inactive", cut.Markup); Assert.Contains("Not connected", cut.Markup);
        cut.Find("[data-testid=discovery-nav-quality]").Click();
        Assert.Contains("Norwegian legal requirements", cut.Markup);
        Assert.Equal(48, cut.FindAll("[data-testid=wcag-criterion-row]").Count);
        Assert.DoesNotContain("Automated assessment", cut.Markup);
        Assert.Contains("Connect Browser Companion", cut.Markup);
        cut.Find(".wcag-workspace > label select").Change(WcagProfiles.ExtendedId);
        Assert.Contains("Extended WCAG review", cut.Find("h3").TextContent);
        Assert.Equal(55, cut.FindAll("[data-testid=wcag-criterion-row]").Count);
        cut.Find(".wcag-workspace > label select").Change(WcagProfiles.NorwegianId);
        Assert.Contains("WCAG 2.1", cut.Find("h3").TextContent);
        Assert.Equal(48, cut.FindAll("[data-testid=wcag-criterion-row]").Count);
        Assert.DoesNotContain("3.3.8", cut.Find("tbody").TextContent);
    }

    [Fact]
    public void OneRowPerCriterionAndDetailExposesAllPagesWithoutPermanentReviewButtons()
    {
        var snapshot = new EndpointDiscoverySnapshot { Pages = [new() { PagePath = "/a" }, new() { PagePath = "/b" }] };
        var cut = Render<WcagCoverage>(p => p.Add(c => c.Assessment, WcagAssessmentEngine.Evaluate(snapshot))
            .Add(c => c.Review, _ => { }));
        Assert.Equal(48, cut.FindAll("tbody tr").Count);
        Assert.DoesNotContain(cut.FindAll("button"), b => b.TextContent.StartsWith("Review"));
        cut.Find("tbody tr button").Click();
        var detail = cut.Find("[data-testid=wcag-criterion-detail]");
        Assert.Contains("/a", detail.TextContent); Assert.Contains("/b", detail.TextContent);
        Assert.Equal(2, detail.QuerySelectorAll("article").Length);
        Assert.Contains("Manual-only remaining", cut.Markup);
        Assert.Contains("explicit review-required results", cut.Markup);
    }

    [Fact]
    public void DisconnectDoesNotRemoveSavedEvidenceOrImplyCurrentSessionAnalysis()
    {
        var discovery = Services.GetRequiredService<IEndpointDiscoveryService>();
        discovery.GetSnapshot("dev").Pages.Add(new() { PageOrigin = "https://m2lbdev.bufetat.no", PagePath = "/dashboard",
            BrowserEvidence = new() { CapturedAt = DateTimeOffset.UtcNow, Accessibility = new() { Checks = [new() { CheckId = "a11y-page-title", Outcome = "Pass", Tested = 1 }] } } });
        var cut = Render<EndpointDiscoveryTab>(p => p.Add(c => c.Profile, new FrontendAnalysisProfile { Id = "dev", TargetUrl = "https://m2lbdev.bufetat.no" })
            .Add(c => c.CompanionStatus, new BrowserCompanionStatus()));
        Assert.Equal("1", cut.Find("[data-testid=discovery-pages-count]").TextContent);
        Assert.Equal("Not connected", cut.Find("[data-testid=discovery-companion]").TextContent);
        Assert.Contains("Partially assessed", cut.Markup);
    }
}
