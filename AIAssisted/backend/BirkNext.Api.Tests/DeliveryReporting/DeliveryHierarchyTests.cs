using BirkNext.Api.Services.DeliveryReporting;
using BirkNext.DeliveryReporting;
using FluentAssertions;
using Xunit;
using static BirkNext.Api.Tests.DeliveryReporting.DeliveryReportingFixtures;

namespace BirkNext.Api.Tests.DeliveryReporting;

/// <summary>
/// Feature → optional Phase → User Story → Acceptance Criteria, with tasks attached only through explicit evidence (Phase N groups, [USn] tags).
/// Nothing is invented: no fake stories, phases or task links; baseline/current deltas at each level; completed this period only for matched
/// open → checked tasks. Local fixtures only.
/// </summary>
public sealed class DeliveryHierarchyTests
{
    private const string Folder = "Frontend/specs/002-registration";

    private static string Story(int n, string title, string priority, params string[] criteria) => $"""
        ### User Story {n} - {title} (Priority: {priority})

        A caseworker wants to {title.ToLowerInvariant()} so that the child is followed up.

        **Why this priority**: core flow.

        **Acceptance Scenarios**:

        {string.Join("\n", criteria.Select((c, i) => $"{i + 1}. {c}"))}

        """;

    private static string Spec(string title, string stories, string successCriteria = "") => $"""
        # Feature Specification: {title}

        ## User Scenarios & Testing *(mandatory)*

        {stories}
        ## Requirements *(mandatory)*

        - **FR-001**: System MUST support {title}.
        {(successCriteria.Length == 0 ? "" : $"\n## Success Criteria\n\n- **SC-001**: {successCriteria}\n")}
        """;

    private static readonly string ThreeStories = Story(1, "Register child", "P1", "**Given** a caseworker, **When** they register a child, **Then** the child is saved", "**Given** a missing name, **When** they save, **Then** an error is shown")
        + Story(2, "Handle unknown placement category", "P2", "**Given** an unknown category, **When** imported, **Then** it is flagged")
        + Story(3, "Audit registrations", "P3", "**Given** a registration, **When** saved, **Then** an audit entry exists");

    private static string PhasedTasks(bool t3Done = false, bool extra = false, bool reopen = false) => $"""
        # Tasks

        ## Phase 1: Foundation

        - [x] T001 [US1] Create registration form
        - [{(reopen ? " " : "x")}] T002 [US2] Map placement categories
        - [x] T003 Configure logging for the registration module

        ## Phase 2: Hardening

        - [{(t3Done ? "x" : " ")}] T010 [US3] Write audit entries
        - [ ] T011 Register child validation messages review
        {(extra ? "- [x] T012 [US3] Export audit report\n" : "")}
        ## Polish

        - [ ] T020 Update documentation
        """;

    private static (string, string)[] Feature(string spec, string tasks) => [($"{Folder}/spec.md", spec), ($"{Folder}/tasks.md", tasks)];

    private static ParsedDeliveryFeature ParseOne(string spec, string tasks) => Parse(Feature(spec, tasks)).Features.Single();

    private static DeliveryStoryRow Row((string, string)[] baseline, (string, string)[] current, string? title = null)
    {
        var result = DeliverySnapshotComparison.Compare(Content(Parse(baseline), Monday), Content(Parse(current), Friday));
        return title is null ? result.Stories.Single() : result.Stories.Single(s => s.Title == title);
    }

    [Fact]
    public void A_feature_without_user_story_headings_has_no_fake_story_and_keeps_criteria_feature_level()
    {
        var spec = "# Feature Specification: Placement export\n\n## Overview\n\nExports placements.\n\n## Success Criteria\n\n- **SC-001**: An export completes within 5 minutes\n";
        var feature = ParseOne(spec, "# Tasks\n\n- [ ] T001 Build export\n");
        feature.Stories.Should().BeEmpty("no explicit User Story section");
        feature.Criteria.Should().ContainSingle().Which.Should().Match<ParsedDeliveryCriterion>(c => c.StoryKey == null && c.Key == "SC-001");
        feature.Phases.Should().BeEmpty("no Phase N group: no phase is invented");
    }

    [Fact]
    public void User_stories_are_children_of_the_feature_with_their_own_acceptance_criteria()
    {
        var feature = ParseOne(Spec("Registration", ThreeStories, "Registrations are audited"), PhasedTasks());
        feature.Stories.Select(s => (s.Key, s.Title, s.Priority)).Should().Equal(("US1", "Register child", "P1"), ("US2", "Handle unknown placement category", "P2"), ("US3", "Audit registrations", "P3"));
        feature.Stories[0].Description.Should().Be("A caseworker wants to register child so that the child is followed up.", "the actual story text, without the priority rationale");
        feature.Criteria.Where(c => c.StoryKey == "US1").Select(c => c.Key).Should().Equal("AC-1", "AC-2");
        feature.Criteria.Where(c => c.StoryKey == "US2").Should().ContainSingle();
        feature.Criteria.Where(c => c.StoryKey == null).Should().ContainSingle(c => c.Key == "SC-001", "a success criterion stays feature-level, never duplicated under stories");
    }

