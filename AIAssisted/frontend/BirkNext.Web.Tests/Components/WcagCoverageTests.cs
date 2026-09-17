using BirkNext.Web.Components;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using Bunit;

namespace BirkNext.Web.Tests.Components;

public sealed class WcagCoverageTests : BunitContext
{
    [Fact]
    public void MatrixExposesStatusesAndFiltersWithoutCollapsingReviewsIntoFailures()
    {
        var definitions = WcagRegistry.All.Take(5).ToArray();
        var assessment = new WcagAssessment { Results = Enum.GetValues<WcagStatus>().Select((s, i) => new WcagCriterionResult
        { Definition = definitions[i], Page = "/test", Status = s }).ToList() };
        var cut = Render<WcagCoverage>(p => p.Add(c => c.Assessment, assessment));
        Assert.Equal(5, cut.FindAll("tbody tr").Count);
        Assert.Contains("No automated failure detected does not establish", cut.Markup);
        cut.FindAll("select")[0].Change("ManualReviewRequired");
        Assert.Single(cut.FindAll("tbody tr"));
        Assert.Contains("Manual review required", cut.Find("tbody").TextContent);
        cut.FindAll("select")[0].Change("NotApplicable");
        Assert.Contains("Not applicable", cut.Find("tbody").TextContent);
    }
}
