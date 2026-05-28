using BirkNext.Web.Layout;
using Bunit;
using FluentAssertions;

namespace BirkNext.Web.Tests.Layout;

public class NavMenuTests : BunitContext
{
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
        navText.Should().Contain("Scenario Library");
        navText.Should().Contain("New Scenario");
        navText.Should().Contain("Compare Specs");

        navText.IndexOf("Dashboard", StringComparison.Ordinal).Should().BeLessThan(navText.IndexOf("Specification Review", StringComparison.Ordinal));
        navText.IndexOf("Specification Review", StringComparison.Ordinal).Should().BeLessThan(navText.IndexOf("Scenario Library", StringComparison.Ordinal));
        navText.IndexOf("Scenario Library", StringComparison.Ordinal).Should().BeLessThan(navText.IndexOf("New Scenario", StringComparison.Ordinal));
        navText.IndexOf("New Scenario", StringComparison.Ordinal).Should().BeLessThan(navText.IndexOf("Compare Specs", StringComparison.Ordinal));

        cut.Find("a[href='dashboard']").Should().NotBeNull();
        cut.Find("a[href='extract']").Should().NotBeNull();
        cut.Find("a[href='scenarios']").Should().NotBeNull();
        cut.Find("a[href='scenarios/new']").Should().NotBeNull();
        cut.Find("a[href='compare']").Should().NotBeNull();
        cut.Find("a[href='dashboard'] .nav-icon-dashboard").Should().NotBeNull();
        cut.Find("a[href='compare'] .nav-icon-compare").Should().NotBeNull();
    }
}
