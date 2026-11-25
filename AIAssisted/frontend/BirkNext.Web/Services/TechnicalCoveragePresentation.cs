using BirkNext.TestPlanning;

namespace BirkNext.Web.Services;

/// <summary>
/// «Hva er allerede kontrollert?» for one acceptance scenario (2026-11-25): the source tests discovery attached to it (<see cref="DiscoveredScenario.ExistingTests"/>)
/// read together with reviewers' decisions, in plain Norwegian. Four separate facts are never merged:
/// <list type="bullet">
/// <item>whether a test is tied to the scenario (it names the scenario, only a candidate, only the same requirement, confirmed or rejected by a person);</item>
/// <item>how much of the scenario it checks (only a reviewer's assessment of a confirmed link; never inferred);</item>
/// <item>whether that assessment is still current (the test or the scenario changed since);</item>
/// <item>whether it ran: Manual Testing holds no test results, so it is always «Testresultat ikke tilgjengelig» — never «bestått».</item>
/// </list>
/// Pure: no grouping state is read (decisions come in as plain values), nothing is stored.
/// </summary>
public static class TechnicalCoveragePresentation
{
    /// <summary>What the scenario's list shows, strongest first in <see cref="Of"/>'s precedence.</summary>
    public enum State { NotAssessed, NoKnownTest, SameRequirementOnly, Candidate, Linked, Partial, Full, Review }

    /// <summary>How one test is tied to the scenario.</summary>
    public enum Link { Explicit, Candidate, SameRequirement, Confirmed, Rejected, Review }

    public const string NoResult = "Testresultat ikke tilgjengelig";

    /// <summary>A reviewer's decision as plain values (from the specification's decision document).</summary>
    public sealed record Decision(TestLinkReview Review, TechnicalCoverageExtent Extent, string? TestFingerprint, string? ScenarioFingerprint, string By, DateTimeOffset At);

    public sealed record TestRow(CandidateTestAssociation Test, Link Link, TechnicalCoverageExtent Extent, Decision? Decided, string? ReviewReason)
    {
        public string Layer => LayerLabel(Test.Layer);
        public string ReadableName => Readable(Test.MethodName ?? Test.Name);
    }

    public sealed record Coverage(State State, IReadOnlyList<TestRow> Tests, int ReviewedButGone)
    {
        public string Label => StateLabel(State);
        public string Tone => State switch { State.Full or State.Partial or State.Linked => "ok", State.Review => "warn", State.Candidate or State.SameRequirementOnly => "info", _ => "muted" };
    }

    public static Decision ToDecision(ScenarioTestLinkDecision d) => new(d.Review, d.Extent, d.TestFingerprint, d.ScenarioFingerprint, d.By, d.At);

    /// <param name="decisions">Current decisions by <see cref="StoryGroupingEngine.TestLinkKey"/> (cleared ones left out).</param>
    /// <param name="testsRead">Whether the source's tests were read for scenario references (an older import has not been).</param>
    public static Coverage Of(DiscoveredScenario c, IReadOnlyDictionary<string, Decision> decisions, bool testsRead)
    {
        var rows = new List<TestRow>();
        foreach (var t in c.ExistingTests)
        {
            decisions.TryGetValue(StoryGroupingEngine.TestLinkKey(c.CandidateId, t.TestId), out var d);
            string? reason = d is null ? null
                : d.TestFingerprint is { Length: > 0 } tf && t.SourceFingerprint is { Length: > 0 } now && tf != now ? "Testen er endret etter vurderingen"
                : d.ScenarioFingerprint is { Length: > 0 } sf && c.Fingerprint.Length > 0 && sf != c.Fingerprint ? "Scenarioet er endret etter vurderingen"
                : null;
            var link = reason is not null ? Link.Review
                : d?.Review == TestLinkReview.Rejected ? Link.Rejected
                : d?.Review == TestLinkReview.Confirmed ? Link.Confirmed
                : t.Origin == TestScenarioLinkOrigin.SharedRequirement ? Link.SameRequirement
                : t.Confirmed ? Link.Explicit : Link.Candidate;
            rows.Add(new TestRow(t, link, link == Link.Confirmed ? d!.Extent : TechnicalCoverageExtent.NotAssessed, d, reason));
        }
        var present = c.ExistingTests.Select(t => t.TestId).ToHashSet(StringComparer.Ordinal);
        var gone = decisions.Keys.Count(k => k.StartsWith(c.CandidateId + "|", StringComparison.Ordinal) && !present.Contains(k[(c.CandidateId.Length + 1)..]));
        var state = rows.Any(r => r.Link == Link.Review) ? State.Review
            : rows.Any(r => r.Link == Link.Confirmed && r.Extent == TechnicalCoverageExtent.Full) ? State.Full
            : rows.Any(r => r.Link == Link.Confirmed && r.Extent == TechnicalCoverageExtent.Partial) ? State.Partial
            : rows.Any(r => r.Link is Link.Confirmed or Link.Explicit) ? State.Linked
            : rows.Any(r => r.Link == Link.Candidate) ? State.Candidate
            : rows.Any(r => r.Link == Link.SameRequirement) ? State.SameRequirementOnly
            : testsRead ? State.NoKnownTest : State.NotAssessed;
        // Strongest ties first; rejected ones last (kept visible: a rejection is a decision, not a deletion).
        var order = new[] { Link.Review, Link.Confirmed, Link.Explicit, Link.Candidate, Link.SameRequirement, Link.Rejected };
        return new Coverage(state, [.. rows.OrderBy(r => Array.IndexOf(order, r.Link)).ThenBy(r => r.ReadableName, StringComparer.CurrentCultureIgnoreCase)], gone);
    }

