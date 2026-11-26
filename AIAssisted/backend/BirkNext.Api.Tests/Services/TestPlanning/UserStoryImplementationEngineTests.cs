using BirkNext.Api.Services.ImplementationGaps;
using BirkNext.Api.Services.TestPlanning;
using BirkNext.ImplementationGaps;
using BirkNext.TestCoverage;
using BirkNext.TestPlanning;
using FluentAssertions;
using E = BirkNext.Api.Services.TestPlanning.UserStoryImplementationEngine;

namespace BirkNext.Api.Tests.Services.TestPlanning;

/// <summary>
/// The user story implementation status engine (2026-11-26) over task lines extracted exactly as a project import extracts them
/// (<see cref="ImplementationGapExtractor.Extract"/>). The fixtures are verbatim lines of the real M2LB package (M2LB.zip, Test-packages),
/// shortened only after the part the rules read (marker, id, tags, dependency wording).
/// </summary>
public sealed class UserStoryImplementationEngineTests
{
    private const string FrontendSpec = "Frontend/specs/002-child-search-profile/spec.md";

    // Frontend/specs/002-child-search-profile/tasks.md (real lines; descriptions shortened with «…»).
    private const string FrontendTasks = """
# Tasks: Child Search and Profile View

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies)
- **[Story]**: Which user story this task belongs to (e.g., US1, US2, US3)

## Phase 2: Foundational (Blocking Prerequisites)

- [X] T003 Add `CaseStatus` enum (values: `ITiltak`, `BestillingUnderBehandling`, `Reservert`) … to `M2LB.Frontend.Web/Modules/Person/Models/PersonDetailModels.cs`

## Phase 3: User Story 1 — Search for a Child (Priority: P1) 🎯 MVP

### Prerequisites for User Story 1

- [ ] T050 [US1] Extend `PersonSearchResult` model and API integration to carry `CaseStatus` and `Age` … (depends on T003; blocks T022, T025, T028)

### bUnit Tests for User Story 1

- [X] T022 [P] [US1] Write bUnit tests for `PersonSearchComponent`: search button disabled below 2 characters …
- [X] T023 [P] [US1] Write bUnit tests for `PersonSearchComponent`: default lists hidden when search results visible …
- [X] T024 [P] [US1] Write bUnit tests for `PersonPage`: `NavigationManager.NavigateTo` called with correct `/person/{childId}` path …

### Implementation for User Story 1

- [X] T025 [US1] Update `M2LB.Frontend.Web/Modules/Person/Components/PersonSearchComponent.razor`: replace `RadzenDataGrid` with card list … (depends on T022, T023)
- [X] T026 [P] [US1] Update `M2LB.Frontend.Web/Modules/Person/Components/PersonListComponent.razor` … (depends on T024)
- [X] T027 [P] [US1] Verify `M2LB.Frontend.Web/Modules/Person/Components/RecentlyViewedComponent.razor` … (depends on T024)
- [X] T028 [US1] Update `M2LB.Frontend.Web/Modules/Person/Components/PersonProfileCard.razor`: add `CaseStatus` badge … (depends on T003)
- [X] T029 [US1] Update `M2LB.Frontend.Web/Modules/Person/Pages/PersonPage.razor`: remove `DialogService` injection … (depends on T025–T028)

## Phase 4: User Story 2 — View Child Profile Overview (Priority: P1)

- [X] T036 [US2] **Prerequisite**: Read `M2LB.Frontend.Web/Modules/Person/Services/PersonDetailService.cs` … (depends on T030, T031)
- [ ] T042 [US2] Fix gender display: edit the `.graphql` query definition file for `GetBarn` … (depends on T036) — **deferred**: requires backend GraphQL schema extension; placeholder `KjoennType.Ukjent` in `PersonDetailPage.MapToChildDetail`
- [X] T043 [US2] Fix `CaseStatus` badge in `M2LB.Frontend.Web/Modules/Person/Components/PersonDetailContent.razor` … (depends on T037)

## Phase 5: User Story 3 — Filter the Event Timeline (Priority: P2)

- [X] T044 [P] [US3] Write bUnit tests for `TimelineSection`: "Vis alle" active by default shows all events newest-first …
- [X] T045 [US3] Update `M2LB.Frontend.Web/Modules/Person/Components/TimelineSection.razor` … (depends on T044)
- [X] T046 [US3] Wire `TimelineSection` in `M2LB.Frontend.Web/Modules/Person/Pages/PersonDetailPage.razor` … (depends on T036, T045)

## Phase 6: Polish & Cross-Cutting Concerns

- [ ] T047 [P] WCAG 2.1 AA audit across all new and modified components: verify minimum 4.5:1 colour contrast ratio …
- [X] T048 [P] Run `dotnet test` from repo root; confirm all bUnit tests in `M2LB.Frontend.Tests/Modules/Person/` pass …
""";

