using BirkNext.TestCoverage;

namespace BirkNext.Web.Services;

/// <summary>One "What needs attention" line, counted from the review result.</summary>
public sealed record CoverageAttentionItem(string Key, int Count, string Text, bool Neutral);

/// <summary>A journey step whose connection a tester has to decide (suggested or needs confirmation, no decision yet).</summary>
public sealed record ConnectionDecision(Journey Journey, JourneyStep Step);

/// <summary>One group of Remaining QA Scope: a count, a one-line explanation and its items (gaps and/or recommendation lines).</summary>
public sealed record QaScopeGroup(string Key, string Title, string Explanation, IReadOnlyList<CoverageGap> Gaps, IReadOnlyList<string> Lines, int Count);

public sealed record JourneySummary(int Steps, int Covered, int MockOnly, int LowerLevelOnly, int NotVerified, int Partly, int RuntimeVerified, int NeedConfirmation);

/// <summary>
/// Presentation-only structure for Test Coverage &amp; Overlap Review: attention counts, the QA recommendation, journey summaries and the
/// grouped Remaining QA Scope, all derived from the stored result. No classification, journey, overlap or recommendation rule is recomputed;
/// counts always equal the underlying lists and "found" is never shown as executed, passed or a coverage percentage.
/// </summary>
public static class TestCoverageGuidance
{
    public static bool IsUnknownLevel(CoverageTest t) => t.Level is TestLevel.Unknown or TestLevel.NeedsReview;

    public static (int Unit, int ApiIntegrationDatabase, int UiE2E, int Unknown) Levels(TestCoverageReviewResult r) =>
        (r.Tests.Count(t => t.Level == TestLevel.Unit), r.Tests.Count(t => t.Level is TestLevel.Api or TestLevel.Integration or TestLevel.Repository),
         r.Tests.Count(t => t.Level is TestLevel.E2E or TestLevel.Ui), r.Tests.Count(IsUnknownLevel));

    public static (int Substituted, int Real) Boundaries(TestCoverageReviewResult r) =>
        (r.Tests.Count(t => t.Boundaries.Any(b => b.Mode is BoundaryMode.Mocked or BoundaryMode.Fake)),
         r.Tests.Count(t => t.Boundaries.Any(b => b.Mode is BoundaryMode.Real or BoundaryMode.RealContainer)));

    public static IReadOnlyList<ConnectionDecision> PendingConnections(TestCoverageReviewResult r) =>
        [.. r.Journeys.SelectMany(j => j.Steps.Where(s => s.Evidence is ConnectionEvidence.Suggested or ConnectionEvidence.NeedsConfirmation && s.ReviewerDecision is null)
            .Select(s => new ConnectionDecision(j, s)))
            .DistinctBy(d => (d.Step.From, d.Step.To))];

    public static IReadOnlyList<CoverageAttentionItem> Attention(TestCoverageReviewResult r)
    {
        var items = new List<CoverageAttentionItem>();
        void Add(string key, int count, string singular, string plural, bool neutral = false) { if (count > 0) items.Add(new(key, count, $"{count} {(count == 1 ? singular : plural)}", neutral)); }
        Add("gaps", r.Gaps.Count(g => g.Attention == "Needs attention"), "remaining gap needs tester attention", "remaining gaps need tester attention");
        Add("connections", PendingConnections(r).Count, "journey connection needs confirmation", "journey connections need confirmation");
        Add("mock-only", r.Gaps.Count(g => g.Kind == GapKind.MockOnly), "area is covered only by mocks or in-memory substitutes", "areas are covered only by mocks or in-memory substitutes");
        Add("unknown-level", Levels(r).Unknown, "test has an unknown test level (a review limitation, not a quality failure)", "tests have an unknown test level (a review limitation, not a quality failure)", neutral: true);
        Add("decisions", TesterDecisions(r).Count, "item needs a tester decision", "items need a tester decision");
        items.Add(new("execution", r.Inventory.TestsWithExecutionEvidence, r.Inventory.TestsWithExecutionEvidence == 0
            ? "0 execution results are available — execution is not verified"
            : $"{r.Inventory.TestsWithExecutionEvidence} tests have imported execution results", true));
        return items;
    }

    private const string ConfirmPrefix = "Confirm the connection";

