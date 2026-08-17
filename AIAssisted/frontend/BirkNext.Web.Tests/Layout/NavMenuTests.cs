using BirkNext.Web.Layout;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace BirkNext.Web.Tests.Layout;

public class NavMenuTests : BunitContext
{
    public NavMenuTests()
    {
        Services.AddSingleton<FeatureVisibilityService>();
    }

    [Fact]
    public void NavMenu_RendersMvpNavigation()
    {
        var cut = Render<NavMenu>();

        var navText = cut.Find("nav").TextContent;
        navText.Should().NotContain("Home");
        navText.Should().Contain("Review");
        navText.Should().Contain("Library");
        navText.Should().Contain("Analysis");
        navText.Should().Contain("Dashboard");
        navText.Should().Contain("Specification Explorer");
        navText.Should().NotContain("Specification Review");
        navText.Should().Contain("QA Artifact Library");
        navText.Should().NotContain("Create Test Scenario");
        navText.Should().NotContain("AI REVIEW");
        navText.Should().NotContain("AI Change Review");
        navText.Should().NotContain("Spec Comparison");
        navText.Should().NotContain("Specification Deltas");

        navText.IndexOf("Dashboard", StringComparison.Ordinal).Should().BeLessThan(navText.IndexOf("Specification Explorer", StringComparison.Ordinal));
        navText.IndexOf("Specification Explorer", StringComparison.Ordinal).Should().BeLessThan(navText.IndexOf("QA Artifact Library", StringComparison.Ordinal));

        cut.Find("a[href='dashboard']").Should().NotBeNull();
        cut.Find("a[href='specification-explorer']").Should().NotBeNull();
        cut.FindAll("a[href='extract']").Should().BeEmpty();
        cut.Find("a[href='scenarios']").Should().NotBeNull();
        cut.FindAll("a[href='scenarios/new']").Should().BeEmpty();
        cut.FindAll("a[href='compare']").Should().BeEmpty();
        cut.FindAll("a[href='compare/reviews']").Should().BeEmpty();
        cut.FindAll("a[href='ai-change-auditor']").Should().BeEmpty();
        cut.Find("a[href='dashboard'] .nav-icon-dashboard").Should().NotBeNull();
    }

    [Fact]
    public void Navigation_ShowsAiReviewGroup_WhenFeatureFlagEnabled()
    {
        Services.GetRequiredService<FeatureVisibilityService>().ApplyLocalFlags(new FeatureVisibilityDto
        {
            AiChangeReview = true
        });

        var cut = Render<NavMenu>();

        var navText = cut.Find("nav").TextContent;
        navText.Should().Contain("AI REVIEW");
        navText.Should().Contain("AI Change Review");
        cut.Find("a[href='ai-change-auditor']").Should().NotBeNull();
    }

    [Fact]
    public void Navigation_HidesLegacyTraceabilityGroupByDefault()
    {
        var cut = Render<NavMenu>();

        var navText = cut.Find("nav").TextContent;
        // Legacy traceability group items should be hidden when LegacyTraceabilityNavigationEnabled = false
        navText.Should().NotContain("Traceability & Coverage");
        navText.Should().NotContain("Traceability Suggestions");
        navText.Should().NotContain("Code Traceability");
        // "Artifact Traceability" is a distinct analysis feature and IS expected to show by default

        cut.FindAll("a[href='traceability']").Should().BeEmpty();
        cut.FindAll("a[href='traceability/suggestions']").Should().BeEmpty();
        cut.FindAll("a[href='code-traceability']").Should().BeEmpty();
    }

    [Fact]
    public void Navigation_ShowsLegacyTraceabilityGroup_WhenFeatureFlagEnabled()
    {
        Services.GetRequiredService<FeatureVisibilityService>().ApplyLocalFlags(new FeatureVisibilityDto
        {
            LegacyTraceabilityNavigationEnabled = true
        });

        var cut = Render<NavMenu>();

        var navText = cut.Find("nav").TextContent;
        navText.Should().Contain("Traceability & Coverage");
        navText.Should().Contain("Traceability Suggestions");
        navText.Should().Contain("Code Traceability");

        cut.Find("a[href='traceability']").Should().NotBeNull();
        cut.Find("a[href='traceability/suggestions']").Should().NotBeNull();
        cut.Find("a[href='code-traceability']").Should().NotBeNull();

        var traceLink = cut.Find("a[href='traceability']");
        (traceLink.GetAttribute("style") ?? string.Empty).Should().NotContain("height:");
    }

    [Fact]
    public void TraceabilityNavigation_RendersCorrectHierarchy_WhenFeatureFlagEnabled()
    {
        Services.GetRequiredService<FeatureVisibilityService>().ApplyLocalFlags(new FeatureVisibilityDto
        {
            LegacyTraceabilityNavigationEnabled = true
        });

        var cut = Render<NavMenu>();

        var navText = cut.Find("nav").TextContent;

        // "Traceability & Coverage" must precede "Traceability Suggestions"
        navText.IndexOf("Traceability & Coverage", StringComparison.Ordinal)
            .Should().BeLessThan(navText.IndexOf("Traceability Suggestions", StringComparison.Ordinal));

        // "Traceability Suggestions" must precede "Code Traceability"
        navText.IndexOf("Traceability Suggestions", StringComparison.Ordinal)
            .Should().BeLessThan(navText.IndexOf("Code Traceability", StringComparison.Ordinal));
    }

    [Fact]
    public void LegacyTraceabilityRoutes_StillResolve()
    {
        RoutesFor<Traceability>().Should().Contain("/traceability");
        RoutesFor<TraceabilitySuggestions>().Should().Contain("/traceability/suggestions");
        RoutesFor<TraceabilitySuggestions>().Should().Contain("/traceability-suggestions");
        RoutesFor<CodeTraceability>().Should().Contain("/code-traceability");
    }

    private static IReadOnlyList<string> RoutesFor<TComponent>() =>
        typeof(TComponent)
            .GetCustomAttributes(typeof(RouteAttribute), inherit: false)
            .Cast<RouteAttribute>()
            .Select(a => a.Template)
            .ToList();
}