    [Fact]
    public void Phases_come_only_from_explicit_phase_groups_and_tasks_link_to_stories_only_by_explicit_tags()
    {
        var feature = ParseOne(Spec("Registration", ThreeStories), PhasedTasks());
        feature.Phases.Select(p => (p.Key, p.Title)).Should().Equal(("phase-1", "Phase 1: Foundation"), ("phase-2", "Phase 2: Hardening"));
        feature.Stories.Single(s => s.Key == "US1").PhaseKeys.Should().Equal("phase-1");
        feature.Stories.Single(s => s.Key == "US3").PhaseKeys.Should().Equal("phase-2");
        var tasks = feature.Tasks.ToDictionary(t => t.TaskId!);
        (tasks["T003"].PhaseKey, tasks["T003"].UserStoryKey).Should().Be(("phase-1", (string?)null), "a phase task without a story tag stays at phase level");
        tasks["T002"].UserStoryKey.Should().Be("US2");
        tasks["T011"].UserStoryKey.Should().BeNull("\"Register child\" in the text is not a traceability marker");
        (tasks["T020"].PhaseKey, tasks["T020"].UserStoryKey).Should().Be(((string?)null, (string?)null), "\"Polish\" is not a Phase N group");
    }

    [Fact]
    public void A_task_tag_without_a_matching_story_stays_feature_level_and_is_reported()
    {
        var parsed = Parse(Feature(Spec("Registration", ThreeStories), "# Tasks\n\n## Phase 1: Setup\n\n- [ ] T001 [US7] Something\n"));
        parsed.Features.Single().Tasks.Single().UserStoryKey.Should().BeNull();
        parsed.Warnings.Should().Contain(w => w.Code == "TASK_STORY_UNKNOWN");
    }

    [Fact]
    public void Story_and_acceptance_criteria_changes_are_reported_per_level()
    {
        var changed = Story(1, "Register child", "P1", "**Given** a caseworker, **When** they register a child, **Then** the child is saved and confirmed", "**Given** a missing name, **When** they save, **Then** an error is shown")
            + Story(2, "Handle unknown placement category", "P2", "**Given** an unknown category, **When** imported, **Then** it is flagged")
            + Story(3, "Audit registrations", "P3", "**Given** a registration, **When** saved, **Then** an audit entry exists")
            + Story(4, "Archive registrations", "P3", "**Given** an old registration, **When** archived, **Then** it is read-only");
        var h = Row(Feature(Spec("Registration", ThreeStories), PhasedTasks()), Feature(Spec("Registration", changed), PhasedTasks())).Hierarchy!;
        h.Captured.Should().BeTrue();
        h.Stories.Single(s => s.Key == "US4").Change.Should().Be(DeliveryItemChange.Added);
        var us1 = h.Stories.Single(s => s.Key == "US1");
        us1.Change.Should().Be(DeliveryItemChange.Changed);
        us1.Criteria.Single(c => c.Key == "AC-1").Should().Match<DeliveryCriterionRow>(c => c.Change == DeliveryItemChange.Changed && c.BaselineText!.EndsWith("the child is saved") && c.Text.EndsWith("saved and confirmed"));
        us1.Criteria.Single(c => c.Key == "AC-2").Change.Should().Be(DeliveryItemChange.Unchanged);
        (h.CriteriaBaseline, h.CriteriaCurrent, h.CriteriaAdded, h.CriteriaChanged).Should().Be((4, 5, 1, 1));
        h.Stories.Single(s => s.Key == "US2").Change.Should().Be(DeliveryItemChange.Unchanged);
    }

    [Fact]
    public void Only_matched_open_to_checked_tasks_are_completed_this_period()
    {
        var row = Row(Feature(Spec("Registration", ThreeStories), PhasedTasks()), Feature(Spec("Registration", ThreeStories), PhasedTasks(t3Done: true, extra: true, reopen: true)));
        var tasks = row.Hierarchy!.Tasks.ToDictionary(t => t.TaskId!);
        tasks["T010"].Change.Should().Be(DeliveryTaskChange.CompletedThisPeriod);
        tasks["T012"].Should().Match<DeliveryTaskRow>(t => t.Change == DeliveryTaskChange.New && t.Completed, "a new checked task is new inventory, not completed this period");
        tasks["T002"].Change.Should().Be(DeliveryTaskChange.Reopened);
        row.TasksCompletedThisPeriod.Should().Be(1);
        (row.Hierarchy.TasksAdded, row.Hierarchy.TasksReopened).Should().Be((1, 1));
        row.Hierarchy.Phases.Single(p => p.Key == "phase-2").Should().Match<DeliveryPhaseRow>(p => p.Change == DeliveryItemChange.Changed && p.TaskCompleted == 2 && p.TaskTotal == 3);
        row.Hierarchy.Stories.Single(s => s.Key == "US3").Should().Match<DeliveryUserStoryRow>(s => s.TaskTotal == 2 && s.TaskCompleted == 2);
    }

