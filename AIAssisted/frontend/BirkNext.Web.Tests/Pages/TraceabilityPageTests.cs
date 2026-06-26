using BirkNext.Web.GraphQL;
using BirkNext.Web.Pages;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using StrawberryShake;

namespace BirkNext.Web.Tests.Pages;

public class TraceabilityPageTests : BunitContext
{
    private void SetupEmptyClient()
    {
        var matrixQuery = new Mock<IGetTraceabilityMatrixQuery>();
        matrixQuery
            .Setup(q => q.ExecuteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeEmptyMatrixResult());

        var summaryQuery = new Mock<IGetCoverageSummaryQuery>();
        summaryQuery
            .Setup(q => q.ExecuteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeEmptySummaryResult());

        var scenariosQuery = new Mock<IGetScenariosQuery>();
        scenariosQuery
            .Setup(q => q.ExecuteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeEmptyScenariosResult());

        var client = new Mock<IBirkNextClient>();
        client.Setup(c => c.GetTraceabilityMatrix).Returns(matrixQuery.Object);
        client.Setup(c => c.GetCoverageSummary).Returns(summaryQuery.Object);
        client.Setup(c => c.GetScenarios).Returns(scenariosQuery.Object);

        Services.AddSingleton(client.Object);
    }

    [Fact]
    public void TraceabilityFilters_UseDesignSystemComponents()
    {
        SetupEmptyClient();

        var cut = Render<Traceability>();

        cut.WaitForAssertion(() =>
            cut.Markup.Should().NotContain("Loading coverage data"),
            timeout: TimeSpan.FromSeconds(1));

        var chipGrid = cut.Find(".filter-chip-grid");
        chipGrid.Should().NotBeNull();

        var filterButtons = cut.FindAll(".filter-chip-grid button");
        filterButtons.Should().HaveCount(4);
        foreach (var btn in filterButtons)
            btn.ClassList.Should().Contain("library-filter-chip");
    }

    [Fact]
    public void TraceabilityPage_HasNoDefaultBrowserButtons()
    {
        SetupEmptyClient();

        var cut = Render<Traceability>();

        cut.WaitForAssertion(() =>
            cut.Markup.Should().NotContain("Loading coverage data"),
            timeout: TimeSpan.FromSeconds(1));

        // Every button in the filter area must use the design system chip class
        var filterButtons = cut.FindAll(".filter-chip-grid button");
        foreach (var btn in filterButtons)
        {
            btn.ClassList.Should().Contain("library-filter-chip",
                because: $"button '{btn.TextContent.Trim()}' must use design-system styling");
        }
    }

    private static IOperationResult<IGetTraceabilityMatrixResult> MakeEmptyMatrixResult()
    {
        var mockData = new Mock<IGetTraceabilityMatrixResult>();
        mockData.Setup(d => d.TraceabilityMatrix).Returns([]);

        var mockResult = new Mock<IOperationResult<IGetTraceabilityMatrixResult>>();
        mockResult.Setup(r => r.Data).Returns(mockData.Object);
        mockResult.Setup(r => r.Errors).Returns([]);

        return mockResult.Object;
    }

    private static IOperationResult<IGetCoverageSummaryResult> MakeEmptySummaryResult()
    {
        var mockSummary = new Mock<IGetCoverageSummary_CoverageSummary>();
        // Moq returns default(int)/default(double) for unset properties — all zeros is correct for empty data

        var mockData = new Mock<IGetCoverageSummaryResult>();
        mockData.Setup(d => d.CoverageSummary).Returns(mockSummary.Object);

        var mockResult = new Mock<IOperationResult<IGetCoverageSummaryResult>>();
        mockResult.Setup(r => r.Data).Returns(mockData.Object);
        mockResult.Setup(r => r.Errors).Returns([]);

        return mockResult.Object;
    }

    private static IOperationResult<IGetScenariosResult> MakeEmptyScenariosResult()
    {
        var mockData = new Mock<IGetScenariosResult>();
        mockData.Setup(d => d.Scenarios).Returns([]);

        var mockResult = new Mock<IOperationResult<IGetScenariosResult>>();
        mockResult.Setup(r => r.Data).Returns(mockData.Object);
        mockResult.Setup(r => r.Errors).Returns([]);

        return mockResult.Object;
    }
}