    private static CoverageUserStory Story(string spec, int n, string title) => new() { Key = CoverageUserStory.KeyOf(spec, $"US{n}"), StoryId = $"US{n}", Ordinal = n, Title = title, SpecificationPath = spec };

    private static List<UserStoryImplementationStatus> Run(string spec, string tasks, IReadOnlyList<CoverageUserStory> stories, IReadOnlyDictionary<string, E.Confirmation>? confirmations = null)
    {
        var docs = ImplementationGapExtractor.Extract([new(E.TasksPathOf(spec), tasks)]);
        return E.Compute(spec, "Frontend", stories, docs, confirmations);
    }

    private static readonly CoverageUserStory[] FrontendStories =
        [Story(FrontendSpec, 1, "Search for a Child"), Story(FrontendSpec, 2, "View Child Profile Overview"), Story(FrontendSpec, 3, "Filter the Child's Event Timeline")];

    [Fact]
    public void Frontend_child_search_US1_is_in_progress_with_8_of_9_tasks_and_the_one_open_task_named()
    {
        var us1 = Run(FrontendSpec, FrontendTasks, FrontendStories).Single(s => s.StoryId == "US1");

        us1.Status.Should().Be(UserStoryTaskStatus.InProgress);
        (us1.Completed, us1.Eligible, us1.Open, us1.ProgressPercent).Should().Be((8, 9, 1, 89));
        us1.Tasks.Where(t => t.State == UserStoryTaskState.Open).Select(t => t.TaskId).Should().Equal("T050");
        us1.Tasks.Should().OnlyContain(t => t.Link == UserStoryTaskLink.ExplicitTag);
        us1.Evidence.Should().Be(UserStoryEvidenceQuality.Complete);
        us1.Confirmation.Should().Be(UserStoryConfirmation.NotConfirmed, "a person has not confirmed it; checkboxes never do");
        us1.Tasks.Should().NotContain(t => t.TaskId == "T003" || t.TaskId == "T047", "foundational and polish tasks are shared, never in a story's count");
        us1.Dependencies.Where(d => d.ToTaskId == "T003").Select(d => d.FromTaskId).Should().BeEquivalentTo(["T050", "T028"], "«depends on T003» on both lines is explicit");
        us1.Dependencies.Where(d => d.ToTaskId == "T003").Should().OnlyContain(d => d.Kind == UserStoryDependencyKind.Blocking && !d.TargetOpen, "T003 is done");
        us1.Explanation.Should().StartWith("Statusen er beregnet fra oppgavene");
    }

    [Fact]
    public void Frontend_US2_keeps_its_deferred_open_task_as_remaining_work_and_US3_has_all_direct_tasks_checked_but_is_not_implemented()
    {
        var result = Run(FrontendSpec, FrontendTasks, FrontendStories);
        var us2 = result.Single(s => s.StoryId == "US2");
        us2.Status.Should().Be(UserStoryTaskStatus.InProgress);
        (us2.Completed, us2.Eligible).Should().Be((2, 3), "a deferred task that is not checked is still open work — never silently removed");
        us2.Tasks.Single(t => t.TaskId == "T042").StateNote.Should().StartWith("Utsatt");
        us2.Dependencies.Where(d => d.Kind == UserStoryDependencyKind.Unresolved).Select(d => d.ToTaskId).Should().BeEquivalentTo(["T030", "T031", "T037"],
            "the excerpt names tasks it does not contain: reported as unresolved, never guessed");

        var us3 = result.Single(s => s.StoryId == "US3");
        us3.Status.Should().Be(UserStoryTaskStatus.TasksCompleted);
        us3.ProgressPercent.Should().Be(100);
        UserStoryStatusText.Label(us3.Status).Should().Be("US-oppgaver fullført");
        us3.Explanation.Should().Contain("bekrefter ikke alene").And.Contain("implementert");
        us3.Confirmation.Should().Be(UserStoryConfirmation.NotConfirmed);
        us3.SharedOpen.Select(t => t.TaskId).Should().Equal(["T047"], "the open polish task is shown as shared context, not counted");
    }

