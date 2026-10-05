using BirkNext.Web.Components;
using BirkNext.Web.Models;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;

namespace BirkNext.Web.Tests.Pages;

public class UxConsistencyTests : BunitContext
{
    [Fact]
    public void SpecDeltaResultsPanel_HeadingIsChanges()
    {
        var result = new SpecComparisonResult(
            Array.Empty<SpecDeltaItem>(),
            Array.Empty<SpecDeltaItem>(),
            Array.Empty<SpecDeltaItem>(),
            new SpecComparisonSummary(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0));

        var cut = Render<SpecDeltaResultsPanel>(p => p.Add(x => x.Result, result));

        cut.Find("h2").TextContent.Trim().Should().Be("Changes");
        cut.Markup.Should().NotContain("Delta Results");
    }

    [Fact]
    public void Dashboard_DoesNotContainLegacyProgressText()
    {
        var empty = new DashboardMetrics(
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 0, 0, 0);

        var cut = Render<DashboardMetricsCards>(p => p.Add(x => x.Metrics, empty));

        cut.Markup.Should().NotContain("Review Progress");
        cut.Markup.Should().Contain("Coverage Requirements");
        cut.Find("[data-testid='dashboard-nav-links'] a[href='spec-drift']").Should().NotBeNull();
    }

    [Fact]
    public void UserGuide_CoversCurrentProviderAndExtensionBoundaries()
    {
        var cut = Render<UserGuide>();

        cut.Markup.Should().Contain("current provider is Azure");
        cut.Markup.Should().Contain("M2LB child-security-classification extension");
        cut.Markup.Should().Contain("does not make BirkNext Azure-only");
    }
}
