using BirkNext.Web.GraphQL;

namespace BirkNext.Web.Services;

public sealed record DashboardCandidate(
    string Id,
    ScenarioKind Classification,
    CandidateReviewStatus ReviewStatus);

public sealed record DashboardCandidateLink(
    string SourceCandidateRef,
    string TargetCandidateRef,
    CandidateLinkType LinkType);

public sealed record DashboardMetrics(
    int TotalCandidates,
    int RequirementCount,
    int TestCount,
    int ClarificationCount,
    int ReviewedCount,
    int ReviewedPercent,
    int AcceptedCount,
    int RejectedCount,
    int NeedsReviewCount,
    int UnreviewedCount,
    int AcceptanceRatio,
    int RejectionRatio,
    int RequirementsWithTests,
    int RequirementsWithoutTests,
    int RequirementsCoveredPercent,
    int RequirementsWithoutTestsPercent,
    int TestsWithoutRequirements,
    int TestsLinkedToRequirementsPercent,
    int RequirementsWithUnresolvedClarifications,
    int UnresolvedClarifications,
    int ClarificationsWithoutRequirements,
    int PendingRequirements,
    int PendingTests,
    int PendingClarifications);

public interface IDashboardMetricsService
{
    DashboardMetrics Calculate(
        IReadOnlyList<DashboardCandidate> candidates,
        IReadOnlyList<DashboardCandidateLink> links);
}

public sealed class DashboardMetricsService : IDashboardMetricsService
{
    public DashboardMetrics Calculate(
        IReadOnlyList<DashboardCandidate> candidates,
        IReadOnlyList<DashboardCandidateLink> links)
    {
        var requirements = candidates.Where(c => c.Classification == ScenarioKind.Requirement).ToList();
        var tests = candidates.Where(c => c.Classification == ScenarioKind.Test).ToList();
        var clarifications = candidates.Where(c => c.Classification == ScenarioKind.NeedsClarification).ToList();

        var requirementIds = requirements.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        var testIds = tests.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        var clarificationIds = clarifications.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);

        var requirementsWithTests = requirements.Count(c =>
            links.Any(l => l.LinkType == CandidateLinkType.RequirementTest && Touches(l, c.Id, testIds)));
        var requirementsWithoutTests = requirements.Count - requirementsWithTests;

        var testsWithoutRequirements = tests.Count(c =>
            !links.Any(l => l.LinkType == CandidateLinkType.RequirementTest && Touches(l, c.Id, requirementIds)));

        var clarificationsWithoutRequirements = clarifications.Count(c =>
            !links.Any(l => l.LinkType == CandidateLinkType.RequirementClarification && Touches(l, c.Id, requirementIds)));

        var unresolvedClarificationIds = clarifications
            .Where(IsUnresolved)
            .Select(c => c.Id)
            .ToHashSet(StringComparer.Ordinal);

        var requirementsWithUnresolvedClarifications = requirements.Count(c =>
            links.Any(l => l.LinkType == CandidateLinkType.RequirementClarification &&
                           Touches(l, c.Id, unresolvedClarificationIds)));

        var accepted = candidates.Count(c => c.ReviewStatus == CandidateReviewStatus.Accepted);
        var rejected = candidates.Count(c => c.ReviewStatus == CandidateReviewStatus.Rejected);
        var needsReview = candidates.Count(c => c.ReviewStatus == CandidateReviewStatus.NeedsReview);
        var unreviewed = candidates.Count(c => c.ReviewStatus == CandidateReviewStatus.New);
        var reviewed = accepted + rejected + needsReview;

        var testsWithRequirements = tests.Count - testsWithoutRequirements;

        return new DashboardMetrics(
            TotalCandidates: candidates.Count,
            RequirementCount: requirements.Count,
            TestCount: tests.Count,
            ClarificationCount: clarifications.Count,
            ReviewedCount: reviewed,
            ReviewedPercent: Percent(reviewed, candidates.Count),
            AcceptedCount: accepted,
            RejectedCount: rejected,
            NeedsReviewCount: needsReview,
            UnreviewedCount: unreviewed,
            AcceptanceRatio: Percent(accepted, candidates.Count),
            RejectionRatio: Percent(rejected, candidates.Count),
            RequirementsWithTests: requirementsWithTests,
            RequirementsWithoutTests: requirementsWithoutTests,
            RequirementsCoveredPercent: Percent(requirementsWithTests, requirements.Count),
            RequirementsWithoutTestsPercent: Percent(requirementsWithoutTests, requirements.Count),
            TestsWithoutRequirements: testsWithoutRequirements,
            TestsLinkedToRequirementsPercent: Percent(testsWithRequirements, tests.Count),
            RequirementsWithUnresolvedClarifications: requirementsWithUnresolvedClarifications,
            UnresolvedClarifications: clarifications.Count(IsUnresolved),
            ClarificationsWithoutRequirements: clarificationsWithoutRequirements,
            PendingRequirements: requirements.Count(IsUnresolved),
            PendingTests: tests.Count(IsUnresolved),
            PendingClarifications: clarifications.Count(IsUnresolved));
    }

    private static bool Touches(DashboardCandidateLink link, string candidateId, HashSet<string> counterpartIds) =>
        string.Equals(link.SourceCandidateRef, candidateId, StringComparison.Ordinal)
            ? counterpartIds.Contains(link.TargetCandidateRef)
            : string.Equals(link.TargetCandidateRef, candidateId, StringComparison.Ordinal) &&
              counterpartIds.Contains(link.SourceCandidateRef);

    private static bool IsUnresolved(DashboardCandidate candidate) =>
        candidate.ReviewStatus is CandidateReviewStatus.New or CandidateReviewStatus.NeedsReview;

    private static int Percent(int value, int total) =>
        total == 0 ? 0 : (int)Math.Round(value * 100.0 / total, MidpointRounding.AwayFromZero);
}