    [Fact]
    public void Proxy_initial_setup_US1_has_no_completed_task_and_is_not_started()
    {
        const string spec = "Proxy/specs/001-proxy-initial-setup/spec.md";
        // Proxy/specs/001-proxy-initial-setup/tasks.md (real lines, shortened).
        const string tasks = """
## Phase 2: Foundational (Blocking Prerequisites)

- [ ] T005 Create `src/M2LB.Proxy.Api/appsettings.json` with the full configuration schema …

## Phase 3: User Story 1 — Developer Onboards and Runs the Service Locally (Priority: P1) 🎯 MVP

- [ ] T010 [P] [US1] Wire Microsoft.Identity.Web JWT bearer validation in `src/M2LB.Proxy.Api/Program.cs` …
- [ ] T011 [US1] Apply `RequireAuthorization()` to `MapReverseProxy()` in `src/M2LB.Proxy.Api/Program.cs` …
- [ ] T015 [P] [US1] Write integration tests in `tests/M2LB.Proxy.Integration/Pipeline/AuthenticationTests.cs` …
""";
        var us1 = E.Compute(spec, "Proxy", [Story(spec, 1, "Developer Onboards and Runs the Service Locally")], ImplementationGapExtractor.Extract([new(E.TasksPathOf(spec), tasks)])).Single();
        us1.Status.Should().Be(UserStoryTaskStatus.NotStarted);
        UserStoryStatusText.Label(us1.Status).Should().Be("Ikke startet iht. plan");
        (us1.Completed, us1.Eligible, us1.ProgressPercent).Should().Be((0, 3, 0));
    }

    [Fact]
    public void A_superseded_marker_defined_by_the_tasks_document_is_listed_but_not_counted()
    {
        const string spec = "TjenesteService/specs/004-child-placements-period/spec.md";
        // TjenesteService/specs/004-child-placements-period/tasks.md: the document's own revision log defines «[~]» (real text).
        const string tasks = """
> **Revision log 2026-10-27.** The tasks were first completed against an earlier design, and the code was then revised in four steps. Tasks are amended in place so they read as built. A task marked `[~]` was superseded: its original text is kept after "Superseded", and nothing is built for it today.

## Phase 4: User Story 2 — Placements are read for a child (Priority: P1)

- [~] T031 [P] [US2] **Superseded 2026-10-27 (`PlacementCategoryRules` removed).** Original text: Create `UnitTests/Domain/PlacementCategoryRulesTests.cs` …
- [X] T042 [US2] Implement the placements read endpoint …
""";
        var docs = ImplementationGapExtractor.Extract([new(E.TasksPathOf(spec), tasks)]);
        docs.MarkerConventions.Single().Value.Should().ContainKey("~").WhoseValue.Should().Contain("superseded");
        docs.Tasks.Single(t => t.TaskId == "T031").Marker.Should().Be("~", "the raw marker is kept");
        var us2 = E.Compute(spec, "TjenesteService", [Story(spec, 2, "Placements")], docs).Single();
        us2.Cancelled.Should().Be(1);
        (us2.Completed, us2.Eligible, us2.Status).Should().Be((1, 1, UserStoryTaskStatus.TasksCompleted));
        us2.Tasks.Single(t => t.TaskId == "T031").State.Should().Be(UserStoryTaskState.CancelledOrDeferred);
    }

    [Fact]
    public void An_unexplained_marker_such_as_X_slash_is_unknown_and_makes_the_percentage_unavailable()
    {
        const string spec = "AutorisasjonService/specs/001-translate-source-english/spec.md";
        // AutorisasjonService/specs/001-translate-source-english/tasks.md line 49 (real): «[X/]» is defined nowhere in the document.
        const string tasks = """
## Phase 3: User Story 1 — Layer 1: Autorisasjon.Domain (~25 files)

- [X/] T003 [P] [US1] Rename `DomeneException` → `DomainException` and its `Feilkode` property → `ErrorCode` in `src/Autorisasjon.Domain/Exceptions/`
- [X] T004 [P] [US1] Transliterate `Revisjonsoppforing` entity → `Revisjonsoppfoering` …
""";
        var docs = ImplementationGapExtractor.Extract([new(E.TasksPathOf(spec), tasks)]);
        docs.Tasks.Single(t => t.TaskId == "T003").Should().Match<GapTaskInput>(t => t.Marker == "X/" && t.State == GapTaskState.Unknown, "before 2026-11-26 this line was not read as a task at all");
        var us1 = E.Compute(spec, "AutorisasjonService", [Story(spec, 1, "Layer 1")], docs).Single();
        us1.Status.Should().Be(UserStoryTaskStatus.NeedsClarification);
        us1.ProgressPercent.Should().BeNull("a marker of unknown meaning is never counted as complete or open");
        us1.Evidence.Should().Be(UserStoryEvidenceQuality.Ambiguous);
        us1.Tasks.Single(t => t.TaskId == "T003").StateNote.Should().Contain("ikke forklart");
    }

