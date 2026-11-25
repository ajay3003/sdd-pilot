using System.Reflection;
using BirkNext.Api.Controllers;
using BirkNext.Api.Data;
using BirkNext.Api.Models;
using BirkNext.Api.Services.Specifications;
using BirkNext.Api.Services.TestPlanning;
using BirkNext.Sdd;
using BirkNext.TestCoverage;
using BirkNext.TestPlanning;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BirkNext.Api.Tests.Services.TestPlanning;

/// <summary>
/// Fail-safe grouping (2026-11-25): a user story without a group is still a valid story; grouping never blocks testing, never invents a link and never
/// moves a confirmed decision silently. F15–F38 synthetic rules and persistence; F39–F46 the real SkoleService evidence (the API's own builder over the
/// unchanged spec.md + tasks.md) — what the source says, not a preferred outcome.
/// </summary>
public sealed class StoryGroupingFailSafeTests : IDisposable
{
    // ── Synthetic (clearly not source evidence) ─────────────────────────────────────────────────────────────────
    private static readonly RequirementSectionMap Sections = RequirementSections.Read("""
        ## Requirements
        ### Functional Requirements
        **Intake**
        - **FR-001**: a
        - **FR-002**: b
        **Quality**
        - **FR-014**: c
        - **FR-015**: d
        **Control**
        - **FR-008**: e
        - **FR-009**: f
        **Follow-up lists**
        - **FR-041**: g
        ### Key Entities
        - **FR-099**: outside the requirements region
        """);

    private static CoverageUserStory Story(string id, string[]? named = null, string[]? tasks = null, string[]? details = null, string title = "") => new()
    {
        Key = $"spec.md#{id.ToLowerInvariant()}", StoryId = id, Title = title.Length > 0 ? title : id, SpecificationPath = "spec.md", NamedRequirements = [.. named ?? []],
        TaskRequirements = [.. (tasks ?? []).Select((t, i) => new StoryTaskRequirement(t, $"T{i}", $"[{id}] task naming {t}", "tasks.md", i + 1, StoryTaskRequirement.TaskLine)),
            .. (details ?? []).Select((t, i) => new StoryTaskRequirement(t, $"T9{i}", $"  - detail naming {t}", "tasks.md", 100 + i, StoryTaskRequirement.TaskDetail))],
    };

    private static StoryGroupingEngine.StoryInputs In(CoverageUserStory s, string[]? scenarios = null, string[]? suggestions = null) =>
        new(s, [.. (scenarios ?? []).Select(x => (x, "scenario 1"))], [], [.. (suggestions ?? []).Select(x => (x, "regel"))]);

    private static StoryGroupingProfile One(StoryGroupingEngine.StoryInputs input, StoryGroupingDocument? doc = null) =>
        StoryGroupingEngine.Profiles([input], Sections, doc).Single();

    private static StoryGroupingDocument Doc(params StoryGroupingOperation[] ops) =>
        StoryGroupingEngine.Apply(new StoryGroupingDocument { ProjectKey = "p", SpecificationPath = "spec.md" }, ops, "anna", DateTimeOffset.UtcNow, _ => true);

    [Fact]
    public void F15_a_story_without_requirements_is_ungrouped_with_the_reason_never_hidden_or_guessed()
    {
        var p = One(In(Story("US2", title: "Follow-up lists")));
        (p.State, p.PrimaryArea, p.SuggestedArea, p.GroupId).Should().Be((GroupingState.NoRequirements, (string?)null, (string?)null, (string?)null));
        p.Explanation.Should().StartWith("Ingen tilknyttede funksjonelle krav.");
    }

    [Fact]
    public void F16_only_suggested_links_never_group_the_story()
    {
        var p = One(In(Story("US1"), suggestions: ["FR-014", "FR-015"]));
        p.State.Should().Be(GroupingState.OnlySuggestions);
        p.PrimaryArea.Should().BeNull("a suggestion never decides");
        p.Candidates.Should().Equal("Quality");
    }