    /// <summary>Needs-review lines that are not journey connections (those are listed as structured decisions) and not the unknown-level count.</summary>
    public static IReadOnlyList<string> TesterDecisions(TestCoverageReviewResult r) =>
        [.. r.QaScope.NeedsReview.Where(l => !l.StartsWith(ConfirmPrefix, StringComparison.Ordinal) && !l.Contains("level could not be determined", StringComparison.Ordinal))];

    public static JourneySummary Summarize(Journey j) => new(j.Steps.Count,
        j.Steps.Count(s => s.Developer == StepCoverage.Covered), j.Steps.Count(s => s.Developer == StepCoverage.MockOnly),
        j.Steps.Count(s => s.Developer == StepCoverage.LowerLevelOnly), j.Steps.Count(s => s.Developer == StepCoverage.NotVerified),
        j.Steps.Count(s => s.Developer == StepCoverage.PartlyCovered),
        j.Steps.Count(s => !string.Equals(s.Runtime, "Not verified", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(s.Runtime)),
        j.Steps.Count(s => s.Evidence is ConnectionEvidence.Suggested or ConnectionEvidence.NeedsConfirmation && s.ReviewerDecision is null));

    /// <summary>The weakest developer coverage on the journey's path (the step that limits it).</summary>
    public static StepCoverage Weakest(Journey j) => j.Steps.Count == 0 ? StepCoverage.NotVerified
        : j.Steps.Select(s => s.Developer).OrderByDescending(Rank).First();

    private static int Rank(StepCoverage c) => c switch
    {
        StepCoverage.NotVerified => 4, StepCoverage.MockOnly => 3, StepCoverage.LowerLevelOnly => 2, StepCoverage.PartlyCovered => 1, _ => 0,
    };

    /// <summary>Remaining QA Scope as groups. Every gap is in exactly one gap-bearing group; recommendation lines keep the engine's wording.</summary>
    public static IReadOnlyList<QaScopeGroup> ScopeGroups(TestCoverageReviewResult r)
    {
        var realFlow = r.Gaps.Where(g => g.Kind == GapKind.JourneyStepNotVerified).ToList();
        var mockOnly = r.Gaps.Where(g => g.Kind == GapKind.MockOnly).ToList();
        var other = r.Gaps.Except(realFlow).Except(mockOnly).ToList();
        var unknown = Levels(r).Unknown;
        var connections = PendingConnections(r);
        var decisions = TesterDecisions(r);
        return
        [
            new("focus", "What testers should focus on", "Real-flow checks the developer tests do not reach: one system test per affected journey or area.", [], r.QaScope.Keep, r.QaScope.Keep.Count),
            new("real-flow", "Critical real-flow gaps", "Journey steps not verified through a real path (not tested, or tested with substitutes only).", realFlow, [], realFlow.Count),
            new("mock-only", "Covered only by mocks or in-memory substitutes", "Developer tests replace the dependency; the real integration path is not verified.", mockOnly, [], mockOnly.Count),
            new("connections", "Connections needing confirmation", "Suggested from source and names — not confirmed architecture. Confirm or reject each.", [], [], connections.Count),
            new("decisions", "Tester decisions", "Items that need human judgement (possible overlaps, insufficient evidence).", [], decisions, decisions.Count),
            new("unknown-level", "Unknown test classification", "Tests whose level could not be determined from setup or source evidence — a review limitation, not a quality failure.", [], [], unknown),
            new("not-repeat", "Probably do not need to repeat", "Developer automation already covers this isolated logic; review whether QA duplication is necessary and keep one system/journey test for the real flow.", [], r.QaScope.ProbablyUnnecessary, r.QaScope.ProbablyUnnecessary.Count),
            new("other", "Other remaining gaps", "Remaining gaps of other kinds (no tests, unit only, changed without test change, …).", other, [], other.Count),
        ];
    }

    /// <summary>Why 0 possible duplicates means little without QA evidence.</summary>
    public static string? DuplicateContext(TestCoverageReviewResult r) =>
        r.Overlaps.Count == 0 && r.QaTests.Count == 0
            ? "No QA-owned automated tests or acceptance scenarios were available for direct comparison, so 0 is not evidence that no duplication exists."
            : null;
}
