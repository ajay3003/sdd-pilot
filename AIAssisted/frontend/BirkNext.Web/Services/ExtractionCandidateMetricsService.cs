using BirkNext.Web.GraphQL;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

/// <summary>
/// Centralized metrics calculations for extraction candidates (session artifacts).
/// Single source of truth for coverage, gap, and status metrics.
/// </summary>
public sealed class ExtractionCandidateMetricsService : IExtractionCandidateMetricsService
{
    /// <summary>
    /// Count requirements with at least one linked test.
    /// </summary>
    public int CountRequirementsWithTests(IReadOnlyList<ExtractionCandidate> candidates, IReadOnlyList<CandidateLinkEntry> links)
    {
        var testIds = new HashSet<Guid>(
            candidates.Where(c => c.Classification == ScenarioKind.Test).Select(c => c.CandidateId));

        return candidates.Count(c =>
            c.Classification == ScenarioKind.Requirement &&
            links.Any(l => (l.LinkType == CandidateLinkType.RequirementTest) &&
                          ((l.SourceId == c.CandidateId && testIds.Contains(l.TargetId)) ||
                           (l.TargetId == c.CandidateId && testIds.Contains(l.SourceId)))));
    }

    /// <summary>
    /// Count requirements with no linked tests.
    /// </summary>
    public int CountRequirementsWithoutTests(IReadOnlyList<ExtractionCandidate> candidates, IReadOnlyList<CandidateLinkEntry> links)
    {
        var requirementsWithTests = CountRequirementsWithTests(candidates, links);
        var totalRequirements = candidates.Count(c => c.Classification == ScenarioKind.Requirement);
        return totalRequirements - requirementsWithTests;
    }

    /// <summary>
    /// Count requirements with at least one linked clarification.
    /// </summary>
    public int CountRequirementsWithClarifications(IReadOnlyList<ExtractionCandidate> candidates, IReadOnlyList<CandidateLinkEntry> links)
    {
        var clarificationIds = new HashSet<Guid>(
            candidates.Where(c => c.Classification == ScenarioKind.NeedsClarification).Select(c => c.CandidateId));

        return candidates.Count(c =>
            c.Classification == ScenarioKind.Requirement &&
            links.Any(l => (l.LinkType == CandidateLinkType.RequirementClarification) &&
                          ((l.SourceId == c.CandidateId && clarificationIds.Contains(l.TargetId)) ||
                           (l.TargetId == c.CandidateId && clarificationIds.Contains(l.SourceId)))));
    }

    /// <summary>
    /// Count tests with no linked requirements.
    /// </summary>
    public int CountTestsWithoutRequirements(IReadOnlyList<ExtractionCandidate> candidates, IReadOnlyList<CandidateLinkEntry> links)
    {
        var requirementIds = new HashSet<Guid>(
            candidates.Where(c => c.Classification == ScenarioKind.Requirement).Select(c => c.CandidateId));

        return candidates.Count(c =>
            c.Classification == ScenarioKind.Test &&
            !links.Any(l => l.LinkType == CandidateLinkType.RequirementTest &&
                           ((l.SourceId == c.CandidateId && requirementIds.Contains(l.TargetId)) ||
                            (l.TargetId == c.CandidateId && requirementIds.Contains(l.SourceId)))));
    }

    /// <summary>
    /// Count clarifications with no linked requirements.
    /// </summary>
    public int CountClarificationsWithoutRequirements(IReadOnlyList<ExtractionCandidate> candidates, IReadOnlyList<CandidateLinkEntry> links)
    {
        var requirementIds = new HashSet<Guid>(
            candidates.Where(c => c.Classification == ScenarioKind.Requirement).Select(c => c.CandidateId));

        return candidates.Count(c =>
            c.Classification == ScenarioKind.NeedsClarification &&
            !links.Any(l => l.LinkType == CandidateLinkType.RequirementClarification &&
                           ((l.SourceId == c.CandidateId && requirementIds.Contains(l.TargetId)) ||
                            (l.TargetId == c.CandidateId && requirementIds.Contains(l.SourceId)))));
    }

    /// <summary>
    /// Count unresolved (New or NeedsReview) clarifications.
    /// </summary>
    public int CountUnresolvedClarifications(IReadOnlyList<ExtractionCandidate> candidates)
    {
        return candidates.Count(c =>
            c.Classification == ScenarioKind.NeedsClarification &&
            (c.ReviewStatus == CandidateReviewStatus.New || c.ReviewStatus == CandidateReviewStatus.NeedsReview));
    }

    /// <summary>
    /// Count requirements with unresolved clarifications linked to them.
    /// </summary>
    public int CountRequirementsWithUnresolvedClarifications(IReadOnlyList<ExtractionCandidate> candidates, IReadOnlyList<CandidateLinkEntry> links)
    {
        var unresolvedClarificationIds = candidates
            .Where(c => c.Classification == ScenarioKind.NeedsClarification &&
                       (c.ReviewStatus == CandidateReviewStatus.New || c.ReviewStatus == CandidateReviewStatus.NeedsReview))
            .Select(c => c.CandidateId)
            .ToHashSet();

        return candidates.Count(c =>
            c.Classification == ScenarioKind.Requirement &&
            links.Any(l => l.LinkType == CandidateLinkType.RequirementClarification &&
                          ((l.SourceId == c.CandidateId && unresolvedClarificationIds.Contains(l.TargetId)) ||
                           (l.TargetId == c.CandidateId && unresolvedClarificationIds.Contains(l.SourceId)))));
    }

    /// <summary>
    /// Count unresolved (New or NeedsReview) candidates of a specific kind.
    /// </summary>
    public int CountPending(IReadOnlyList<ExtractionCandidate> candidates, ScenarioKind kind)
    {
        return candidates.Count(c =>
            c.Classification == kind &&
            (c.ReviewStatus == CandidateReviewStatus.New || c.ReviewStatus == CandidateReviewStatus.NeedsReview));
    }
}

/// <summary>
/// Interface for extraction candidate metrics calculations.
/// </summary>
public interface IExtractionCandidateMetricsService
{
    int CountRequirementsWithTests(IReadOnlyList<ExtractionCandidate> candidates, IReadOnlyList<CandidateLinkEntry> links);
    int CountRequirementsWithoutTests(IReadOnlyList<ExtractionCandidate> candidates, IReadOnlyList<CandidateLinkEntry> links);
    int CountRequirementsWithClarifications(IReadOnlyList<ExtractionCandidate> candidates, IReadOnlyList<CandidateLinkEntry> links);
    int CountTestsWithoutRequirements(IReadOnlyList<ExtractionCandidate> candidates, IReadOnlyList<CandidateLinkEntry> links);
    int CountClarificationsWithoutRequirements(IReadOnlyList<ExtractionCandidate> candidates, IReadOnlyList<CandidateLinkEntry> links);
    int CountUnresolvedClarifications(IReadOnlyList<ExtractionCandidate> candidates);
    int CountRequirementsWithUnresolvedClarifications(IReadOnlyList<ExtractionCandidate> candidates, IReadOnlyList<CandidateLinkEntry> links);
    int CountPending(IReadOnlyList<ExtractionCandidate> candidates, ScenarioKind kind);
}