    [Fact]
    public void A_task_states_partial_by_its_own_words_and_is_never_counted_complete()
    {
        // TjenesteService/specs/003-domain-sync-self-heal/tasks.md line 102 (real wording); adapted: placed in a story phase with a [US1] tag so it
        // counts for a story (in the source it is a polish task).
        const string spec = "TjenesteService/specs/003-domain-sync-self-heal/spec.md";
        const string tasks = """
## Phase 3: User Story 1 — Self-heal (Priority: P1)

- [~] T017 [US1] Execute the manual validation walkthrough in `specs/003-domain-sync-self-heal/quickstart.md`. **Partially done**: applied `AddDomainSyncStatusToBirkTiltak` …
- [X] T018 [US1] Add the self-heal job …
""";
        var us1 = Run(spec, tasks, [Story(spec, 1, "Self-heal")]).Single();
        us1.Tasks.Single(t => t.TaskId == "T017").State.Should().Be(UserStoryTaskState.Partial);
        (us1.Completed, us1.Partial, us1.Status, us1.ProgressPercent).Should().Be((1, 1, UserStoryTaskStatus.InProgress, 50));
    }

    [Fact]
    public void Ownership_conflicts_malformed_tags_and_duplicate_ids_are_reported_and_never_double_counted()
    {
        const string spec = "Demo/specs/001-demo/spec.md";
        const string tasks = """
## Phase 3: User Story 1 — First

- [X] T010 [US1] First story task
- [X] T011 [US2] Tagged for the second story but placed under the first
- [ ] T012 [US1-2] A range tag is not one story
- [X] T010 [US1] The same id again

## Phase 4: User Story 2 — Second

- [ ] T020 Untagged task in the second story's phase
""";
        var result = Run(spec, tasks, [Story(spec, 1, "First"), Story(spec, 2, "Second")]);
        var us1 = result.Single(s => s.StoryId == "US1");
        var us2 = result.Single(s => s.StoryId == "US2");
        us1.Tasks.Select(t => t.TaskId).Should().Equal("T010");
        us1.Diagnostics.Should().Contain(d => d.Contains("T012") && d.Contains("US1-2")).And.Contain(d => d.Contains("T010") && d.Contains("flere ganger"));
        us2.Tasks.Select(t => (t.TaskId, t.Link)).Should().Equal(("T011", UserStoryTaskLink.ConflictingPhase), ("T020", UserStoryTaskLink.StoryPhase));
        us2.Evidence.Should().Be(UserStoryEvidenceQuality.Ambiguous);
        us2.Diagnostics.Should().Contain(d => d.Contains("motstridende"));
    }

    [Fact]
    public void An_open_explicit_dependency_outside_the_story_holds_a_fully_checked_story_and_a_mention_is_only_related()
    {
        const string spec = "Demo/specs/002-deps/spec.md";
        const string tasks = """
## Phase 2: Foundational

- [ ] T004 Shared migration

## Phase 3: User Story 1 — Uses the migration

- [X] T010 [US1] Endpoint (depends on T004)
- [X] T011 [US1] Page; see T030 for the follow-up
- [X] T012 [US1] Wiring (requires T099)
""";
        var us1 = Run(spec, tasks, [Story(spec, 1, "Uses the migration")]).Single();
        us1.Status.Should().Be(UserStoryTaskStatus.DependencyPending);
        UserStoryStatusText.Label(us1.Status).Should().Be("Avhengighet gjenstår");
        us1.Dependencies.Should().ContainSingle(d => d.ToTaskId == "T004" && d.Kind == UserStoryDependencyKind.Blocking && d.TargetOpen);
        us1.Dependencies.Should().ContainSingle(d => d.ToTaskId == "T030" && d.Kind == UserStoryDependencyKind.Unresolved);
        us1.Dependencies.Should().ContainSingle(d => d.ToTaskId == "T099" && d.Kind == UserStoryDependencyKind.Unresolved);
        us1.SharedOpen.Select(t => t.TaskId).Should().Equal("T004");
    }

    [Fact]
    public void A_dependency_cycle_is_reported()
    {
        const string spec = "Demo/specs/003-cycle/spec.md";
        const string tasks = """
## Phase 3: User Story 1 — Cycle

- [ ] T010 [US1] A (depends on T011)
- [ ] T011 [US1] B (depends on T010)
""";
        Run(spec, tasks, [Story(spec, 1, "Cycle")]).Single().Diagnostics.Should().Contain(d => d.Contains("sirkel"));
    }