    [Fact]
    public void F17_equally_plausible_areas_are_several_possible_groups_with_the_real_candidates()
    {
        var p = One(In(Story("US5", tasks: ["FR-001", "FR-014"])));
        p.State.Should().Be(GroupingState.NeedsGrouping);
        p.Candidates.Should().BeEquivalentTo(["Intake", "Quality"]);
        p.PrimaryArea.Should().BeNull();
        p.Explanation.Should().Contain("FR-001").And.Contain("FR-014");
    }

    [Fact]
    public void F18_requirements_outside_any_section_need_review_without_an_invented_area()
    {
        var p = One(In(Story("US1", tasks: ["FR-099"])));
        p.State.Should().Be(GroupingState.NeedsReview);
        p.Candidates.Should().BeEmpty();
        p.PrimaryArea.Should().BeNull();
        p.Explanation.Should().Contain("står ikke under noen funksjonsdel");
    }

    [Fact]
    public void F19_a_missing_source_keeps_confirmed_placements_and_orphaned_placements_are_kept_not_deleted()
    {
        var doc = Doc(new StoryGroupingOperation { Kind = StoryGroupingOperationKind.SetPrimaryArea, StoryKey = "spec.md#us1", PrimaryArea = "Quality" },
            new StoryGroupingOperation { Kind = StoryGroupingOperationKind.SetPrimaryArea, StoryKey = "spec.md#us9", PrimaryArea = "Intake" });
        // No specification text: no sections at all.
        var p = StoryGroupingEngine.Profiles([In(Story("US1", tasks: ["FR-014"]))], RequirementSectionMap.Empty, doc).Single();
        (p.State, p.PrimaryArea).Should().Be((GroupingState.Confirmed, "Quality"));
        StoryGroupingEngine.OrphanPlacements(doc, [In(Story("US1"))]).Select(o => o.StoryKey).Should().Equal("spec.md#us9");
    }

    [Fact]
    public void F20_odd_inputs_never_break_grouping()
    {
        var inputs = new[] { In(Story("US1", named: ["FR-", "fr-14", ""], tasks: ["FR-0001", "not-an-id"])), In(Story("US1")), In(Story("")) };
        var act = () => StoryGroupingEngine.Profiles(inputs, Sections, new StoryGroupingDocument { Placements = [new() { StoryKey = "spec.md#us1", GroupId = "gone" }] });
        act.Should().NotThrow();
        act().Should().HaveCount(3, "one profile per story input, also for duplicates (the page keys stories itself)");
    }

    [Fact]
    public void F21_a_new_story_gets_a_suggestion_but_never_a_confirmation()
    {
        var doc = Doc(new StoryGroupingOperation { Kind = StoryGroupingOperationKind.SetPrimaryArea, StoryKey = "spec.md#us1", PrimaryArea = "Quality" });
        var profiles = StoryGroupingEngine.Profiles([In(Story("US1", tasks: ["FR-014", "FR-015"])), In(Story("US9", named: ["FR-001"]))], Sections, doc);
        var fresh = profiles.Single(p => p.StoryKey == "spec.md#us9");
        (fresh.State, fresh.PrimaryArea).Should().Be((GroupingState.Suggested, "Intake"));
    }

    [Fact]
    public void F22_a_placement_naming_a_deleted_group_falls_back_to_ungrouped_and_says_why()
    {
        var doc = new StoryGroupingDocument { Placements = [new() { StoryKey = "spec.md#us1", GroupId = "gone", By = "anna" }] };
        var p = One(In(Story("US1", tasks: ["FR-001", "FR-014"])), doc);
        p.MissingGroupId.Should().Be("gone");
        p.GroupId.Should().BeNull();
        p.State.Should().NotBe(GroupingState.Confirmed, "the decision named a group that no longer exists");
        p.Explanation.Should().StartWith("Den valgte gruppen finnes ikke lenger.");
    }

    [Fact]
    public void F23_F38_a_confirmed_placement_survives_requirement_changes_and_is_flagged_for_review()
    {
        var doc = Doc(new StoryGroupingOperation { Kind = StoryGroupingOperationKind.SetPrimaryArea, StoryKey = "spec.md#us1", PrimaryArea = "Intake", SuggestedArea = "Intake" });
        One(In(Story("US1", tasks: ["FR-001", "FR-002"])), doc).NeedsReviewFlag.Should().BeFalse();
        var after = One(In(Story("US1", tasks: ["FR-014", "FR-015"])), doc); // the source now links Quality
        (after.State, after.PrimaryArea, after.SuggestedArea).Should().Be((GroupingState.Confirmed, "Intake", "Quality"));
        after.NeedsReviewFlag.Should().BeTrue("the changed suggestion is shown, never applied");
    }

