using BirkNext.TestPlanning;
using TC = BirkNext.Web.Services.TechnicalCoveragePresentation;

namespace BirkNext.Web.Services;

/// <summary>
/// Developer tests and acceptance-criteria coverage (2026-11-26), read from the same facts as «Hva er allerede kontrollert?»
/// (<see cref="TechnicalCoveragePresentation"/>): which source tests are tied to each acceptance scenario and what a reviewer assessed.
/// <para>The one rule this class exists for: <b>«utviklertesten finnes» is never «akseptansescenarioet er tilstrekkelig testet»</b>.</para>
/// <list type="bullet">
/// <item>A user story summary counts the scenarios per state and never says «dekket» or uses a success tone for the story as a whole.</item>
/// <item>A checkpoint (an expected result of the scenario) is «kontrollert av utviklertest» only when a reviewer assessed a confirmed test as covering
///   the WHOLE scenario. Partial, explicit or candidate ties leave every checkpoint «ikke bekreftet», because no one has said which ones the test checks.</item>
/// <item>A developer test never replaces or removes a manual case; planning only gets a priority hint.</item>
/// </list>
/// Pure: nothing is stored or inferred from test names.
/// </summary>
public static class DeveloperTestCoveragePresentation
{
    public const string Caveat = "En utviklertest viser at noe er kontrollert i koden — ikke at akseptansescenarioet er tilstrekkelig testet.";

    /// <summary>Counts for one user story's acceptance scenarios. <see cref="WithTest"/> = a test explicitly tied, confirmed or assessed.</summary>
    public sealed record StorySummary(int Total, int Full, int Partial, int Linked, int Candidate, int NoTest, int Review, int NotAssessed)
    {
        public int WithTest => Full + Partial + Linked;
        public bool AnyRead => Total > NotAssessed;
    }

    public static StorySummary Summarize(IEnumerable<TC.State> states)
    {
        var list = states.ToList();
        int N(params TC.State[] s) => list.Count(x => s.Contains(x));
        return new(list.Count, N(TC.State.Full), N(TC.State.Partial), N(TC.State.Linked), N(TC.State.Candidate),
            N(TC.State.NoKnownTest, TC.State.SameRequirementOnly), N(TC.State.Review), N(TC.State.NotAssessed));
    }

    /// <summary>The US card line, e.g. «Utviklertester: 3 av 9 AC har koblet test (1 vurdert som full, 1 delvis) · 5 uten kjent test».</summary>
    public static string StoryText(StorySummary s)
    {
        if (s.Total == 0) return "Utviklertester: ingen akseptansescenarioer";
        if (!s.AnyRead) return "Utviklertester: ikke vurdert (testene i kilden er ikke lest)";
        var parts = new List<string> { $"{s.WithTest} av {s.Total} AC har koblet test" };
        var detail = new List<string>();
        if (s.Full > 0) detail.Add($"{s.Full} vurdert som full");
        if (s.Partial > 0) detail.Add($"{s.Partial} delvis");
        if (detail.Count > 0) parts[0] += $" ({string.Join(", ", detail)})";
        if (s.Candidate > 0) parts.Add($"{s.Candidate} med mulig test");
        if (s.Review > 0) parts.Add($"{s.Review} må vurderes på nytt");
        if (s.NoTest > 0) parts.Add($"{s.NoTest} uten kjent test");
        return "Utviklertester: " + string.Join(" · ", parts);
    }

    /// <summary>Never «ok» for a story: at best informational. Warn when ties must be reviewed again.</summary>
    public static string StoryTone(StorySummary s) => s.Review > 0 ? "warn" : s.AnyRead ? "info" : "muted";

    public enum CheckpointState { CheckedByAssessedTest, NotConfirmed }

    public sealed record Checkpoint(string Text, CheckpointState State)
    {
        public string Label => State == CheckpointState.CheckedByAssessedTest ? "Kontrollert av utviklertest (vurdert)" : "Ikke bekreftet kontrollert av utviklertest";
    }

    /// <summary>The scenario's checkpoints (its expected results, in order, without repeats) and whether a developer test is assessed to check them.</summary>
    public static IReadOnlyList<Checkpoint> Checkpoints(DiscoveredScenario c, TC.State state)
    {
        var texts = c.Steps.Select(s => s.Expected).Append(c.ExpectedBehaviour)
            .Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t!.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var checkedState = state == TC.State.Full ? CheckpointState.CheckedByAssessedTest : CheckpointState.NotConfirmed;
        return [.. texts.Select(t => new Checkpoint(t, checkedState))];
    }

    /// <summary>What the coverage means for this scenario in plain words (shown under the state in the details and in Planlegg).</summary>
    public static string Meaning(TC.State state) => state switch
    {
        TC.State.Full => "En gjennomgått utviklertest er vurdert til å dekke hele scenarioet. Vurder om manuell test fortsatt trengs — testtilfellet fjernes aldri automatisk.",
        TC.State.Partial => "En utviklertest dekker deler av scenarioet. Resultatene som ikke er bekreftet kontrollert, må dekkes av manuell test.",
        TC.State.Linked => "En utviklertest er knyttet til scenarioet, men ingen har vurdert hvor mye den kontrollerer.",
        TC.State.Candidate => "Det finnes en mulig utviklertest. Koblingen er ikke bekreftet.",
        TC.State.Review => "Testen eller scenarioet er endret etter vurderingen. Vurder koblingen på nytt.",
        TC.State.NoKnownTest or TC.State.SameRequirementOnly => "Ingen kjent utviklertest kontrollerer dette scenarioet. Prioriter manuell test.",
        _ => "Utviklertestene er ikke vurdert for dette scenarioet.",
    };

    /// <summary>Priority hint for planning: scenarios without a tied developer test first. Never a reason to drop a case.</summary>
    public static bool PrioritiseManual(TC.State state) => state is TC.State.NoKnownTest or TC.State.SameRequirementOnly or TC.State.Candidate;
}