    public static string StateLabel(State s) => s switch
    {
        State.Full => "Kontrollert av automatisk test (vurdert)",
        State.Partial => "Delvis kontrollert",
        State.Linked => "Automatisk test funnet",
        State.Candidate => "Mulig automatisk test",
        State.SameRequirementOnly => "Tester for samme krav",
        State.Review => "Kontroller koblingen",
        State.NoKnownTest => "Ingen kjent automatisk test",
        _ => "Automatiske tester ikke vurdert",
    };

    /// <summary>The short tag in the scenario list (null = nothing worth a tag).</summary>
    public static string? ListTag(State s) => s switch
    {
        State.Full => "kontrollert", State.Partial => "delvis kontrollert", State.Linked => "automatisk test", State.Candidate => "mulig automatisk test",
        State.Review => "kontroller koblingen", _ => null,
    };

    public static string LinkLabel(Link l) => l switch
    {
        Link.Explicit => "Testen nevner scenarioet",
        Link.Candidate => "Mulig kobling",
        Link.SameRequirement => "Samme krav, ikke koblet til scenarioet",
        Link.Confirmed => "Koblet (bekreftet)",
        Link.Rejected => "Avvist",
        _ => "Må vurderes på nytt",
    };

    public static string ExtentLabel(TechnicalCoverageExtent e) => e switch
    {
        TechnicalCoverageExtent.Full => "Dekker hele scenarioet",
        TechnicalCoverageExtent.Partial => "Dekker deler av scenarioet",
        _ => "Dekning ikke vurdert",
    };

    public static string LayerLabel(string? layer) => layer switch
    {
        "Unit" => "Enhetstest",
        "Integration" => "Integrasjonstest",
        "Contract" => "Kontrakttest",
        "FrontendComponent" => "Komponenttest",
        _ => "Automatisk test",
    };

    /// <summary>A test method name as words ("Scenario_4_a_withdrawn_report_is_not_delivered" → "Scenario 4 a withdrawn report is not delivered").</summary>
    public static string Readable(string name) => string.Join(' ', name.Replace('_', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries));

    /// <summary>Filter values for «Automatiske tester» (one per state, plus "any tie").</summary>
    public static readonly IReadOnlyList<(string Key, string Label)> Filters =
    [
        ("none", "Ingen kjent automatisk test"), ("candidate", "Mulig automatisk test"), ("linked", "Automatisk test funnet"),
        ("partial", "Delvis kontrollert"), ("full", "Kontrollert (vurdert)"), ("review", "Kontroller koblingen"), ("notassessed", "Ikke vurdert"),
    ];

    public static bool Matches(State s, string filter) => filter switch
    {
        "none" => s is State.NoKnownTest or State.SameRequirementOnly,
        "candidate" => s == State.Candidate,
        "linked" => s == State.Linked,
        "partial" => s == State.Partial,
        "full" => s == State.Full,
        "review" => s == State.Review,
        "notassessed" => s == State.NotAssessed,
        _ => true,
    };
}