    [Fact]
    public void F24_nothing_is_confirmed_without_a_persons_decision()
    {
        var inputs = new[] { In(Story("US1", named: ["FR-001"])), In(Story("US2", tasks: ["FR-014", "FR-015"])), In(Story("US3")) };
        StoryGroupingEngine.Profiles(inputs, Sections, null).Should().NotContain(p => p.State == GroupingState.Confirmed || p.KeptUngrouped);
    }

    [Fact]
    public void F25_every_story_input_has_exactly_one_profile()
    {
        var inputs = Enumerable.Range(1, 12).Select(i => In(Story($"US{i}", tasks: i % 3 == 0 ? [] : [i % 2 == 0 ? "FR-001" : "FR-014"]))).ToList();
        StoryGroupingEngine.Profiles(inputs, Sections, null).Select(p => p.StoryKey).Should().Equal(inputs.Select(i => i.Story.Key));
    }

    [Fact]
    public void F32_reassigning_a_story_moves_only_its_placement()
    {
        var doc = Doc(new StoryGroupingOperation { Kind = StoryGroupingOperationKind.CreateGroup, GroupId = "a", Name = "A" }, new StoryGroupingOperation { Kind = StoryGroupingOperationKind.CreateGroup, GroupId = "b", Name = "B" },
            new StoryGroupingOperation { Kind = StoryGroupingOperationKind.PlaceStory, StoryKey = "spec.md#us1", GroupId = "a" });
        var moved = StoryGroupingEngine.Apply(doc, [new StoryGroupingOperation { Kind = StoryGroupingOperationKind.PlaceStory, StoryKey = "spec.md#us1", GroupId = "b" }], "bo", DateTimeOffset.UtcNow, _ => true);
        moved.Placements.Single().GroupId.Should().Be("b");
        moved.Groups.Select(g => g.Name).Should().Equal("A", "B");
        moved.Decisions.Should().BeEquivalentTo(doc.Decisions);
    }

    [Fact]
    public void F33_F34_deleting_a_group_with_stories_needs_a_choice_and_never_cascades()
    {
        var doc = Doc(new StoryGroupingOperation { Kind = StoryGroupingOperationKind.CreateGroup, GroupId = "a", Name = "A" }, new StoryGroupingOperation { Kind = StoryGroupingOperationKind.CreateGroup, GroupId = "b", Name = "B" },
            new StoryGroupingOperation { Kind = StoryGroupingOperationKind.PlaceStory, StoryKey = "spec.md#us1", GroupId = "a" },
            new StoryGroupingOperation { Kind = StoryGroupingOperationKind.SetPrimaryArea, StoryKey = "spec.md#us1", PrimaryArea = "Quality" },
            new StoryGroupingOperation { Kind = StoryGroupingOperationKind.AddLink, StoryKey = "spec.md#us1", RequirementId = "FR-014" });
        var refuse = () => StoryGroupingEngine.Apply(doc, [new StoryGroupingOperation { Kind = StoryGroupingOperationKind.DeleteGroup, GroupId = "a" }], "bo", DateTimeOffset.UtcNow, _ => true);
        refuse.Should().Throw<StoryGroupingEngine.GroupingRefusal>().Which.Message.Should().Contain("1 brukerhistorier");

        var released = StoryGroupingEngine.Apply(doc, [new StoryGroupingOperation { Kind = StoryGroupingOperationKind.DeleteGroup, GroupId = "a", ReleaseMembers = true }], "bo", DateTimeOffset.UtcNow, _ => true);
        released.Groups.Select(g => g.Id).Should().Equal("b");
        released.Placements.Single().Should().Match<StoryPlacement>(p => p.GroupId == null && p.PrimaryArea == "Quality", "only the group goes; the area decision stays");
        released.Decisions.Should().BeEquivalentTo(doc.Decisions, "requirement links are never touched by grouping");

        var moved = StoryGroupingEngine.Apply(doc, [new StoryGroupingOperation { Kind = StoryGroupingOperationKind.DeleteGroup, GroupId = "a", TargetGroupId = "b" }], "bo", DateTimeOffset.UtcNow, _ => true);
        moved.Placements.Single().GroupId.Should().Be("b");
        moved.History.Last().Detail.Should().Contain("flyttet til «B»");
    }

