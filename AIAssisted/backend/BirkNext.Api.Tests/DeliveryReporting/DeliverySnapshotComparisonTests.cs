using BirkNext.Api.Services.DeliveryReporting;
using BirkNext.DeliveryReporting;
using FluentAssertions;
using Xunit;
using static BirkNext.Api.Tests.DeliveryReporting.DeliveryReportingFixtures;

namespace BirkNext.Api.Tests.DeliveryReporting;

public sealed class DeliverySnapshotComparisonTests
{
    private static DeliveryComparisonResult Compare((string, string)[] baseline, (string, string)[] current) =>
        DeliverySnapshotComparison.Compare(Content(Parse(baseline), Monday), Content(Parse(current), Friday));

    [Fact]
    public void ThreeOfEight_To_SevenOfEight_IsProgressed_WithPlusFour()
    {
        var story = Compare(ChildSearch(8, 3), ChildSearch(8, 7)).Stories.Single();

        story.Movement.Should().Be(DeliveryMovement.Progressed);
        story.TaskDelta.Should().Be(4);
        story.TasksCompletedThisPeriod.Should().Be(4);
        story.BaselineStatus.Should().Be(DeliveryStatus.InProgress);
        story.CurrentStatus.Should().Be(DeliveryStatus.InProgress);
        story.Reasons.Should().Contain("Progressed: task completion changed from 3/8 to 7/8");
        story.Evidence.TasksCompleted.Should().HaveCount(4);
    }

    [Fact]
    public void AllTasksChecked_IsCompleted_ButStatusStaysTasksComplete()
    {
        var story = Compare(ChildSearch(8, 7), ChildSearch(8, 8)).Stories.Single();

        story.Movement.Should().Be(DeliveryMovement.Completed);
        story.CurrentStatus.Should().Be(DeliveryStatus.TasksComplete, "checked tasks are not Done");
    }

    [Fact]
    public void FeatureOnlyInCurrent_IsNewThisPeriod()
    {
        var current = ChildSearch(8, 3).Concat([("Frontend/specs/002-case-overview/spec.md", Spec("Case overview", DefaultAcceptance)),
            ("Frontend/specs/002-case-overview/tasks.md", Tasks(5, 0))]).ToArray();

        var story = Compare(ChildSearch(8, 3), current).Stories.Single(s => s.Title == "Case overview");

        story.Movement.Should().Be(DeliveryMovement.NewThisPeriod);
        story.NewFeature.Should().BeTrue();
        story.BaselineStatus.Should().BeNull();
    }

    [Fact]
    public void FeatureOnlyInBaseline_IsRemovedNeedsReview_NeverCompleted()
    {
        var baseline = ChildSearch(8, 3).Concat([("Frontend/specs/002-case-overview/spec.md", Spec("Case overview", DefaultAcceptance)),
            ("Frontend/specs/002-case-overview/tasks.md", Tasks(5, 5))]).ToArray();

        var result = Compare(baseline, ChildSearch(8, 3));
        var story = result.Stories.Single(s => s.Title == "Case overview");

        story.Movement.Should().Be(DeliveryMovement.Removed);
        story.Movement.Should().NotBe(DeliveryMovement.Completed);
        DeliveryReportingText.Movement(story.Movement).Should().Contain("needs review");
        result.Attention.Should().Contain(a => a.Kind == DeliveryAttentionKind.FeatureRemoved && a.Severity == DeliveryAttentionSeverity.Review);
    }

    [Fact]
    public void AcceptanceCriteriaChange_IsSpecificationChanged_WithTheChangeInEvidence()
    {
        string[] changed = [DefaultAcceptance[0], "**Given** a protected child, **When** they search, **Then** the child is hidden"];

        var result = Compare(ChildSearch(8, 3), ChildSearch(8, 3, acceptance: changed));
        var story = result.Stories.Single();

        story.SpecificationChanged.Should().BeTrue();
        story.AcceptanceCriteriaChanged.Should().BeTrue();
        story.Movement.Should().Be(DeliveryMovement.NeedsReview);
        story.Evidence.AcceptanceAdded.Should().ContainSingle(a => a.Contains("protected child"));
        story.Evidence.AcceptanceRemoved.Should().ContainSingle(a => a.Contains("no match"));
        result.Attention.Should().Contain(a => a.Kind == DeliveryAttentionKind.AcceptanceCriteriaChanged, "work had started when the criteria changed");
    }

    [Fact]
    public void EquivalentSnapshots_HaveNoSignificantChanges()
    {
        var result = Compare(ChildSearch(8, 3), ChildSearch(8, 3));

        result.Stories.Should().OnlyContain(s => s.Movement == DeliveryMovement.NoSignificantChange);
        result.FeaturesChanged.Should().Be(0);
        result.Events.Should().BeEmpty();
        result.Attention.Should().BeEmpty();
    }

    [Fact]
    public void UncheckedTasks_AreRegressed_AndNeedAttention()
    {
        var result = Compare(ChildSearch(8, 6), ChildSearch(8, 4));

        result.Stories.Single().Movement.Should().Be(DeliveryMovement.Regressed);
        result.Attention.Should().Contain(a => a.Kind == DeliveryAttentionKind.TaskCompletionWentBackwards);
    }

    [Fact]
    public void DisappearedTasks_NeedAttention()
    {
        var result = Compare(ChildSearch(8, 3), ChildSearch(5, 3));

        result.Attention.Should().Contain(a => a.Kind == DeliveryAttentionKind.TasksDisappeared);
        result.Stories.Single().Movement.Should().Be(DeliveryMovement.NeedsReview);
    }

    [Fact]
    public void ExplicitBlocked_IsRegressedAndABlocker_OnlyFromExplicitEvidence()
    {
        var result = Compare(ChildSearch(8, 3), ChildSearch(8, 3, statusLine: "Blocked"));

        result.Stories.Single().Movement.Should().Be(DeliveryMovement.Regressed);
        result.Attention.Should().ContainSingle(a => a.Severity == DeliveryAttentionSeverity.Blocker);
    }

    [Fact]
    public void SourceChanges_AreSupportingEvidenceOnly()
    {
        var result = Compare([.. ChildSearch(8, 3), ("Frontend/src/Search.cs", "class Search {}")],
            [.. ChildSearch(8, 3), ("Frontend/src/Search.cs", "class Search { void Run() {} }")]);
        var story = result.Stories.Single();

        story.Movement.Should().Be(DeliveryMovement.NoSignificantChange, "source movement never decides delivery status");
        story.Evidence.SourceSummary.Should().Contain("1 source file(s) changed in Frontend").And.Contain("supporting evidence only");
    }

    [Fact]
    public void RenamedFeatureFolder_IsFlaggedAsAmbiguousIdentity()
    {
        var current = new[] { ("Frontend/specs/001-child-lookup/spec.md", Spec("Child lookup", DefaultAcceptance)), ("Frontend/specs/001-child-lookup/tasks.md", Tasks(8, 3)) };

        var result = Compare(ChildSearch(8, 3), current);

        result.Attention.Should().Contain(a => a.Kind == DeliveryAttentionKind.AmbiguousFeatureIdentity);
    }

    [Fact]
    public void Comparison_IsDeterministic()
    {
        var baseline = Content(Parse(ChildSearch(8, 3)), Monday);
        var current = Content(Parse(ChildSearch(8, 7)), Friday);

        var a = DeliverySnapshotComparison.Compare(baseline, current);
        var b = DeliverySnapshotComparison.Compare(baseline, current);

        b.Should().BeEquivalentTo(a, o => o.WithStrictOrdering());
    }
}
