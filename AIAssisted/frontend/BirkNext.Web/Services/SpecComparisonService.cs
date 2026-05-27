using System.Text.RegularExpressions;
using BirkNext.Web.GraphQL;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

public interface ISpecComparisonService
{
    SpecComparisonResult Compare(
        ExtractionPipelineResult oldSpec,
        ExtractionPipelineResult newSpec,
        IReadOnlyList<CandidateLinkEntry>? links = null);
}

public sealed partial class SpecComparisonService : ISpecComparisonService
{
    public SpecComparisonResult Compare(
        ExtractionPipelineResult oldSpec,
        ExtractionPipelineResult newSpec,
        IReadOnlyList<CandidateLinkEntry>? links = null)
    {
        ArgumentNullException.ThrowIfNull(oldSpec);
        ArgumentNullException.ThrowIfNull(newSpec);

        links ??= [];

        var requirementDeltas = CompareKind(oldSpec, newSpec, ScenarioKind.Requirement, links);
        var testDeltas = CompareKind(oldSpec, newSpec, ScenarioKind.Test, links);
        var clarificationDeltas = CompareKind(oldSpec, newSpec, ScenarioKind.NeedsClarification, links);

        var impactedTests = CountPotentiallyImpactedTests(oldSpec, requirementDeltas, links);
        var uncoveredRequirements = CountUncoveredRequirements(newSpec, requirementDeltas, links);
        var newClarificationRisks = CountNewClarificationRisks(clarificationDeltas);

        return new SpecComparisonResult(
            requirementDeltas,
            testDeltas,
            clarificationDeltas,
            new SpecComparisonSummary(
                AddedRequirements: requirementDeltas.Count(d => d.Status == SpecDeltaStatus.Added),
                ModifiedRequirements: requirementDeltas.Count(d => d.Status == SpecDeltaStatus.Modified),
                RemovedRequirements: requirementDeltas.Count(d => d.Status == SpecDeltaStatus.Removed),
                UnchangedRequirements: requirementDeltas.Count(d => d.Status == SpecDeltaStatus.Unchanged),
                AddedTests: testDeltas.Count(d => d.Status == SpecDeltaStatus.Added),
                RemovedTests: testDeltas.Count(d => d.Status == SpecDeltaStatus.Removed),
                PotentiallyImpactedTests: impactedTests,
                AddedClarifications: clarificationDeltas.Count(d => d.Status == SpecDeltaStatus.Added),
                RemovedClarifications: clarificationDeltas.Count(d => d.Status == SpecDeltaStatus.Removed),
                StillUnresolvedClarifications: CountStillUnresolvedClarifications(clarificationDeltas),
                UncoveredRequirements: uncoveredRequirements,
                NewClarificationRisks: newClarificationRisks));
    }