    [Fact]
    public void F36_F37_a_manual_link_recalculates_the_suggestion_and_a_leave_ungrouped_decision_is_a_reviewed_state()
    {
        var input = In(Story("US1", tasks: ["FR-001"]));
        One(input).SuggestedArea.Should().Be("Intake");
        var doc = Doc(new StoryGroupingOperation { Kind = StoryGroupingOperationKind.AddLink, StoryKey = "spec.md#us1", RequirementId = "FR-014" },
            new StoryGroupingOperation { Kind = StoryGroupingOperationKind.AddLink, StoryKey = "spec.md#us1", RequirementId = "FR-015" });
        var linked = One(input, doc);
        (linked.SuggestedArea, linked.State).Should().Be(("Quality", GroupingState.Suggested));
        linked.Requirements.Single(r => r.RequirementId == "FR-014").Basis.Should().Be(StoryLinkBasis.HumanConfirmed);

        var kept = One(input, StoryGroupingEngine.Apply(doc, [new StoryGroupingOperation { Kind = StoryGroupingOperationKind.LeaveUngrouped, StoryKey = "spec.md#us1", SuggestedArea = "Quality" }], "anna", DateTimeOffset.UtcNow, _ => true));
        (kept.KeptUngrouped, kept.PrimaryArea, kept.GroupId).Should().Be((true, (string?)null, (string?)null));
        kept.Explanation.Should().StartWith("Bevisst uten gruppe (besluttet av anna).");
    }

    [Fact]
    public void F43_primary_and_related_areas_stay_distinct()
    {
        var p = One(In(Story("US1", tasks: ["FR-014", "FR-015", "FR-001"], details: ["FR-008"])));
        p.PrimaryArea.Should().Be("Quality");
        p.SecondaryAreas.Should().BeEquivalentTo(["Intake", "Control"]);
        p.Areas.Single(a => a.Area == "Control").SupportingOnly.Should().BeTrue("a task's sub-bullet shows a related area only");
        p.Explanation.Should().Contain("Også relatert");
    }

    [Fact]
    public void F44_a_control_area_many_stories_pass_through_never_decides_their_primary_area()
    {
        // «Control» is named by 4 of 5 linked stories: cross-cutting. US4 has its own area; US2 has only the control area.
        var inputs = new[]
        {
            In(Story("US1", tasks: ["FR-008", "FR-014", "FR-015"])), In(Story("US2", tasks: ["FR-008"])), In(Story("US3", tasks: ["FR-009", "FR-001", "FR-002"])),
            In(Story("US4", tasks: ["FR-008", "FR-041"]), scenarios: ["FR-041"]), In(Story("US5", tasks: ["FR-001", "FR-002"])),
        };
        var profiles = StoryGroupingEngine.Profiles(inputs, Sections, null).ToDictionary(p => p.StoryKey[^3..]);
        StoryGroupingEngine.CrossCuttingAreas([.. inputs.Select(i => (IReadOnlyList<StoryRequirementRelation>)StoryGroupingEngine.Relations(i, Sections, null))], []).Should().Contain("Control");
        profiles["us1"].SuggestedArea.Should().Be("Quality");
        profiles["us4"].SuggestedArea.Should().Be("Follow-up lists");
        (profiles["us2"].State, profiles["us2"].PrimaryArea).Should().Be((GroupingState.NeedsReview, (string?)null));
        profiles["us2"].Candidates.Should().Equal("Control");
        profiles["us2"].Explanation.Should().Contain("tverrgående");
    }

    [Fact]
    public void F45_a_title_that_matches_a_section_name_never_groups_a_story_without_links()
    {
        var p = One(In(Story("US8", title: "Follow-up lists for caseworkers")));
        p.PrimaryArea.Should().BeNull("no keyword-only grouping");
        p.State.Should().Be(GroupingState.NoRequirements);
    }

