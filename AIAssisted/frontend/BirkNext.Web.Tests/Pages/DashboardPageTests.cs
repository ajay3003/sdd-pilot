using BirkNext.Web.GraphQL;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using StrawberryShake;

namespace BirkNext.Web.Tests.Pages;

public class DashboardPageTests : BunitContext
{
    public DashboardPageTests()
    {
        Services.AddSingleton<IDashboardMetricsService, DashboardMetricsService>();
    }

    [Fact]
    public void DashboardRoute_RendersMetrics()
    {
        var candidate = MakeReviewedCandidate("req-1", ScenarioKind.Requirement, CandidateReviewStatus.Accepted);
        RegisterClient([candidate], []);

        var cut = Render<Dashboard>();

        cut.WaitForAssertion(() =>
        {
            cut.Find("[data-testid='coverage-dashboard']").TextContent.Should().Contain("Review Progress");
            cut.Markup.Should().Contain("Dashboard");
        }, timeout: TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void DashboardMetrics_RenderWithEmptyData()
    {
        RegisterClient([], []);

        var cut = Render<Dashboard>();

        cut.WaitForAssertion(() =>
        {
            var dashboard = cut.Find("[data-testid='coverage-dashboard']").TextContent;
            dashboard.Should().Contain("QA Health Score");
            dashboard.Should().Contain("No Data");
            dashboard.Should().Contain("Run a specification review to generate QA health, coverage, risk, and traceability metrics.");
            dashboard.Should().Contain("--");
            dashboard.Should().Contain("Coverage");
            dashboard.Should().Contain("Review Progress");
            dashboard.Should().Contain("Open Risks");
            dashboard.Should().Contain("Traceability");
            dashboard.Should().Contain("Top QA Risks");
            dashboard.Should().Contain("Quality Overview");
            dashboard.Should().Contain("Test Coverage Breakdown");
            cut.Find("[data-testid='dashboard-empty-state'] a[href='extract']").Should().NotBeNull();
            cut.FindAll("[data-testid='dashboard-health-card']").Should().HaveCount(4);
        }, timeout: TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void DashboardMetrics_RenderHealthProgressRisksAndActivitySummary()
    {
        var candidates = new List<IGetReviewedCandidates_ReviewedCandidates>
        {
            MakeReviewedCandidate("req-1", ScenarioKind.Requirement, CandidateReviewStatus.Accepted),
            MakeReviewedCandidate("req-2", ScenarioKind.Requirement, CandidateReviewStatus.New),
            MakeReviewedCandidate("test-1", ScenarioKind.Test, CandidateReviewStatus.Accepted),
            MakeReviewedCandidate("test-2", ScenarioKind.Test, CandidateReviewStatus.Rejected),
            MakeReviewedCandidate("clr-1", ScenarioKind.NeedsClarification, CandidateReviewStatus.NeedsReview),
        };
        var links = new List<IGetCandidateLinks_CandidateLinks>
        {
            MakeCandidateLink("req-1", "test-1", CandidateLinkType.RequirementTest),
            MakeCandidateLink("req-2", "clr-1", CandidateLinkType.RequirementClarification),
        };
        RegisterClient(candidates, links);

        var cut = Render<Dashboard>();

        cut.WaitForAssertion(() =>
        {
            var dashboard = cut.Find("[data-testid='coverage-dashboard']").TextContent;
            dashboard.Should().Contain("QA Health Score");
            dashboard.Should().Contain("Coverage");
            dashboard.Should().Contain("Review Progress");
            dashboard.Should().Contain("Traceability");
            dashboard.Should().Contain("Top QA Risks");
            dashboard.Should().Contain("Test coverage below 70%");
            dashboard.Should().Contain("clarification item");
            dashboard.Should().Contain("Quality Overview");
            dashboard.Should().Contain("Accepted");
            dashboard.Should().Contain("Rejected");
            cut.FindAll("[data-testid='dashboard-health-card']").Should().HaveCount(4);
        }, timeout: TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void Dashboard_ApiFailure_ShowsInlineError()
    {
        var reviewedQuery = new Mock<IGetReviewedCandidatesQuery>();
        reviewedQuery
            .Setup(q => q.ExecuteAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Backend unavailable"));

        var linksQuery = new Mock<IGetCandidateLinksQuery>();
        var client = new Mock<IBirkNextClient>();
        client.Setup(c => c.GetReviewedCandidates).Returns(reviewedQuery.Object);
        client.Setup(c => c.GetCandidateLinks).Returns(linksQuery.Object);
        Services.AddSingleton(client.Object);

        var cut = Render<Dashboard>();

        cut.WaitForAssertion(() =>
        {
            cut.Find("[data-testid='dashboard-load-error']")
                .TextContent.Should().Contain("couldn't load dashboard data");
            cut.Find("[data-testid='coverage-dashboard']").TextContent.Should().Contain("No Data");
        }, timeout: TimeSpan.FromSeconds(1));
    }

    private void RegisterClient(
        IReadOnlyList<IGetReviewedCandidates_ReviewedCandidates> candidates,
        IReadOnlyList<IGetCandidateLinks_CandidateLinks> links)
    {
        var reviewedQuery = new Mock<IGetReviewedCandidatesQuery>();
        reviewedQuery
            .Setup(q => q.ExecuteAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeReviewedResult(candidates));

        var linksQuery = new Mock<IGetCandidateLinksQuery>();
        linksQuery
            .Setup(q => q.ExecuteAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeLinksResult(links));

        var client = new Mock<IBirkNextClient>();
        client.Setup(c => c.GetReviewedCandidates).Returns(reviewedQuery.Object);
        client.Setup(c => c.GetCandidateLinks).Returns(linksQuery.Object);
        Services.AddSingleton(client.Object);
    }

    private static IOperationResult<IGetReviewedCandidatesResult> MakeReviewedResult(
        IReadOnlyList<IGetReviewedCandidates_ReviewedCandidates> candidates)
    {
        var data = new Mock<IGetReviewedCandidatesResult>();
        data.Setup(d => d.ReviewedCandidates).Returns(candidates);

        var result = new Mock<IOperationResult<IGetReviewedCandidatesResult>>();
        result.Setup(r => r.Data).Returns(data.Object);
        result.Setup(r => r.Errors).Returns([]);
        return result.Object;
    }

    private static IOperationResult<IGetCandidateLinksResult> MakeLinksResult(
        IReadOnlyList<IGetCandidateLinks_CandidateLinks> links)
    {
        var data = new Mock<IGetCandidateLinksResult>();
        data.Setup(d => d.CandidateLinks).Returns(links);

        var result = new Mock<IOperationResult<IGetCandidateLinksResult>>();
        result.Setup(r => r.Data).Returns(data.Object);
        result.Setup(r => r.Errors).Returns([]);
        return result.Object;
    }

    private static IGetReviewedCandidates_ReviewedCandidates MakeReviewedCandidate(
        string id,
        ScenarioKind classification,
        CandidateReviewStatus reviewStatus,
        string title = "")
    {
        var candidate = new Mock<IGetReviewedCandidates_ReviewedCandidates>();
        candidate.Setup(c => c.Id).Returns(id);
        candidate.Setup(c => c.Title).Returns(title);
        candidate.Setup(c => c.Classification).Returns(classification);
        candidate.Setup(c => c.ReviewStatus).Returns(reviewStatus);
        return candidate.Object;
    }

    private static IGetCandidateLinks_CandidateLinks MakeCandidateLink(
        string sourceCandidateRef,
        string targetCandidateRef,
        CandidateLinkType linkType)
    {
        var link = new Mock<IGetCandidateLinks_CandidateLinks>();
        link.Setup(l => l.SourceCandidateRef).Returns(sourceCandidateRef);
        link.Setup(l => l.TargetCandidateRef).Returns(targetCandidateRef);
        link.Setup(l => l.LinkType).Returns(linkType);
        return link.Object;
    }
}
