using BirkNext.Web.GraphQL;
using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

public class DashboardMetricsServiceTests
{
    private readonly DashboardMetricsService _service = new();

    [Fact]
    public void Calculate_EmptyData_ReturnsZeroMetrics()
    {
        var metrics = _service.Calculate([], []);

        metrics.TotalCandidates.Should().Be(0);
        metrics.ReviewedPercent.Should().Be(0);
        metrics.RequirementsWithoutTests.Should().Be(0);
        metrics.TestsWithoutRequirements.Should().Be(0);
        metrics.ClarificationsWithoutRequirements.Should().Be(0);
    }

    [Fact]
    public void Calculate_ComputesCoverageRiskQueueAndQualityMetrics()
    {
        var candidates = new List<DashboardCandidate>
        {
            new("req-1", ScenarioKind.Requirement, CandidateReviewStatus.Accepted),
            new("req-2", ScenarioKind.Requirement, CandidateReviewStatus.New),
            new("test-1", ScenarioKind.Test, CandidateReviewStatus.Accepted),
            new("test-2", ScenarioKind.Test, CandidateReviewStatus.Rejected),
            new("clr-1", ScenarioKind.NeedsClarification, CandidateReviewStatus.NeedsReview),
            new("clr-2", ScenarioKind.NeedsClarification, CandidateReviewStatus.New),
        };

        var links = new List<DashboardCandidateLink>
        {
            new("req-1", "test-1", CandidateLinkType.RequirementTest),
            new("req-2", "clr-1", CandidateLinkType.RequirementClarification),
        };

        var metrics = _service.Calculate(candidates, links);

        metrics.ReviewedPercent.Should().Be(67);
        metrics.AcceptedCount.Should().Be(2);
        metrics.RejectedCount.Should().Be(1);
        metrics.RequirementsWithTests.Should().Be(1);
        metrics.RequirementsWithoutTests.Should().Be(1);
        metrics.TestsWithoutRequirements.Should().Be(1);
        metrics.RequirementsWithUnresolvedClarifications.Should().Be(1);
        metrics.UnresolvedClarifications.Should().Be(2);
        metrics.ClarificationsWithoutRequirements.Should().Be(1);
        metrics.PendingRequirements.Should().Be(1);
        metrics.PendingTests.Should().Be(0);
        metrics.PendingClarifications.Should().Be(2);
    }
}
