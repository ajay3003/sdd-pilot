using BirkNext.TestCoverage;
using BirkNext.TestPlanning;
using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// «Uavhengig test» checkpoints (2026-11-26), on the real PersonAdapter example: actions are steps (never «mangler»), an expected result keeps its
/// adjectives, negation and timing (no comma fragments), and «nevnt i et testtilfelle» is only a word match against manual cases — no developer test.
/// </summary>
public sealed class PreparationStepsTests
{
    private const string Barn =
        "Submit a Barn (child registration) record whose parent Person does not exist yet. Confirm it is recorded as a distinct, retryable failure with its original data preserved. "
        + "Then submit the parent Person. Confirm the Barn record is automatically delivered on the next retry cycle, with no manual action taken.";

    [Fact]
    public void The_Barn_example_is_two_actions_each_with_one_whole_expected_result_in_source_order()
    {
        var objectives = StoryInterpretation.Objectives(Barn);
        objectives.Select(o => (o.Kind, o.Text)).Should().Equal(
            (StoryObjectiveKind.Condition, "Submit a Barn (child registration) record whose parent Person does not exist yet"),
            (StoryObjectiveKind.Verification, "it is recorded as a distinct, retryable failure with its original data preserved"),
            (StoryObjectiveKind.Condition, "Then submit the parent Person"),
            (StoryObjectiveKind.Verification, "the Barn record is automatically delivered on the next retry cycle, with no manual action taken"));
        objectives.Where(o => o.Kind == StoryObjectiveKind.Condition).Should().OnlyContain(o => !o.NeedsReview, "an explicit action is not an interpretation");

        var steps = PlanPreparePresentation.PreparationSteps([.. objectives.Select(o => new PlanPreparePresentation.ObjectiveCoverage(o, []))]);
        steps.Select(s => (s.Action, s.Expected.Single().Objective.Text)).Should().Equal(
            ("Submit a Barn (child registration) record whose parent Person does not exist yet", "it is recorded as a distinct, retryable failure with its original data preserved"),
            ("Then submit the parent Person", "the Barn record is automatically delivered on the next retry cycle, with no manual action taken"));
    }

    [Fact]
    public void A_real_list_is_still_split_at_new_items_and_an_and_inside_an_item_never_is()
    {
        var objectives = StoryInterpretation.Objectives("Submit a report. Verify the stored outcome, the rejection reasons, and that no duplicate exists.");
        objectives.Where(o => o.Kind == StoryObjectiveKind.Verification).Select(o => o.Text).Should().Equal("the stored outcome", "the rejection reasons", "no duplicate exists");
    }

    [Fact]
    public void Only_an_expected_result_can_be_mentioned_and_coverage_is_never_claimed_for_an_action()
    {
        var story = new CoverageUserStory { Key = "k", StoryId = "US1", IndependentTest = Barn };
        var coverage = PlanPreparePresentation.ObjectiveCoverageOf(story, [("Case A", "Submit a Barn record whose parent person does not exist; it is recorded as a distinct retryable failure with original data preserved")]);
        var steps = PlanPreparePresentation.PreparationSteps(coverage);
        steps[0].Expected.Single().Covered.Should().BeTrue("the case text mentions the expected result (a hint)");
        steps[1].Expected.Single().Covered.Should().BeFalse();
        steps.Should().OnlyContain(s => s.Action != null, "actions are steps, not coverage obligations");
    }
}
