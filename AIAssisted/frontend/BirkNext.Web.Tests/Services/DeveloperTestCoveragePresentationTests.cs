using BirkNext.TestPlanning;
using FluentAssertions;
using DT = BirkNext.Web.Services.DeveloperTestCoveragePresentation;
using TC = BirkNext.Web.Services.TechnicalCoveragePresentation;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// Developer tests and AC coverage (2026-11-26): «utviklertesten finnes» is never «akseptansescenarioet er tilstrekkelig testet». A story summary
/// counts per state with no success tone; checkpoints count as checked only behind a reviewer's full-coverage assessment; planning gets a priority
/// hint, never a removed case.
/// </summary>
public sealed class DeveloperTestCoveragePresentationTests
{
    [Fact]
    public void A_story_summary_counts_per_state_and_never_claims_the_story_is_covered()
    {
        var s = DT.Summarize([TC.State.Full, TC.State.Partial, TC.State.Linked, TC.State.Candidate, TC.State.NoKnownTest, TC.State.SameRequirementOnly, TC.State.Review]);
        (s.Total, s.WithTest, s.NoTest, s.Candidate, s.Review).Should().Be((7, 3, 2, 1, 1));
        DT.StoryText(s).Should().Be("Utviklertester: 3 av 7 AC har koblet test (1 vurdert som full, 1 delvis) · 1 med mulig test · 1 må vurderes på nytt · 2 uten kjent test");
        DT.StoryTone(s).Should().Be("warn");

        var all = DT.Summarize([TC.State.Full, TC.State.Full]);
        DT.StoryText(all).Should().NotContainAny("dekket", "Dekket", "testet");
        DT.StoryTone(all).Should().Be("info", "even every AC assessed as full is not shown as a success for the story");
        DT.StoryText(DT.Summarize([TC.State.NotAssessed])).Should().Contain("ikke vurdert");
        DT.StoryText(DT.Summarize([])).Should().Contain("ingen akseptansescenarioer");
    }

    [Fact]
    public void Checkpoints_are_checked_only_behind_a_full_assessment_and_partial_leaves_all_unconfirmed()
    {
        var c = new DiscoveredScenario
        {
            CandidateId = "s1", Steps = [new("Send rapport", "Rapporten lagres"), new("Send igjen", "Duplikat avvises"), new("Åpne", null)], ExpectedBehaviour = "rapporten lagres",
        };
        DT.Checkpoints(c, TC.State.Full).Select(x => (x.Text, x.State)).Should().Equal(("Rapporten lagres", DT.CheckpointState.CheckedByAssessedTest), ("Duplikat avvises", DT.CheckpointState.CheckedByAssessedTest));
        DT.Checkpoints(c, TC.State.Partial).Should().OnlyContain(x => x.State == DT.CheckpointState.NotConfirmed, "partial does not say which");
        DT.Checkpoints(c, TC.State.Linked).Should().OnlyContain(x => x.Label == "Ikke bekreftet kontrollert av utviklertest", "a test that exists is not an assessment");
    }

    [Fact]
    public void Planning_priority_and_meaning_never_drop_a_manual_case()
    {
        DT.PrioritiseManual(TC.State.NoKnownTest).Should().BeTrue();
        DT.PrioritiseManual(TC.State.Candidate).Should().BeTrue("an unconfirmed tie is not a test");
        DT.PrioritiseManual(TC.State.Full).Should().BeFalse();
        DT.Meaning(TC.State.Full).Should().Contain("fjernes aldri automatisk");
        DT.Meaning(TC.State.Partial).Should().Contain("manuell test");
        TC.Matches(TC.State.SameRequirementOnly, "nodevtest").Should().BeTrue();
        TC.Matches(TC.State.Linked, "nodevtest").Should().BeFalse();
    }
}