    [Theory]
    [InlineData("Utdanning resolves it [US-C1, UT-FR-001..005, EXC-05]", new string[0])]
    [InlineData("see [SR-FR-013] and FR-014", new[] { "FR-014" })]
    [InlineData("FR-008a and (FR-009)", new[] { "FR-008A", "FR-009" })]
    public void F46_another_services_requirement_references_are_never_read_as_this_specifications(string text, string[] expected) =>
        SpecificationEvidenceBuilder.NamedRequirements(text).Should().Equal(expected);

    // ── Persistence: failure, retry, permission, rollback ────────────────────────────────────────────────────────

    private readonly AppDbContext _db = new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    public void Dispose() => _db.Dispose();
    private const string Spec = "SkoleService/specs/001-skoleservice/spec.md";
    private StoryGroupingService Service() => new(_db, TimeProvider.System, NullLogger<StoryGroupingService>.Instance);
    private static StoryGroupingChangeRequest Change(int revision, string? requestId, params StoryGroupingOperation[] ops) =>
        new() { RequestId = requestId, ExpectedRevision = revision, Operations = [.. ops], RequirementCatalog = new ProjectRequirementCatalog { ProjectKey = "m2lb", Requirements = [new("FR-014", "x", Spec)] } };

    [Fact]
    public async Task F26_F27_a_refused_group_creation_saves_nothing_and_names_the_reason()
    {
        await Service().ApplyAsync("m2lb", Spec, Change(0, null, new StoryGroupingOperation { Kind = StoryGroupingOperationKind.CreateGroup, GroupId = "a", Name = "Mottak" }), "anna", default);
        var dup = () => Service().ApplyAsync("m2lb", Spec, Change(1, null, new StoryGroupingOperation { Kind = StoryGroupingOperationKind.CreateGroup, GroupId = "b", Name = "mottak" }), "bo", default);
        (await dup.Should().ThrowAsync<StoryGroupingService.GroupingException>()).Which.Should().Match<StoryGroupingService.GroupingException>(e =>
            e.Status == 400 && e.Code == "duplicate-group" && e.Message.Contains("Det finnes allerede en gruppe som heter «mottak»"));
        var empty = () => Service().ApplyAsync("m2lb", Spec, Change(1, null, new StoryGroupingOperation { Kind = StoryGroupingOperationKind.CreateGroup, Name = "  " }), "bo", default);
        (await empty.Should().ThrowAsync<StoryGroupingService.GroupingException>()).Which.Code.Should().Be("missing-name");
        var stored = await Service().GetAsync("m2lb", Spec, default);
        (stored.Revision, stored.Groups.Count).Should().Be((1, 1));
    }

    [Fact]
    public void F28_changing_grouping_needs_the_write_permission_reading_does_not()
    {
        var post = typeof(StoryGroupingController).GetMethod(nameof(StoryGroupingController.Apply))!;
        post.GetCustomAttribute<AuthorizeAttribute>()!.Policy.Should().Be(BirkNext.Api.Services.ActiveEventTesting.BirkNextPermissions.IntegrationConfigurationWritePolicy);
        typeof(StoryGroupingController).GetMethod(nameof(StoryGroupingController.Get))!.GetCustomAttribute<AuthorizeAttribute>().Should().BeNull();
    }

