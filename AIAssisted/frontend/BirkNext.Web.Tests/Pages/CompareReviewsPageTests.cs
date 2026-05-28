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

public class CompareReviewsPageTests : BunitContext
{
    [Fact]
    public void Page_LoadsReviews_AndDisplaysThem()
    {
        var mockQuery = SetupGetReviewsQuery([MakeReview("rev-1", "Review One"), MakeReview("rev-2", "Review Two")]);
        var mockClient = MakeClient(getReviewsQuery: mockQuery);
        Services.AddSingleton(mockClient.Object);

        var cut = Render<CompareReviews>();

        cut.WaitForAssertion(() =>
            cut.FindAll("[data-testid='review-card']").Should().HaveCount(2),
            timeout: TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void Page_EmptyList_ShowsEmptyState()
    {
        var mockQuery = SetupGetReviewsQuery([]);
        var mockClient = MakeClient(getReviewsQuery: mockQuery);
        Services.AddSingleton(mockClient.Object);

        var cut = Render<CompareReviews>();

        cut.WaitForAssertion(() =>
            cut.Find("[data-testid='review-list-empty']").Should().NotBeNull(),
            timeout: TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void Page_LoadError_ShowsErrorMessage()
    {
        var mockQuery = new Mock<IGetQaDeltaReviewsQuery>();
        mockQuery
            .Setup(q => q.ExecuteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Backend unavailable"));

        var mockClient = MakeClient(getReviewsQuery: mockQuery);
        Services.AddSingleton(mockClient.Object);

        var cut = Render<CompareReviews>();

        cut.WaitForAssertion(() =>
        {
            cut.Find("[data-testid='reviews-load-error']")
                .TextContent.Should().Contain("couldn't load delta reviews");
        }, timeout: TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void Page_Delete_RemovesReviewFromList()
    {
        var mockGetQuery = SetupGetReviewsQuery([MakeReview("rev-1", "Keep me"), MakeReview("rev-2", "Delete me")]);

        var mockDeletePayload = new Mock<IDeleteQaDeltaReview_DeleteQaDeltaReview>();
        mockDeletePayload.Setup(p => p.Success).Returns(true);
        mockDeletePayload.Setup(p => p.DeletedId).Returns("rev-2");
        mockDeletePayload.Setup(p => p.Errors).Returns([]);
        mockDeletePayload.Setup(p => p.CorrelationId).Returns("corr-1");

        var mockDeleteData = new Mock<IDeleteQaDeltaReviewResult>();
        mockDeleteData.Setup(d => d.DeleteQaDeltaReview).Returns(mockDeletePayload.Object);

        var mockDeleteResult = new Mock<IOperationResult<IDeleteQaDeltaReviewResult>>();
        mockDeleteResult.Setup(r => r.Data).Returns(mockDeleteData.Object);

        var mockDeleteMutation = new Mock<IDeleteQaDeltaReviewMutation>();
        mockDeleteMutation
            .Setup(m => m.ExecuteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockDeleteResult.Object);

        var mockClient = MakeClient(getReviewsQuery: mockGetQuery, deleteReviewMutation: mockDeleteMutation);
        Services.AddSingleton(mockClient.Object);

        var cut = Render<CompareReviews>();

        cut.WaitForAssertion(() =>
            cut.FindAll("[data-testid='review-card']").Should().HaveCount(2),
            timeout: TimeSpan.FromSeconds(1));

        cut.Find("[data-testid='delete-btn-rev-2']").Click();
        cut.Find("[data-testid='delete-confirm-btn']").Click();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll("[data-testid='review-card']").Should().ContainSingle();
            cut.Markup.Should().Contain("Keep me");
            cut.Markup.Should().NotContain("Delete me");
        }, timeout: TimeSpan.FromSeconds(1));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static Mock<IGetQaDeltaReviewsQuery> SetupGetReviewsQuery(
        IReadOnlyList<IGetQaDeltaReviews_QaDeltaReviews> reviews)
    {
        var mockData = new Mock<IGetQaDeltaReviewsResult>();
        mockData.Setup(d => d.QaDeltaReviews).Returns(reviews);

        var mockResult = new Mock<IOperationResult<IGetQaDeltaReviewsResult>>();
        mockResult.Setup(r => r.Data).Returns(mockData.Object);
        mockResult.Setup(r => r.Errors).Returns([]);

        var mockQuery = new Mock<IGetQaDeltaReviewsQuery>();
        mockQuery
            .Setup(q => q.ExecuteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockResult.Object);
        return mockQuery;
    }

    private static Mock<IBirkNextClient> MakeClient(
        Mock<IGetQaDeltaReviewsQuery>? getReviewsQuery = null,
        Mock<IDeleteQaDeltaReviewMutation>? deleteReviewMutation = null)
    {
        var mockClient = new Mock<IBirkNextClient>();
        if (getReviewsQuery is not null)
            mockClient.Setup(c => c.GetQaDeltaReviews).Returns(getReviewsQuery.Object);
        if (deleteReviewMutation is not null)
            mockClient.Setup(c => c.DeleteQaDeltaReview).Returns(deleteReviewMutation.Object);
        return mockClient;
    }

    private static IGetQaDeltaReviews_QaDeltaReviews MakeReview(string id, string title)
    {
        var summaryJson = """{"addedRequirements":0,"modifiedRequirements":0,"removedRequirements":0,"unchangedRequirements":0,"addedTests":0,"removedTests":0,"potentiallyImpactedTests":0,"addedClarifications":0,"removedClarifications":0,"stillUnresolvedClarifications":0,"uncoveredRequirements":0,"newClarificationRisks":0}""";
        var mock = new Mock<IGetQaDeltaReviews_QaDeltaReviews>();
        mock.Setup(r => r.Id).Returns(id);
        mock.Setup(r => r.Title).Returns(title);
        mock.Setup(r => r.ProjectId).Returns("proj-1");
        mock.Setup(r => r.CreatedAt).Returns(DateTimeOffset.UtcNow);
        mock.Setup(r => r.OldSpecFileName).Returns((string?)null);
        mock.Setup(r => r.NewSpecFileName).Returns((string?)null);
        mock.Setup(r => r.AnalysisProfile).Returns("Speckit");
        mock.Setup(r => r.SummaryJson).Returns(summaryJson);
        return mock.Object;
    }
}