    [Fact]
    public void Missing_tasks_md_or_no_task_extraction_is_unknown_never_zero_percent_and_no_story_is_invented_from_tasks()
    {
        var withoutTasks = E.Compute(FrontendSpec, "Frontend", FrontendStories, ImplementationGapExtractor.Extract([new("Frontend/specs/009-other/tasks.md", FrontendTasks)]));
        withoutTasks.Should().OnlyContain(s => s.Status == UserStoryTaskStatus.Unknown && s.ProgressPercent == null && s.EvidenceText == "tasks.md mangler for spesifikasjonen.");
        E.Compute(FrontendSpec, "Frontend", FrontendStories, null).Should().OnlyContain(s => s.Status == UserStoryTaskStatus.Unknown && s.Evidence == UserStoryEvidenceQuality.Missing);
        Run(FrontendSpec, FrontendTasks, []).Should().BeEmpty("tasks.md alone never creates a user story");
    }

    [Fact]
    public void The_same_story_number_in_two_specifications_is_two_stories_with_their_own_tasks()
    {
        const string other = "SkoleService/specs/001-skoleservice/spec.md";
        var docs = ImplementationGapExtractor.Extract([new(E.TasksPathOf(FrontendSpec), FrontendTasks),
            new(E.TasksPathOf(other), "## Phase 3: User Story 1 — Report\n\n- [X] T001 [US1] Only task\n")]);
        var front = E.Compute(FrontendSpec, "Frontend", [FrontendStories[0]], docs).Single();
        var skole = E.Compute(other, "SkoleService", [Story(other, 1, "Report")], docs).Single();
        (front.Completed, front.Eligible).Should().Be((8, 9));
        (skole.Completed, skole.Eligible, skole.Status).Should().Be((1, 1, UserStoryTaskStatus.TasksCompleted));
        front.StoryKey.Should().NotBe(skole.StoryKey);
    }

    [Fact]
    public void A_confirmation_applies_to_the_task_evidence_it_was_given_on_and_a_changed_task_list_asks_for_it_again()
    {
        var us3 = Run(FrontendSpec, FrontendTasks, FrontendStories).Single(s => s.StoryId == "US3");
        var confirmed = new Dictionary<string, E.Confirmation> { [us3.StoryKey] = new(true, us3.EvidenceFingerprint, "anna", DateTimeOffset.UtcNow) };
        Run(FrontendSpec, FrontendTasks, FrontendStories, confirmed).Single(s => s.StoryId == "US3").Confirmation.Should().Be(UserStoryConfirmation.ConfirmedImplemented);

        var changed = FrontendTasks.Replace("- [X] T046 [US3] Wire", "- [X] T046 [US3] Rewire");
        var after = Run(FrontendSpec, changed, FrontendStories, confirmed).Single(s => s.StoryId == "US3");
        after.Confirmation.Should().Be(UserStoryConfirmation.ReconfirmationRequired);
        UserStoryStatusText.Label(after.Confirmation).Should().Be("Må bekreftes på nytt");
        Run(FrontendSpec, FrontendTasks, FrontendStories, new Dictionary<string, E.Confirmation> { [us3.StoryKey] = new(false, us3.EvidenceFingerprint, "anna", DateTimeOffset.UtcNow) })
            .Single(s => s.StoryId == "US3").Confirmation.Should().Be(UserStoryConfirmation.NotConfirmed, "a withdrawn confirmation");
    }

    [Fact]
    public void Inputs_extracted_before_markers_were_kept_use_only_the_unambiguous_states()
    {
        var docs = ImplementationGapExtractor.Extract([new(E.TasksPathOf(FrontendSpec), FrontendTasks)]);
        var old = docs with { Tasks = [.. docs.Tasks.Select(t => t with { Marker = null })], MarkerConventions = [] };
        var us1 = E.Compute(FrontendSpec, "Frontend", FrontendStories, old).Single(s => s.StoryId == "US1");
        (us1.Completed, us1.Eligible, us1.Status).Should().Be((8, 9, UserStoryTaskStatus.InProgress));
        var tilde = old with { Tasks = [.. old.Tasks.Select(t => t.TaskId == "T050" ? t with { State = GapTaskState.InProgress } : t)] };
        E.Compute(FrontendSpec, "Frontend", FrontendStories, tilde).Single(s => s.StoryId == "US1").Status.Should().Be(UserStoryTaskStatus.NeedsClarification,
            "an old [~] without its marker or the document's legend cannot be interpreted");
    }
}
