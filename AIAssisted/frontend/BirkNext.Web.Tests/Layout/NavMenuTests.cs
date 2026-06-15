using BirkNext.Web.Layout;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
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
        navText.Should().Contain("Specification Review");
        navText.Should().Contain("QA Artifact Library");
        navText.Should().Contain("Create Test Scenario");
        navText.Should().Contain("Spec Comparison");
        navText.Should().Contain("Specification Deltas");

        navText.IndexOf("Dashboard", StringComparison.Ordinal).Should().BeLessThan(navText.IndexOf("Specification Review", StringComparison.Ordinal));
        navText.IndexOf("Specification Review", StringComparison.Ordinal).Should().BeLessThan(navText.IndexOf("QA Artifact Library", StringComparison.Ordinal));
        navText.IndexOf("QA Artifact Library", StringComparison.Ordinal).Should().BeLessThan(navText.IndexOf("Create Test Scenario", StringComparison.Ordinal));
        navText.IndexOf("Create Test Scenario", StringComparison.Ordinal).Should().BeLessThan(navText.IndexOf("Spec Comparison", StringComparison.Ordinal));
        navText.IndexOf("Spec Comparison", StringComparison.Ordinal).Should().BeLessThan(navText.IndexOf("Specification Deltas", StringComparison.Ordinal));

        cut.Find("a[href='dashboard']").Should().NotBeNull();
        cut.Find("a[href='extract']").Should().NotBeNull();
        cut.Find("a[href='scenarios']").Should().NotBeNull();
        cut.Find("a[href='scenarios/new']").Should().NotBeNull();
        cut.Find("a[href='compare']").Should().NotBeNull();
        cut.Find("a[href='compare/reviews']").Should().NotBeNull();
        cut.Find("a[href='dashboard'] .nav-icon-dashboard").Should().NotBeNull();
        cut.Find("a[href='compare'] .nav-icon-compare").Should().NotBeNull();
    }
}
