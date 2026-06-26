using BirkNext.Web.GraphQL;

namespace BirkNext.Web.Services;

public sealed record DashboardCandidate(
    string Id,
    ScenarioKind Classification,
    CandidateReviewStatus ReviewStatus,
    string Title = "");

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
    int PendingClarifications,
    int FunctionalTests,
    int NegativeTests,
    int EdgeCaseTests,
    int PerformanceTests,
    int SecurityTests,
    int OtherTests,
    int TraceabilityPercent,
    int QaHealthScore,
    int OpenRisksCount);

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

        ClassifyTests(tests, out var functional, out var negative, out var edge, out var performance, out var security, out var other);

        var unresolvedClarifications = clarifications.Count(IsUnresolved);
        var reviewedPct = Percent(reviewed, candidates.Count);
        var coveredPct = Percent(requirementsWithTests, requirements.Count);
        var testsLinkedPct = Percent(testsWithRequirements, tests.Count);
        var clarificationsLinkedPct = Percent(clarifications.Count - clarificationsWithoutRequirements, clarifications.Count);

        var traceabilityPercent = AverageNonEmpty(
            (requirements.Count, coveredPct),
            (tests.Count, testsLinkedPct),
            (clarifications.Count, clarificationsLinkedPct));

        // Health score: coverage-based only — review progress is not a quality signal
        var qaBaseComponents = new List<int>();
        if (requirements.Count > 0) qaBaseComponents.Add(coveredPct);
        if (tests.Count + clarifications.Count > 0) qaBaseComponents.Add(traceabilityPercent);
        var qaBase = qaBaseComponents.Count > 0
            ? (int)Math.Round(qaBaseComponents.Average(), MidpointRounding.AwayFromZero)
            : 0;
        var clarificationPenalty = Math.Min(unresolvedClarifications * 5, 20);
        var qaHealthScore = Math.Max(0, qaBase - clarificationPenalty);

        var openRisksCount = 0;
        if (requirements.Count > 0 && requirementsWithTests == 0) openRisksCount++;
        if (requirements.Count > 0 && coveredPct < 70 && requirementsWithTests > 0) openRisksCount++;
        if (unresolvedClarifications > 0) openRisksCount++;
        if (testsWithoutRequirements > 0) openRisksCount++;

        return new DashboardMetrics(
            TotalCandidates: candidates.Count,
            RequirementCount: requirements.Count,
            TestCount: tests.Count,
            ClarificationCount: clarifications.Count,
            ReviewedCount: reviewed,
            ReviewedPercent: reviewedPct,
            AcceptedCount: accepted,
            RejectedCount: rejected,
            NeedsReviewCount: needsReview,
            UnreviewedCount: unreviewed,
            AcceptanceRatio: Percent(accepted, candidates.Count),
            RejectionRatio: Percent(rejected, candidates.Count),
            RequirementsWithTests: requirementsWithTests,
            RequirementsWithoutTests: requirementsWithoutTests,
            RequirementsCoveredPercent: coveredPct,
            RequirementsWithoutTestsPercent: Percent(requirementsWithoutTests, requirements.Count),
            TestsWithoutRequirements: testsWithoutRequirements,
            TestsLinkedToRequirementsPercent: testsLinkedPct,
            RequirementsWithUnresolvedClarifications: requirementsWithUnresolvedClarifications,
            UnresolvedClarifications: unresolvedClarifications,
            ClarificationsWithoutRequirements: clarificationsWithoutRequirements,
            PendingRequirements: requirements.Count(IsUnresolved),
            PendingTests: tests.Count(IsUnresolved),
            PendingClarifications: clarifications.Count(IsUnresolved),
            FunctionalTests: functional,
            NegativeTests: negative,
            EdgeCaseTests: edge,
            PerformanceTests: performance,
            SecurityTests: security,
            OtherTests: other,
            TraceabilityPercent: traceabilityPercent,
            QaHealthScore: qaHealthScore,
            OpenRisksCount: openRisksCount);
    }

    private static int AverageNonEmpty(params (int Total, int Percent)[] values)
    {
        var nonEmpty = values.Where(v => v.Total > 0).Select(v => v.Percent).ToList();
        return nonEmpty.Count == 0 ? 0 : (int)Math.Round(nonEmpty.Average(), MidpointRounding.AwayFromZero);
    }

    private static void ClassifyTests(
        IReadOnlyList<DashboardCandidate> tests,
        out int functional, out int negative, out int edge,
        out int performance, out int security, out int other)
    {
        functional = 0; negative = 0; edge = 0; performance = 0; security = 0; other = 0;
        foreach (var test in tests)
        {
            var t = (test.Title ?? "").ToLowerInvariant();
            if (ContainsAny(t, "error", "invalid", "fail", "exception", "not allowed", "unauthorized"))
                negative++;
            else if (ContainsAny(t, "boundary", "edge", "empty", "null", "max", "min"))
                edge++;
            else if (ContainsAny(t, "performance", "load", "response time", "latency", "timeout"))
                performance++;
            else if (ContainsAny(t, "security", "role", "permission", "authentication", "access"))
                security++;
            else
                functional++;
        }
    }

    private static bool ContainsAny(string text, params string[] keywords) =>
        keywords.Any(k => text.Contains(k, StringComparison.Ordinal));

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
