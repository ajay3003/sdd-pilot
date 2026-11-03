using BirkNext.DeliveryReporting;
using BirkNext.Interpretation;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// The shared work-item interpretation engine (Delivery and Traceability both consume it). Regression cases follow the real-project shapes
/// (Autorisasjon functional groups, Hendelse scenarios, Tjeneste removed story, Frontend [US6], setup tasks, technical migration) without
/// depending on package names.
/// </summary>
public sealed class WorkItemInterpretationTests
{
    private static InterpretedFeature Stories(params string[] keys) => new([.. keys.Select(k => new InterpretedStory(k, $"Story {k}", false, false))], true);

    [Fact]
    public void A_TaggedTasksWithoutSpecStories_AreAFunctionalGroup_NotAStory()
    {
        var r = WorkItemInterpreter.Classify(new("US1", "Phase 3: US1 — Roles"), new InterpretedFeature([], HasSpec: true));
        Assert.Equal(DeliveryTaskRelationship.FunctionalGroup, r.Relationship);
        Assert.Equal("US1", r.GroupKey);
        Assert.Null(r.LinkedKey);
        Assert.Equal(WorkItemInterpreter.FunctionalRule, r.RuleId);
        Assert.False(r.NeedsReview);
    }

    [Fact]
    public void B_ScenarioSpec_LinksOnlyWhenThePhaseNamesTheScenario()
    {
        var feature = new InterpretedFeature([new("S1", "Hendelsestidslinje vises for saksbehandler", true, false), new("S2", "Varsler", true, false)], true);
        var linked = WorkItemInterpreter.Classify(new("US1", "Phase 3: US1 Hendelsestidslinje (Priority: P1)"), feature);
        Assert.Equal(DeliveryTaskRelationship.ScenarioGroup, linked.Relationship);
        Assert.Equal("S1", linked.LinkedKey);
        var unlinked = WorkItemInterpreter.Classify(new("US2", "Phase 4: US2"), feature);
        Assert.Equal(DeliveryTaskRelationship.ScenarioGroup, unlinked.Relationship);
        Assert.Null(unlinked.LinkedKey); // equal numbers are never evidence
    }

    [Fact]
    public void C_ReferenceToRemovedStory_IsKept_AndNeedsReview()
    {
        var feature = new InterpretedFeature([new("US1", "A", false, false), new("US3", "Old", false, true)], true);
        var r = WorkItemInterpreter.Classify(new("US3", null), feature);
        Assert.Equal(DeliveryTaskRelationship.RemovedStoryReference, r.Relationship);
        Assert.Equal(WorkItemInterpreter.RemovedRule, r.RuleId);
        Assert.True(r.NeedsReview);
    }

    [Fact]
    public void D_ReferenceToUndefinedStory_IsUnresolved_NeverRepointed()
    {
        var r = WorkItemInterpreter.Classify(new("US6", null), Stories("US1", "US2", "US3", "US4", "US5"));
        Assert.Equal(DeliveryTaskRelationship.UnresolvedReference, r.Relationship);
        Assert.Null(r.LinkedKey);
        Assert.Contains("US1, US2, US3, US4, US5", r.Note);
        Assert.True(r.NeedsReview);
    }

    [Fact]
    public void E_UntaggedSetupTask_IsSharedFoundational_NotADefect()
    {
        var r = WorkItemInterpreter.Classify(new(null, "Phase 1: Setup"), Stories("US1"));
        Assert.Equal(DeliveryTaskRelationship.SharedFoundational, r.Relationship);
        Assert.False(r.NeedsReview);
    }

    [Fact]
    public void F_TechnicalMigrationWithoutStories_InventsNoStory()
    {
        var r = WorkItemInterpreter.Classify(new(null, "Phase 2: Rename namespaces"), new InterpretedFeature([], true));
        Assert.Equal(DeliveryTaskRelationship.SharedFoundational, r.Relationship);
        Assert.Null(r.LinkedKey);
    }

    [Fact]
    public void ExplicitTagWins_AndUntaggedStoryPhaseIsLinkedByTitleOnly()
    {
        Assert.Equal(WorkItemInterpreter.ExplicitRule, WorkItemInterpreter.Classify(new("US-02", "Phase 9: Polish"), Stories("US2")).RuleId);
        var phase = WorkItemInterpreter.Classify(new(null, "Phase 4: User Story 2 — Search"), Stories("US2"));
        Assert.Equal(DeliveryTaskRelationship.StoryPhase, phase.Relationship);
        Assert.Equal("US2", phase.LinkedKey);
    }

    [Theory]
    [InlineData("US1-US3")]
    [InlineData("US1, US2")]
    public void MalformedTags_AreUnresolved(string tag) =>
        Assert.Equal(WorkItemInterpreter.MalformedRule, WorkItemInterpreter.Classify(new(tag, null), Stories("US1")).RuleId);

    [Fact]
    public void Deterministic_SameInputsSameResult()
    {
        var feature = Stories("US1", "US2");
        var a = WorkItemInterpreter.Classify(new("US2", "Phase 4"), feature);
        var b = WorkItemInterpreter.Classify(new("US2", "Phase 4"), feature);
        Assert.Equal(a, b);
    }

    [Fact]
    public void SpecHeadings_GiveTheSameKeysDeliveryStores_IncludingRemovedMarkers()
    {
        var feature = SpecStoryHeadings.Feature(
            [("User Story 1 - **Search** (Priority: P1)", null), ("Scenario 2: Varsler", null), ("Child profile", "US4"), ("User Story 3 — Old (Removed)", null)],
            "### User Story 3 — Old (Removed)\n### User Story 5 - User Deactivated When Removed from Entra");
        Assert.Equal(["US1", "S2", "US4", "US3"], feature.Stories.Select(s => s.Key));
        Assert.True(feature.Stories.Single(s => s.Key == "US3").Removed);
        Assert.True(feature.Stories.Single(s => s.Key == "S2").IsScenario);
        Assert.DoesNotContain(feature.Stories, s => s.Key == "US5"); // a title word is never a removed marker, and it is not a heading here
    }

    [Fact]
    public void LabelsAreTheOnesDeliveryShows() =>
        Assert.Equal("Shared & Foundational", BirkNext.Web.Services.DeliveryWorkItems.RelationshipLabel(DeliveryTaskRelationship.SharedFoundational));
}