    [Fact]
    public void A_new_feature_never_counts_its_checked_tasks_as_completed_this_period()
    {
        var result = DeliverySnapshotComparison.Compare(Content(Parse(ChildSearch(8, 3)), Monday), Content(Parse([.. ChildSearch(8, 3), .. Feature(Spec("Registration", ThreeStories), PhasedTasks())]), Friday));
        var added = result.Stories.Single(s => s.Title == "Registration");
        added.Should().Match<DeliveryStoryRow>(s => s.NewFeature && s.TasksCompletedThisPeriod == 0 && s.CurrentTaskCompleted == 3);
        added.Hierarchy!.Stories.Should().HaveCount(3, "the current state of a new feature");
        result.ScopeChanges.Should().ContainSingle(s => s.Kind == "Added" && s.Title == "Registration");
    }

    [Fact]
    public void M2LB_like_comparison_keeps_its_direction_removed_uses_the_baseline_and_a_changed_spec_stays_visible()
    {
        (string, string)[] SkoleService(string extra) =>
        [
            ("SkoleService/specs/001-skoleservice/spec.md", Spec("School attendance reporting", Story(1, "Report attendance", "P1", "**Given** a school, **When** it reports, **Then** attendance is stored") + extra)),
            ("SkoleService/specs/001-skoleservice/tasks.md", "# Tasks\n\n## Phase 1: Core\n\n- [x] T001 [US1] Store attendance\n- [ ] T002 [US1] Notify\n"),
        ];
        (string, string)[] Placements =
        [
            ("Tjeneste/specs/014-children-in-placements-per-period/spec.md", Spec("Children in Placements per Period", Story(1, "List placements", "P1", "**Given** a period, **When** listed, **Then** placements are shown"))),
            ("Tjeneste/specs/014-children-in-placements-per-period/tasks.md", "# Tasks\n\n- [x] T001 Build list\n- [ ] T002 Paging\n"),
        ];
        var baseline = Content(Parse([.. SkoleService(""), .. Placements], "M2LB (1)(9).zip"), Monday, "M2LB (1)(9).zip");
        var current = Content(Parse(SkoleService(Story(2, "Correct attendance", "P2", "**Given** a report, **When** corrected, **Then** the new value is kept")), "M2LB(20261030-100817).zip"), Friday, "M2LB(20261030-100817).zip");
        var result = DeliverySnapshotComparison.Compare(baseline, current);

        var removed = result.Stories.Single(s => s.Title == "Children in Placements per Period");
        removed.Should().Match<DeliveryStoryRow>(s => s.RemovedFeature && !s.NewFeature && s.Movement == DeliveryMovement.Removed && s.BaselineTaskCompleted == 1 && s.BaselineTaskTotal == 2);
        removed.Hierarchy!.Stories.Should().ContainSingle(s => s.Title == "List placements", "the last known baseline detail");
        var skole = result.Stories.Single(s => s.Title == "School attendance reporting");
        skole.SpecificationChanged.Should().BeTrue();
        skole.Hierarchy!.Stories.Single(s => s.Key == "US2").Change.Should().Be(DeliveryItemChange.Added);
        result.ScopeChanges.Should().ContainSingle(s => s.Kind == "Removed" && s.Title == "Children in Placements per Period");
        result.Stories.Should().NotContain(s => s.NewFeature, "the baseline is the earlier snapshot: nothing is new because of a reversed direction");
    }

    [Fact]
    public void A_snapshot_imported_before_hierarchy_support_is_never_reinterpreted()
    {
        var parsed = Parse(Feature(Spec("Registration", ThreeStories), PhasedTasks()));
        var legacy = Content(parsed, Monday) with { Features = [.. Content(parsed, Monday).Features.Select(f => f with { HierarchyCaptured = false, Stories = [], Phases = [], Criteria = [] })] };
        var h = DeliverySnapshotComparison.Compare(legacy, Content(parsed, Friday)).Stories.Single().Hierarchy!;
        h.Captured.Should().BeFalse();
        h.Stories.Should().BeEmpty();
    }
}
