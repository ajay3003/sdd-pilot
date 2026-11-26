using BirkNext.Api.Data;
using BirkNext.Api.Services.ImplementationGaps;
using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.Api.Services.ManualReview;
using BirkNext.Api.Services.Specifications;
using BirkNext.Api.Services.TestPlanning;
using BirkNext.Api.Tests.TestInfrastructure;
using BirkNext.TestPlanning;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BirkNext.Api.Tests.Services.TestPlanning;

/// <summary>
/// User story status for a project end to end on a temporary migrated database (2026-11-26): the specification catalog and the task lines a
/// project import captures, the status read without writing anything, a person's confirmation (append-only, with history), and a second import
/// with changed tasks asking for the confirmation again. Never the real database (TestPostgres refuses it).
/// </summary>
public sealed class UserStoryStatusServiceTests : IAsyncLifetime
{
    private const string Spec = "Frontend/specs/002-child-search-profile/spec.md";
    private const string SpecText = """
# Feature Specification: Child Search and Profile View

## User Scenarios & Testing

### User Story 1 - Search for a Child (Priority: P1)

A caseworker searches for a child.

**Acceptance Scenarios**:

1. **Given** the caseworker is on the child search page, **When** they type a child's name in the search field, **Then** a list of matching children appears with name, age, birth date, and current case status

### User Story 3 - Filter the Child's Event Timeline (Priority: P2)

**Acceptance Scenarios**:

1. **Given** a child has a mix of event types, **When** the caseworker opens the profile, **Then** the timeline shows all events newest-first with "Vis alle" active
""";
    private const string Tasks = """
## Phase 3: User Story 1 — Search for a Child (Priority: P1)

- [ ] T050 [US1] Extend `PersonSearchResult` model (depends on T003)
- [X] T022 [P] [US1] Write bUnit tests for `PersonSearchComponent`

## Phase 5: User Story 3 — Filter the Event Timeline (Priority: P2)

- [X] T044 [P] [US3] Write bUnit tests for `TimelineSection`
- [X] T045 [US3] Update `TimelineSection.razor`
""";

    private readonly string _database = TestPostgres.TemporaryName("birknext_uss");
    private AppDbContext Context() => new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(TestPostgres.Temporary(_database).ConnectionString).Options);

    public async Task InitializeAsync()
    {
        await Execute($"CREATE DATABASE \"{_database}\"");
        await using var db = Context();
        await DatabaseMigrationLock.MigrateAsync(db);
    }

    public async Task DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        await Execute($"DROP DATABASE IF EXISTS \"{_database}\" WITH (FORCE)");
    }

    private static async Task Execute(string sql)
    {
        await using var connection = new NpgsqlConnection(TestPostgres.Admin().ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>What a project import stores: the specification catalog and the extracted task lines (scope = the project key).</summary>
    private async Task ImportAsync(AppDbContext db, string importId, string tasks)
    {
        await new SpecificationCatalogService(db).CaptureAsync(importId, "sha-" + importId, null, [new SpecificationCatalogService.Document(Spec, SpecText)], default);
        await new ImplementationGapService(db, new IqrSourceStore(db), new ManualReviewDecisionStore(db))
            .EvaluateDocumentsAsync(ImplementationGapService.ImportScope(importId), importId, [new(Spec, SpecText), new(Spec.Replace("spec.md", "tasks.md"), tasks)], [], null, default);
    }

    private static UserStoryStatusService Service(AppDbContext db) => new(db, new SpecificationCatalogService(db), new ManualReviewDecisionStore(db));

    [Fact]
    public async Task The_status_comes_from_the_imported_tasks_reading_writes_nothing_and_a_confirmation_is_kept_with_its_evidence()
    {
        await using var db = Context();
        await ImportAsync(db, "imp-1", Tasks);
        var decisionsBefore = await db.ManualReviewDecisions.CountAsync();

        var view = await Service(db).StatusAsync("import:imp-1", null, default);
        view.TasksAvailable.Should().BeTrue();
        view.Stories.Select(s => (s.StoryId, s.Status, s.Completed, s.Eligible)).Should().BeEquivalentTo(
            [("US1", UserStoryTaskStatus.InProgress, 1, 2), ("US3", UserStoryTaskStatus.TasksCompleted, 2, 2)]);
        (await Service(db).StatusAsync("import:imp-1", Spec, default)).Stories.Should().HaveCount(2, "one specification");
        (await db.ManualReviewDecisions.CountAsync()).Should().Be(decisionsBefore, "reading the status writes nothing");

        var us3 = view.Stories.Single(s => s.StoryId == "US3");
        var confirmed = await Service(db).ConfirmAsync(new ConfirmUserStoryRequest("import:imp-1", us3.StoryKey, true, "Sjekket i testmiljøet"), "anna", default);
        confirmed.Confirmation.Should().Be(UserStoryConfirmation.ConfirmedImplemented);
        confirmed.ConfirmedBy.Should().Be("anna");
        confirmed.Status.Should().Be(UserStoryTaskStatus.TasksCompleted, "the confirmation is another dimension; the task status is unchanged");
        var history = await Service(db).HistoryAsync("import:imp-1", us3.StoryKey, default);
        history.Should().ContainSingle().Which.Should().Match<BirkNext.ManualReview.ManualReviewDecisionView>(d => d.AppliesTo == us3.EvidenceFingerprint && d.Note == "Sjekket i testmiljøet");

        // A new import of the same application where US3's tasks changed: the old confirmation is kept but asks to be given again.
        await ImportAsync(db, "imp-1", Tasks.Replace("Update `TimelineSection.razor`", "Update `TimelineSection.razor` and its filter tabs"));
        var after = (await Service(db).StatusAsync("import:imp-1", null, default)).Stories.Single(s => s.StoryId == "US3");
        after.Confirmation.Should().Be(UserStoryConfirmation.ReconfirmationRequired);
        (await Service(db).HistoryAsync("import:imp-1", us3.StoryKey, default)).Should().HaveCount(1, "history is never rewritten");
    }

    [Fact]
    public async Task Without_task_lines_every_story_is_unknown_and_confirming_needs_task_evidence_and_a_person()
    {
        await using var db = Context();
        await new SpecificationCatalogService(db).CaptureAsync("imp-2", "sha", null, [new SpecificationCatalogService.Document(Spec, SpecText)], default);
        var view = await Service(db).StatusAsync("import:imp-2", null, default);
        view.TasksAvailable.Should().BeFalse();
        view.Stories.Should().OnlyContain(s => s.Status == UserStoryTaskStatus.Unknown && s.ProgressPercent == null);
        var story = view.Stories[0].StoryKey;
        var noEvidence = () => Service(db).ConfirmAsync(new ConfirmUserStoryRequest("import:imp-2", story, true), "anna", default);
        (await noEvidence.Should().ThrowAsync<TestPlanException>()).Which.Code.Should().Be("no-task-evidence");
        var anonymous = () => Service(db).ConfirmAsync(new ConfirmUserStoryRequest("import:imp-2", story, true), " ", default);
        (await anonymous.Should().ThrowAsync<TestPlanException>()).Which.Code.Should().Be("actor-required");
        var unknown = () => Service(db).ConfirmAsync(new ConfirmUserStoryRequest("import:imp-2", Spec + "#US9", true), "anna", default);
        (await unknown.Should().ThrowAsync<TestPlanException>()).Which.Code.Should().Be("story-not-found");
    }
}
