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
    public void UserGuide_HasSpecDeltasMovedFaq()
    {
        var cut = Render<UserGuide>();

        cut.Markup.Should().Contain("Where did Specification Deltas go?",
            "FAQ for displaced Spec Deltas users must be present in the user guide");

        // The answer is collapsed by default — click the question button to expand it
        cut.FindAll("button.ug-faq-q")
           .First(b => b.TextContent.Contains("Where did Specification Deltas go?"))
           .Click();

        cut.Markup.Should().Contain("Spec Drift is the only entry point for specification change analysis",
            "FAQ answer must direct users to Spec Drift as the replacement");
    }
}