    private static IReadOnlyList<SpecDeltaItem> CompareKind(
        ExtractionPipelineResult oldSpec,
        ExtractionPipelineResult newSpec,
        ScenarioKind kind,
        IReadOnlyList<CandidateLinkEntry> links)
    {
        var oldItems = oldSpec.Candidates.Where(c => c.Classification == kind).ToList();
        var newItems = newSpec.Candidates.Where(c => c.Classification == kind).ToList();

        var oldByIdentity = oldItems
            .Select(c => new CandidateMatch(c, IdentityKey(c), ExactKey(c)))
            .ToList();
        var newByIdentity = newItems
            .Select(c => new CandidateMatch(c, IdentityKey(c), ExactKey(c)))
            .ToList();

        var matchedOld = new HashSet<Guid>();
        var matchedNew = new HashSet<Guid>();
        var deltas = new List<SpecDeltaItem>();

        foreach (var oldGroup in oldByIdentity.Where(m => m.IdentityKey is not null).GroupBy(m => m.IdentityKey!, StringComparer.Ordinal))
        {
            var newGroup = newByIdentity.Where(m => m.IdentityKey == oldGroup.Key).ToList();
            foreach (var pair in oldGroup.Zip(newGroup))
            {
                matchedOld.Add(pair.First.Candidate.CandidateId);
                matchedNew.Add(pair.Second.Candidate.CandidateId);

                var status = pair.First.ExactKey == pair.Second.ExactKey
                    ? SpecDeltaStatus.Unchanged
                    : SpecDeltaStatus.Modified;

                deltas.Add(new SpecDeltaItem(
                    status,
                    kind,
                    pair.First.Candidate,
                    pair.Second.Candidate,
                    oldGroup.Key,
                    BuildImpactHints(status, pair.First.Candidate, pair.Second.Candidate, links)));
            }
        }

        foreach (var oldMatch in oldByIdentity.Where(m => !matchedOld.Contains(m.Candidate.CandidateId)))
        {
            var exactMatch = newByIdentity.FirstOrDefault(m =>
                !matchedNew.Contains(m.Candidate.CandidateId) &&
                m.ExactKey == oldMatch.ExactKey);

            if (exactMatch is null)
                continue;

            matchedOld.Add(oldMatch.Candidate.CandidateId);
            matchedNew.Add(exactMatch.Candidate.CandidateId);
            deltas.Add(new SpecDeltaItem(
                SpecDeltaStatus.Unchanged,
                kind,
                oldMatch.Candidate,
                exactMatch.Candidate,
                oldMatch.ExactKey,
                []));
        }

        foreach (var oldMatch in oldByIdentity.Where(m => !matchedOld.Contains(m.Candidate.CandidateId)))
        {
            deltas.Add(new SpecDeltaItem(
                SpecDeltaStatus.Removed,
                kind,
                oldMatch.Candidate,
                null,
                oldMatch.IdentityKey ?? oldMatch.ExactKey,
                BuildImpactHints(SpecDeltaStatus.Removed, oldMatch.Candidate, null, links)));
        }

        foreach (var newMatch in newByIdentity.Where(m => !matchedNew.Contains(m.Candidate.CandidateId)))
        {
            deltas.Add(new SpecDeltaItem(
                SpecDeltaStatus.Added,
                kind,
                null,
                newMatch.Candidate,
                newMatch.IdentityKey ?? newMatch.ExactKey,
                BuildImpactHints(SpecDeltaStatus.Added, null, newMatch.Candidate, links)));
        }

        return deltas
            .OrderBy(d => SortRank(d.Status))
            .ThenBy(d => d.NewCandidate?.ContextHeading ?? d.OldCandidate?.ContextHeading ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(d => d.NewCandidate?.Title ?? d.OldCandidate?.Title ?? string.Empty, StringComparer.Ordinal)
            .ToList();
    }

    private static IReadOnlyList<string> BuildImpactHints(
        SpecDeltaStatus status,
        ExtractionCandidate? oldCandidate,
        ExtractionCandidate? newCandidate,
        IReadOnlyList<CandidateLinkEntry> links)
    {
        var candidate = oldCandidate ?? newCandidate;
        if (candidate is null)
            return [];

        var hints = new List<string>();
        if (candidate.Classification == ScenarioKind.Requirement)
        {
            var linkedTests = CountLinked(candidate.CandidateId, links, CandidateLinkType.RequirementTest);
            var linkedClarifications = CountLinked(candidate.CandidateId, links, CandidateLinkType.RequirementClarification);

            if (status == SpecDeltaStatus.Modified && linkedTests > 0)
                hints.Add($"{linkedTests} linked test(s) may need review");
            if (status == SpecDeltaStatus.Removed && linkedTests > 0)
                hints.Add("removed requirement still has linked test coverage");
            if (status == SpecDeltaStatus.Added && linkedTests == 0)
                hints.Add("new requirement has no linked tests");
            if (linkedClarifications > 0)
                hints.Add($"{linkedClarifications} linked clarification(s)");
        }
        else if (candidate.Classification == ScenarioKind.Test && status == SpecDeltaStatus.Removed)
        {
            var linkedRequirements = CountLinked(candidate.CandidateId, links, CandidateLinkType.RequirementTest);
            if (linkedRequirements > 0)
                hints.Add("removed test was linked to requirement coverage");
        }
        else if (candidate.Classification == ScenarioKind.NeedsClarification &&
                 status == SpecDeltaStatus.Added)
        {
            hints.Add("new clarification risk");
        }

        return hints;
    }

    private static int CountPotentiallyImpactedTests(
        ExtractionPipelineResult oldSpec,
        IReadOnlyList<SpecDeltaItem> requirementDeltas,
        IReadOnlyList<CandidateLinkEntry> links)
    {
        var changedRequirementIds = requirementDeltas
            .Where(d => d.Status is SpecDeltaStatus.Modified or SpecDeltaStatus.Removed)
            .Select(d => d.OldCandidate?.CandidateId)
            .OfType<Guid>()
            .ToHashSet();

        if (changedRequirementIds.Count == 0)
            return 0;

        var linkedTestIds = links
            .Where(l => l.LinkType == CandidateLinkType.RequirementTest)
            .Select(l => changedRequirementIds.Contains(l.SourceId) ? l.TargetId :
                         changedRequirementIds.Contains(l.TargetId) ? l.SourceId :
                         (Guid?)null)
            .OfType<Guid>()
            .ToHashSet();

        if (linkedTestIds.Count > 0)
            return linkedTestIds.Count;

        var changedContexts = requirementDeltas
            .Where(d => d.Status is SpecDeltaStatus.Modified or SpecDeltaStatus.Removed)
            .Select(d => d.OldCandidate?.ContextHeading)
            .Where(h => !string.IsNullOrWhiteSpace(h))
            .ToHashSet(StringComparer.Ordinal);

        return oldSpec.Candidates.Count(c =>
            c.Classification == ScenarioKind.Test &&
            c.ContextHeading is not null &&
            changedContexts.Contains(c.ContextHeading));
    }

    private static int CountUncoveredRequirements(
        ExtractionPipelineResult newSpec,
        IReadOnlyList<SpecDeltaItem> requirementDeltas,
        IReadOnlyList<CandidateLinkEntry> links)
    {
        var newRequirements = requirementDeltas
            .Where(d => d.Status is SpecDeltaStatus.Added or SpecDeltaStatus.Modified)
            .Select(d => d.NewCandidate)
            .OfType<ExtractionCandidate>()
            .ToList();

        if (newRequirements.Count == 0)
            return 0;

        if (links.Any(l => l.LinkType == CandidateLinkType.RequirementTest))
            return newRequirements.Count(c => CountLinked(c.CandidateId, links, CandidateLinkType.RequirementTest) == 0);

        var testContexts = newSpec.Candidates
            .Where(c => c.Classification == ScenarioKind.Test && !string.IsNullOrWhiteSpace(c.ContextHeading))
            .Select(c => c.ContextHeading!)
            .ToHashSet(StringComparer.Ordinal);

        return newRequirements.Count(c =>
            string.IsNullOrWhiteSpace(c.ContextHeading) ||
            !testContexts.Contains(c.ContextHeading));
    }

    private static int CountNewClarificationRisks(IReadOnlyList<SpecDeltaItem> clarificationDeltas) =>
        clarificationDeltas.Count(d => d.Status == SpecDeltaStatus.Added && IsUnresolved(d.NewCandidate));

    private static int CountStillUnresolvedClarifications(IReadOnlyList<SpecDeltaItem> clarificationDeltas) =>
        clarificationDeltas.Count(d => d.Status == SpecDeltaStatus.Unchanged && IsUnresolved(d.NewCandidate));

    private static bool IsUnresolved(ExtractionCandidate? candidate) =>
        candidate?.ReviewStatus is CandidateReviewStatus.New or CandidateReviewStatus.NeedsReview;

    private static int CountLinked(Guid candidateId, IReadOnlyList<CandidateLinkEntry> links, CandidateLinkType linkType) =>
        links.Count(l => l.LinkType == linkType && (l.SourceId == candidateId || l.TargetId == candidateId));

    private static int SortRank(SpecDeltaStatus status) => status switch
    {
        SpecDeltaStatus.Added => 0,
        SpecDeltaStatus.Modified => 1,
        SpecDeltaStatus.Removed => 2,
        SpecDeltaStatus.Unchanged => 3,
        _ => 4,
    };

    private static string? IdentityKey(ExtractionCandidate candidate)
    {
        var idMatch = RequirementIdRegex().Match(candidate.Title);
        if (!idMatch.Success)
            return null;

        return $"{candidate.Classification}|id:{idMatch.Value.ToUpperInvariant()}";
    }

    private static string ExactKey(ExtractionCandidate candidate) =>
        $"{candidate.Classification}|ctx:{Normalize(candidate.ContextHeading ?? string.Empty)}|text:{Normalize(candidate.Title)}";

    private static string Normalize(string value) =>
        WhitespaceRegex().Replace(value.Trim().ToUpperInvariant(), " ");

    [GeneratedRegex(@"\b(?:FR|REQ|US|AC|TC|TEST|NFR)[-_ ]?\d{1,5}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RequirementIdRegex();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();

    private sealed record CandidateMatch(ExtractionCandidate Candidate, string? IdentityKey, string ExactKey);
}
