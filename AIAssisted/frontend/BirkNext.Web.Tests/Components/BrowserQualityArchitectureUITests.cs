using BirkNext.BrowserCompanion;
using BirkNext.Web.Components;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using BirkNext.Web.Shared.Components.Buttons;
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

    /// <summary>
    /// WCAG assessment and browser performance judgement are review surfaces; Browser Discovery no longer hosts them.
    /// The assessment workspace keeps its own behaviour wherever it is mounted.
    /// </summary>
    [Fact]
    public void AssessmentWorkspaceOwnsAssessmentAndPreservesProfileSelection()
    {
        var cut = Render<BrowserQualityWorkspace>(p => p.Add(c => c.Profile,
            new FrontendAnalysisProfile { Id = "dev", TargetUrl = "https://m2lbdev.bufetat.no" }));
        Assert.Single(cut.FindComponents<WcagWorkspace>());
        Assert.Contains("Norwegian legal baseline", cut.Markup);
        cut.Find("[data-testid=wcag-profile]").Change(WcagProfiles.ExtendedId);
        cut.Find("[data-testid=wcag-toggle-criteria]").Click();
        Assert.Equal(55, cut.FindAll("[data-testid=wcag-criterion-row]").Count);
        cut.Find("[data-testid=browser-quality-performance-tab]").Click();
        Assert.Empty(cut.FindComponents<WcagWorkspace>());
        Assert.Contains("No browser performance evidence", cut.Markup);
        cut.Find("[data-testid=browser-quality-wcag-tab]").Click();
        Assert.Contains("Extended assessment", cut.Find("[data-testid=wcag-assessment-title]").TextContent);
    }

    [Fact]
    public void OneRowPerCriterionAndDetailExposesAllPagesWithoutPermanentReviewButtons()
    {
        var snapshot = new EndpointDiscoverySnapshot { Pages = [new() { PagePath = "/a" }, new() { PagePath = "/b" }] };
        var cut = Render<WcagCoverage>(p => p.Add(c => c.Assessment, WcagAssessmentEngine.Evaluate(snapshot))
            .Add(c => c.Review, _ => { }));
        cut.Find("[data-testid=wcag-toggle-criteria]").Click();
        Assert.Equal(48, cut.FindAll("tbody tr").Count);
        Assert.DoesNotContain(cut.FindAll("button"), b => b.TextContent.StartsWith("Review"));
        cut.Find("tbody tr button").Click();
        var detail = cut.Find("[data-testid=wcag-criterion-detail]");
        Assert.Contains("/a", detail.TextContent); Assert.Contains("/b", detail.TextContent);
        Assert.Equal(2, detail.QuerySelectorAll("article").Length);
        Assert.Contains("Manual-only subset", cut.Markup);
        Assert.Contains("Explicit review-required results", cut.Markup);
    }

    [Fact]
    public void DisconnectDoesNotRemoveSavedEvidenceOrImplyCurrentSessionAnalysis()
    {
        var discovery = Services.GetRequiredService<IEndpointDiscoveryService>();
        discovery.GetSnapshot("dev").Pages.Add(new() { PageOrigin = "https://m2lbdev.bufetat.no", PagePath = "/dashboard",
            BrowserEvidence = new() { CapturedAt = DateTimeOffset.UtcNow, Accessibility = new() { Checks = [new() { CheckId = "a11y-page-title", Outcome = "Pass", Tested = 1 }] } } });
        var profile = new FrontendAnalysisProfile { Id = "dev", TargetUrl = "https://m2lbdev.bufetat.no" };
        // Browser Discovery keeps presenting the stored evidence even though no session is connected.
        var discoveryTab = Render<BrowserDiscoveryTab>(p => p.Add(c => c.Profile, profile));
        Assert.Equal("1", discoveryTab.Find("[data-testid=bd-pages-count]").TextContent);
        Assert.Equal("Not connected", discoveryTab.Find("[data-testid=bd-session]").TextContent);
        // The assessment surface reads the same stored evidence without implying a live session.
        var assessment = Render<BrowserQualityWorkspace>(p => p.Add(c => c.Profile, profile));
        Assert.Contains("1 pages with browser evidence", assessment.Markup);
    }
}
