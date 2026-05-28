using BirkNext.Web.GraphQL;
using BirkNext.Web.Models;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using StrawberryShake;

namespace BirkNext.Web.Tests.Pages;

public class CompareReviewDetailTests : BunitContext
{
    public CompareReviewDetailTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public void Page_RendersTitle_FromLoadedReview()
    {
        var client = SetupClientWithReview("rev-1", "My Delta Review", modifiedReq: 1);
        Services.AddSingleton(client.Object);

        var cut = Render<CompareReviewDetail>(p => p.Add(x => x.Id, "rev-1"));

        cut.WaitForAssertion(() =>
            cut.Find("[data-testid='review-detail-header']").TextContent
                .Should().Contain("My Delta Review"),
            timeout: TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void Page_RendersDeltaSummary_FromDeserializedJson()
    {
        var client = SetupClientWithReview("rev-1", "Review", modifiedReq: 2);
        Services.AddSingleton(client.Object);

        var cut = Render<CompareReviewDetail>(p => p.Add(x => x.Id, "rev-1"));

        cut.WaitForAssertion(() =>
        {
            cut.Find("[data-testid='delta-summary-section']").Should().NotBeNull();
            cut.Find("[data-testid='delta-dashboard']").TextContent.Should().Contain("2");
        }, timeout: TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void Page_RendersDeltaCards_FromDeserializedJson()
    {
        var client = SetupClientWithReview("rev-1", "Review", modifiedReq: 1);
        Services.AddSingleton(client.Object);

        var cut = Render<CompareReviewDetail>(p => p.Add(x => x.Id, "rev-1"));

        cut.WaitForAssertion(() =>
            cut.FindAll("[data-testid='delta-card']").Should().ContainSingle(),
            timeout: TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void Page_FilterChips_WorkOnReopenedReview()
    {
        var client = SetupClientWithReview("rev-1", "Review", modifiedReq: 1);
        Services.AddSingleton(client.Object);

        var cut = Render<CompareReviewDetail>(p => p.Add(x => x.Id, "rev-1"));

        cut.WaitForAssertion(() =>
            cut.Find("[data-testid='change-explorer-section']").Should().NotBeNull(),
            timeout: TimeSpan.FromSeconds(1));

        cut.Find("[data-testid='filter-added']").Click();

        cut.Find("[data-testid='change-explorer-empty']").Should().NotBeNull();
    }

    [Fact]
    public void Page_EmptyDeltas_ShowsChangeExplorerEmpty()
    {
        var client = SetupClientWithReview("rev-1", "Empty Review", modifiedReq: 0);
        Services.AddSingleton(client.Object);

        var cut = Render<CompareReviewDetail>(p => p.Add(x => x.Id, "rev-1"));

        cut.WaitForAssertion(() =>
            cut.Find("[data-testid='change-explorer-empty']").Should().NotBeNull(),
            timeout: TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void Page_LoadError_ShowsInlineError()
    {
        var mockQuery = new Mock<IGetQaDeltaReviewQuery>();
        mockQuery
            .Setup(q => q.ExecuteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Backend unavailable"));

        var mockClient = new Mock<IBirkNextClient>();
        mockClient.Setup(c => c.GetQaDeltaReview).Returns(mockQuery.Object);
        Services.AddSingleton(mockClient.Object);

        var cut = Render<CompareReviewDetail>(p => p.Add(x => x.Id, "rev-1"));

        cut.WaitForAssertion(() =>
        {
            cut.Find("[data-testid='review-detail-load-error']")
                .TextContent.Should().Contain("couldn't load");
        }, timeout: TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void Page_SpecFileNames_ShownInHeader()
    {
        var client = SetupClientWithReview("rev-1", "Review", modifiedReq: 0,
            oldSpecFileName: "v1.0-spec.md", newSpecFileName: "v2.0-spec.md");
        Services.AddSingleton(client.Object);

        var cut = Render<CompareReviewDetail>(p => p.Add(x => x.Id, "rev-1"));

        cut.WaitForAssertion(() =>
        {
            var header = cut.Find("[data-testid='review-detail-header']").TextContent;
            header.Should().Contain("v1.0-spec.md");
            header.Should().Contain("v2.0-spec.md");
        }, timeout: TimeSpan.FromSeconds(1));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static Mock<IBirkNextClient> SetupClientWithReview(
        string id, string title, int modifiedReq,
        string? oldSpecFileName = null, string? newSpecFileName = null)
    {
        var (summaryJson, deltaItemsJson) = BuildJsonPayload(modifiedReq);

        var mockReview = new Mock<IGetQaDeltaReview_QaDeltaReview>();
        mockReview.Setup(r => r.Id).Returns(id);
        mockReview.Setup(r => r.Title).Returns(title);
        mockReview.Setup(r => r.ProjectId).Returns("proj-1");
        mockReview.Setup(r => r.CreatedAt).Returns(DateTimeOffset.UtcNow);
        mockReview.Setup(r => r.OldSpecFileName).Returns(oldSpecFileName);
        mockReview.Setup(r => r.NewSpecFileName).Returns(newSpecFileName);
        mockReview.Setup(r => r.AnalysisProfile).Returns("Speckit");
        mockReview.Setup(r => r.SummaryJson).Returns(summaryJson);
        mockReview.Setup(r => r.DeltaItemsJson).Returns(deltaItemsJson);

        var mockData = new Mock<IGetQaDeltaReviewResult>();
        mockData.Setup(d => d.QaDeltaReview).Returns(mockReview.Object);

        var mockResult = new Mock<IOperationResult<IGetQaDeltaReviewResult>>();
        mockResult.Setup(r => r.Data).Returns(mockData.Object);
        mockResult.Setup(r => r.Errors).Returns([]);

        var mockQuery = new Mock<IGetQaDeltaReviewQuery>();
        mockQuery
            .Setup(q => q.ExecuteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockResult.Object);

        var mockClient = new Mock<IBirkNextClient>();
        mockClient.Setup(c => c.GetQaDeltaReview).Returns(mockQuery.Object);
        return mockClient;
    }

    private static (string SummaryJson, string DeltaItemsJson) BuildJsonPayload(int modifiedReq)
    {
        var requirementDeltas = Enumerable.Range(0, modifiedReq)
            .Select(i => new SpecDeltaItem(
                SpecDeltaStatus.Modified,
                ScenarioKind.Requirement,
                new ExtractionCandidate
                {
                    Title = $"Old req {i}",
                    Classification = ScenarioKind.Requirement,
                    ClassificationSignal = ClassificationSignal.Rfc2119Uppercase,
                    SourceBlockType = BlockType.UnorderedListItem,
                },
                new ExtractionCandidate
                {
                    Title = $"New req {i}",
                    Classification = ScenarioKind.Requirement,
                    ClassificationSignal = ClassificationSignal.Rfc2119Uppercase,
                    SourceBlockType = BlockType.UnorderedListItem,
                },
                $"FR-{i:000}",
                []))
            .ToList();

        var summary = new SpecComparisonSummary(
            AddedRequirements: 0, ModifiedRequirements: modifiedReq,
            RemovedRequirements: 0, UnchangedRequirements: 0,
            AddedTests: 0, RemovedTests: 0, PotentiallyImpactedTests: 0,
            AddedClarifications: 0, RemovedClarifications: 0, StillUnresolvedClarifications: 0,
            UncoveredRequirements: 0, NewClarificationRisks: 0);

        var result = new SpecComparisonResult(requirementDeltas, [], [], summary);
        return QaDeltaReviewSerializer.Serialize(result);
    }
}