    [Fact]
    public async Task F29_F30_a_stale_or_partly_invalid_change_is_rolled_back_entirely()
    {
        await Service().ApplyAsync("m2lb", Spec, Change(0, null, new StoryGroupingOperation { Kind = StoryGroupingOperationKind.CreateGroup, GroupId = "a", Name = "A" },
            new StoryGroupingOperation { Kind = StoryGroupingOperationKind.PlaceStory, StoryKey = "us1", GroupId = "a" }), "anna", default);
        var partly = () => Service().ApplyAsync("m2lb", Spec, Change(1, null,
            new StoryGroupingOperation { Kind = StoryGroupingOperationKind.CreateGroup, GroupId = "b", Name = "B" },
            new StoryGroupingOperation { Kind = StoryGroupingOperationKind.PlaceStory, StoryKey = "us1", GroupId = "b" },
            new StoryGroupingOperation { Kind = StoryGroupingOperationKind.DeleteGroup, GroupId = "a" }), "bo", default); // last step refused after the move? no: "a" is empty by then
        await partly.Should().NotThrowAsync("the move empties «A» first, so deleting it is allowed in the same change");
        var bad = () => Service().ApplyAsync("m2lb", Spec, Change(2, null,
            new StoryGroupingOperation { Kind = StoryGroupingOperationKind.CreateGroup, GroupId = "c", Name = "C" },
            new StoryGroupingOperation { Kind = StoryGroupingOperationKind.PlaceStory, StoryKey = "us1", GroupId = "c" },
            new StoryGroupingOperation { Kind = StoryGroupingOperationKind.AddLink, StoryKey = "us1", RequirementId = "FR-777" }), "bo", default);
        (await bad.Should().ThrowAsync<StoryGroupingService.GroupingException>()).Which.Code.Should().Be("unknown-requirement");
        var stale = () => Service().ApplyAsync("m2lb", Spec, Change(1, null, new StoryGroupingOperation { Kind = StoryGroupingOperationKind.CreateGroup, GroupId = "d", Name = "D" }), "bo", default);
        (await stale.Should().ThrowAsync<StoryGroupingService.GroupingException>()).Which.Status.Should().Be(409);
        var stored = await Service().GetAsync("m2lb", Spec, default);
        stored.Revision.Should().Be(2);
        stored.Groups.Select(g => g.Name).Should().Equal(["B"], "no group of a refused change exists, partly or wholly");
        stored.Placements.Single().GroupId.Should().Be("b");
    }

    [Fact]
    public async Task F31_a_retried_change_is_stored_once()
    {
        var create = Change(0, "r0-abc", new StoryGroupingOperation { Kind = StoryGroupingOperationKind.CreateGroup, GroupId = "a", Name = "Mottak" });
        var first = await Service().ApplyAsync("m2lb", Spec, create, "anna", default);
        var retry = await Service().ApplyAsync("m2lb", Spec, create, "anna", default); // the answer to the first was lost
        retry.Revision.Should().Be(first.Revision);
        retry.Groups.Should().ContainSingle();
        (await Service().GetAsync("m2lb", Spec, default)).Groups.Should().ContainSingle("no duplicate group after a retry");
    }

    [Fact]
    public async Task F35_F52_F54_rename_persists_and_only_the_grouping_row_is_written()
    {
        _db.TestPlanDrafts.Add(new TestPlanDraftRecord { Id = Guid.NewGuid(), GuideKey = "m2lb", PlanName = "Testplan 2026-11-24", ContentJson = "{}" });
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        await Service().ApplyAsync("m2lb", Spec, Change(0, null, new StoryGroupingOperation { Kind = StoryGroupingOperationKind.CreateGroup, GroupId = "a", Name = "Mottak" }), "anna", default);
        var written = _db.ChangeTracker.Entries().Select(e => e.Entity.GetType()).Distinct().ToList();
        written.Should().Equal([typeof(StoryGroupingRecord)], "grouping never loads or writes plans, cases or runs");
        await Service().ApplyAsync("m2lb", Spec, Change(1, null, new StoryGroupingOperation { Kind = StoryGroupingOperationKind.RenameGroup, GroupId = "a", Name = "Mottak og behandling" }), "anna", default);
        (await Service().GetAsync("m2lb", Spec, default)).Groups.Single().Name.Should().Be("Mottak og behandling");
        (await _db.TestPlanDrafts.AsNoTracking().SingleAsync()).PlanName.Should().Be("Testplan 2026-11-24");
    }

    // ── Real SkoleService evidence ───────────────────────────────────────────────────────────────────────────────

    private const string SkolePath = "SkoleService/specs/001-skoleservice/spec.md";

