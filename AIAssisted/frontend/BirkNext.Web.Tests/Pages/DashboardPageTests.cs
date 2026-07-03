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
    private readonly Mock<IWorkflowReadinessService> _workflowReadiness = new();

    public DashboardPageTests()
    {
        Services.AddSingleton<IDashboardMetricsService, DashboardMetricsService>();
        Services.AddSingleton(new Mock<IReportExportService>().Object);
        var artifactStatus = new Mock<IWorkspaceArtifactStatusService>();
        artifactStatus.Setup(service => service.GetStatus())
            .Returns(new WorkspaceArtifactStatus(false, false, false, false, false, 0, null));
        Services.AddSingleton(artifactStatus.Object);
        Services.AddSingleton(new Mock<IWorkspaceSessionService>().Object);
        Services.AddSingleton(new Mock<IDashboardSnapshotService>().Object);
        Services.AddSingleton(new RuntimeReviewSessionService());
        Services.AddSingleton(new QualityReviewSessionService());
        _workflowReadiness
            .Setup(service => service.GetReadinessAsync())
            .ReturnsAsync(EmptyWorkflowReadiness());
        Services.AddSingleton(_workflowReadiness.Object);
    }

    [Fact]
    public void DashboardRoute_RendersMetrics()
    {
        var candidate = MakeReviewedCandidate("req-1", ScenarioKind.Requirement, CandidateReviewStatus.Accepted);
        RegisterClient([candidate], []);

        var cut = Render<Dashboard>();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("SDD Governance Dashboard");
            cut.Markup.Should().Contain("Project Health");
            cut.Markup.Should().Contain("Workflow");
            cut.Markup.Should().Contain("0%");
            cut.Markup.Should().Contain("Not Started");
        }, timeout: TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void DashboardMetrics_RenderWithEmptyData()
    {
        RegisterClient([], []);

        var cut = Render<Dashboard>();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("0%");
            cut.Markup.Should().Contain("Not Started");
            cut.Markup.Should().Contain("No workspace loaded");
            cut.Markup.Should().Contain("Project Health");
            cut.Markup.Should().Contain("Traceability");
            cut.Markup.Should().Contain("Top Risks");
        }, timeout: TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void DashboardWorkflowMetric_UsesSharedReadinessForLoadedWorkspace()
    {
        _workflowReadiness
            .Setup(service => service.GetReadinessAsync())
            .ReturnsAsync(LoadedWorkflowReadiness());
        RegisterClient([], []);

        var cut = Render<Dashboard>();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("30%");
            cut.Markup.Should().Contain("Started");
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
            cut.Markup.Should().Contain("SDD Governance Dashboard");
            cut.Markup.Should().Contain("Governance Status");
            cut.Markup.Should().Contain("Readiness Summary");
            cut.Markup.Should().Contain("Analysis Summary");
            cut.Markup.Should().Contain("Quick Actions");
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
            cut.Markup.Should().Contain("SDD Governance Dashboard");
            cut.Markup.Should().Contain("No workspace loaded");
            cut.Markup.Should().Contain("Not Started");
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

    private static WorkflowReadiness EmptyWorkflowReadiness() =>
        new(
            CurrentWorkspace: null,
            WorkspaceLoaded: false,
            WorkspaceName: "No workspace loaded",
            ProjectName: "No project loaded",
            WorkspaceStatus: "Not Saved",
            WorkspaceStatusClass: "status-not-saved",
            LastSavedAt: null,
            LastSavedText: "-",
            ArtifactStatus: new WorkspaceArtifactStatus(false, false, false, false, false, 0, null),
            Artifacts: [],
            SpecificationReviewState: null,
            TraceabilityState: null,
            ImplementationReviewState: null,
            QualityGateState: null,
            NextRecommendedAction: null,
            OverallReadiness: new WorkflowReadinessBreakdown(),
            Steps: [],
            CanRelease: false,
            ReleaseReason: "Load a workspace before release readiness can be evaluated.",
            Warnings: []);

    private static WorkflowReadiness LoadedWorkflowReadiness() =>
        EmptyWorkflowReadiness() with
        {
            CurrentWorkspace = new WorkflowWorkspace(
                WorkspaceId: Guid.Parse("11111111-1111-1111-1111-111111111111"),
                WorkspaceName: "Saved workspace",
                ProjectName: "Sample Project",
                ArtifactCount: 3,
                LoadedAt: DateTimeOffset.UtcNow,
                ArtifactSetHash: null,
                AutoSaved: false),
            WorkspaceLoaded = true,
            WorkspaceName = "Saved workspace",
            ProjectName = "Sample Project",
            ArtifactStatus = new WorkspaceArtifactStatus(true, true, true, false, false, 3, "Sample Project"),
            OverallReadiness = new WorkflowReadinessBreakdown
            {
                ArtifactReadiness = 60,
                ReviewReadiness = 0,
                ApprovalReadiness = 0,
                OverallReadiness = 30
            }
        };
}
