using BirkNext.ManualReview;
using BirkNext.TestCoverage;

namespace BirkNext.Web.Services;

/// <summary>How a source-derived journey relates to an Integration Journey: the same id, or a reviewer's recorded decision.</summary>
public enum JourneyAssociationKind { Exact, Confirmed, Rejected }

/// <summary>One association of a source journey with an Integration Journey ("packId:journeyId"). Only Exact and Confirmed ever count.</summary>
public sealed record JourneyAssociationLink(string IntegrationJourneyId, JourneyAssociationKind Kind, string? Reviewer, DateTimeOffset? DecidedAt);

/// <summary>One Test Data asset linked to QA tests in this review (deduplicated by asset id), with the tests that use it.</summary>
/// <param name="Unresolved">Every link to this asset is unresolved (deprecated, invalid, no longer approved or changed since bound).</param>
public sealed record CoverageAssetUse(string AssetId, string? Alias, IReadOnlyList<string> Fingerprints, IReadOnlyList<string> Tests, IReadOnlyList<string> Sources,
    bool Unresolved = false);

/// <summary>
/// Test Coverage &amp; Overlap Review helpers for journey associations and Test Data links. (The test-area filter that lived here was removed with
/// Project Test Setup on 2026-11-19: coverage is read for the selected project as a whole; nothing is tied to a test area any more.)
/// Journeys: the review's journeys are derived from source connections ("journey:a&gt;b"); a reviewer's confirmed association with an Integration
/// Journey ("packId:journeyId") is recorded in the shared manual-review store (module <see cref="ManualReviewModules.JourneyAssociation"/>); a
/// structural candidate is shown for review and never counts. Names are never matched.
/// </summary>
public static class TestCoverageScopePresentation
{
    public const string CandidateLabel = "Candidate — needs review";

    /// <summary>The latest recorded association decision per (source journey, Integration Journey) pair. The store marks the latest per subject
    /// only, so the pair is resolved here by decision time. Decisions of other modules or without an "applies to" are ignored.</summary>
    public static IReadOnlyDictionary<(string Subject, string AppliesTo), ManualReviewDecisionView> LatestAssociations(IEnumerable<ManualReviewDecisionView>? decisions) =>
        (decisions ?? []).Where(d => d.Module == ManualReviewModules.JourneyAssociation && !string.IsNullOrWhiteSpace(d.AppliesTo) && !string.IsNullOrWhiteSpace(d.SubjectKey))
            .GroupBy(d => (d.SubjectKey, d.AppliesTo!)).ToDictionary(g => g.Key, g => g.OrderByDescending(d => d.DecidedAt).First());

    /// <summary>The recorded associations of one source journey (Acceptable = confirmed, IssueFound = rejected; any other latest value is undecided).</summary>
    public static IReadOnlyList<JourneyAssociationLink> Associations(Journey journey, IReadOnlyDictionary<(string Subject, string AppliesTo), ManualReviewDecisionView> latest) =>
        [.. latest.Where(kv => kv.Key.Subject == journey.Id && kv.Value.Decision is ManualReviewDecisionValue.Acceptable or ManualReviewDecisionValue.IssueFound)
            .Select(kv => new JourneyAssociationLink(kv.Key.AppliesTo, kv.Value.Decision == ManualReviewDecisionValue.Acceptable ? JourneyAssociationKind.Confirmed : JourneyAssociationKind.Rejected,
                kv.Value.Reviewer, kv.Value.DecidedAt))
            .OrderBy(l => l.IntegrationJourneyId, StringComparer.Ordinal)];

    /// <summary>The recorded decision on one candidate, or null when it was not reviewed.</summary>
    public static JourneyAssociationKind? CandidateDecision(Journey journey, string integrationJourneyId, IReadOnlyDictionary<(string Subject, string AppliesTo), ManualReviewDecisionView> latest) =>
        latest.TryGetValue((journey.Id, integrationJourneyId), out var d)
            ? d.Decision switch { ManualReviewDecisionValue.Acceptable => JourneyAssociationKind.Confirmed, ManualReviewDecisionValue.IssueFound => JourneyAssociationKind.Rejected, _ => (JourneyAssociationKind?)null }
            : null;

    /// <summary>Plain label of a QA test's data need. "Not assessed" is never "no data needed".</summary>
    public static string DataNeed(QaTestCandidate q) => q.DataNeed switch
    {
        CoverageDataNeed.Linked => $"Linked ({string.Join(", ", q.TestData.Select(l => l.AssetId).Distinct(StringComparer.Ordinal))})",
        CoverageDataNeed.RequiredButMissing => "Required but missing",
        CoverageDataNeed.LinkNeedsReview => $"Linked revision not usable — needs review ({string.Join(", ", q.TestData.Where(l => !l.Resolved).Select(l => l.AssetId).Distinct(StringComparer.Ordinal))})",
        _ => "Not assessed",
    };

    /// <summary>Plain state of one link: the bound revision is current and approved, or it needs review (with Test Data's reason).</summary>
    public static string LinkState(CoverageTestDataLink link) => link.Resolved ? "Resolved" : $"Unresolved — {link.StateReason ?? "needs review"}";

    /// <summary>
    /// Opens Test Data with this QA test as the consumer (Test Data owns requirements and bindings): it shows matching approved assets, records
    /// the requirement ("Add to requested Test Data") and binds an asset revision ("Use for this requirement"). The consumer id is project-scoped.
    /// </summary>
    public static string TestDataHref(QaTestCandidate q, string? projectKey, BirkNext.TestData.TestDataDataType dataType, string? environmentId = null) =>
        "test-data?consumer=" + Uri.EscapeDataString(CoverageTestDataConsumers.ConsumerId(projectKey, q.Id))
        + "&consumerType=" + Uri.EscapeDataString(CoverageTestDataConsumers.ConsumerType)
        + "&consumerName=" + Uri.EscapeDataString(string.IsNullOrWhiteSpace(q.Title) ? q.Id : q.Title)
        + "&dataType=" + dataType
        + (string.IsNullOrWhiteSpace(environmentId) ? "" : "&environment=" + Uri.EscapeDataString(environmentId));

    /// <summary>Every Test Data asset linked to a QA test in this review, once per asset id, with the tests that use it.</summary>
    public static IReadOnlyList<CoverageAssetUse> Assets(TestCoverageReviewResult r) =>
        [.. r.QaTests.SelectMany(q => q.TestData.Select(l => (Test: q, Link: l))).GroupBy(x => x.Link.AssetId, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new CoverageAssetUse(g.Key, g.Select(x => x.Link.Alias).FirstOrDefault(a => !string.IsNullOrWhiteSpace(a)),
                [.. g.Select(x => x.Link.Fingerprint).Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => f!).Distinct(StringComparer.Ordinal)],
                [.. g.Select(x => x.Test.Title).Distinct(StringComparer.Ordinal)], [.. g.Select(x => x.Link.Source).Distinct(StringComparer.Ordinal)],
                g.All(x => !x.Link.Resolved)))];
}