    private static readonly Lazy<(CoverageWorkspaceEvidence Evidence, RequirementSectionMap Sections, Dictionary<string, StoryGroupingProfile> Profiles)> Real = new(() =>
    {
        var spec = RequirementSectionsTests.Spec(SkolePath);
        var tasks = RequirementSectionsTests.Spec("SkoleService/specs/001-skoleservice/tasks.md");
        var evidence = SpecificationEvidenceBuilder.Build(SkolePath, spec, null, [("SkoleService/specs/001-skoleservice/tasks.md", tasks)]);
        var sections = RequirementSections.Read(spec);
        var inputs = evidence.Stories.Select(s => new StoryGroupingEngine.StoryInputs(s,
            [.. evidence.AcceptanceScenarios.Where(a => a.StoryKey == s.Key).SelectMany(a => a.RequirementIds.Select(id => (id, $"scenario {a.NumberInStory}")))], [], [])).ToList();
        var profiles = StoryGroupingEngine.Profiles(inputs, sections, null);
        return (evidence, sections, profiles.ToDictionary(p => evidence.Stories.Single(s => s.Key == p.StoryKey).StoryId!));
    });

    private static IEnumerable<StoryTaskRequirement> Tasks(string story) => Real.Value.Evidence.Stories.Single(s => s.StoryId == story).TaskRequirements;

    [Fact]
    public void F39_US2_names_its_classification_requirement_in_its_tasks_phase_goal_and_is_suggested_there_not_ungrouped()
    {
        // Before 2026-11-25 US2 had no requirement at all: its only one (FR-008a) is in its tasks.md phase «Goal», which was never read.
        Tasks("US2").Should().Contain(t => t.RequirementId == "FR-008A" && t.Kind == StoryTaskRequirement.PhaseGoal && t.TaskText.StartsWith("**Goal**", StringComparison.Ordinal));
        var us2 = Real.Value.Profiles["US2"];
        us2.Requirements.Select(r => r.DisplayId).Should().Equal("FR-008a");
        (us2.State, us2.SuggestedArea).Should().Be((GroupingState.Suggested, "Classification"));
        us2.Explanation.Should().Contain("FR-008a");
    }

    [Fact]
    public void F40_US4_period_lock_rests_on_one_metrics_task_and_its_other_links_are_task_details()
    {
        Tasks("US4").Where(t => t.Kind == StoryTaskRequirement.TaskLine).Select(t => t.RequirementId).Distinct().Should().Equal("FR-026A");
        Tasks("US4").Where(t => t.Kind == StoryTaskRequirement.TaskDetail).Select(t => t.RequirementId).Distinct().Should().BeEquivalentTo(["FR-046", "FR-006", "FR-007", "FR-009"]);
        var us4 = Real.Value.Profiles["US4"];
        us4.State.Should().Be(GroupingState.NeedsReview, "a single task-line requirement is too little to group silently while other areas are touched");
        us4.Candidates.Should().Equal("Period lock");
        us4.PrimaryArea.Should().BeNull();
    }

    [Fact]
    public void F41_US5_corrections_outweighs_one_access_requirement_once_task_details_are_read()
    {
        var us5 = Real.Value.Profiles["US5"];
        us5.Areas.Select(a => a.Area).Should().Contain(["Corrections, chains and idempotency", "Access, audit and retention"]);
        us5.Explanation.Should().Contain("Corrections, chains and idempotency");
    }

    [Fact]
    public void F42_US8_has_no_source_link_to_the_follow_up_list_requirements_so_it_is_not_grouped_by_its_title()
    {
        var us8 = Real.Value.Profiles["US8"];
        us8.Requirements.Where(r => r.Established).Select(r => r.Area).Should().NotContain("Follow-up lists", "no story text, scenario or task names FR-041 / FR-042 for US8");
        us8.PrimaryArea.Should().NotBe("Classification", "Classification is cross-cutting and never decides US8's primary area on its own");
    }

    [Fact]
    public void F46_US7_never_reads_Utdanning_requirements_as_SkoleService_FR_001()
    {
        var us7 = Real.Value.Evidence.Stories.Single(s => s.StoryId == "US7");
        us7.NamedRequirements.Should().NotContain("FR-001", "the story text cites UT-FR-001..005, the Utdanning service's own requirements");
        Real.Value.Profiles["US7"].SuggestedArea.Should().Be("Education master data (Utdanning)");
    }
}
