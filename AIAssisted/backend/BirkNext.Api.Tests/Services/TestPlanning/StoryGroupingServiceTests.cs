using BirkNext.Api.Data;
using BirkNext.Api.Models;
using BirkNext.Api.Services.TestPlanning;
using BirkNext.TestPlanning;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BirkNext.Api.Tests.Services.TestPlanning;

/// <summary>
/// Persistence of user story grouping and requirement review decisions (2026-11-24): one revisioned document per project and specification,
/// stale saves refused, links only to the project's own specification, all-or-nothing changes, plans untouched.
/// </summary>
public sealed class StoryGroupingServiceTests : IDisposable
{
    private readonly AppDbContext _db = new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    public void Dispose() => _db.Dispose();

    private const string Spec = "SkoleService/specs/001-skoleservice/spec.md";
    private StoryGroupingService Service() => new(_db, TimeProvider.System, NullLogger<StoryGroupingService>.Instance);

    private static ProjectRequirementCatalog Catalog(string project = "m2lb") => new()
    {
        ProjectKey = project,
        Requirements = [new("FR-001", "Accept", Spec), new("FR-015", "Inconsistent", Spec), new("FR-015a", "Above plan", Spec), new("FR-001", "Person's FR-001", "PersonService/specs/001-person-module/spec.md")],
    };

    private static StoryGroupingChangeRequest Change(int revision, params StoryGroupingOperation[] ops) => new() { ExpectedRevision = revision, Operations = [.. ops], RequirementCatalog = Catalog() };
    private static StoryGroupingOperation Add(string req) => new() { Kind = StoryGroupingOperationKind.AddLink, StoryKey = "us1", RequirementId = req, DisplayId = req };

    [Fact]
    public async Task V01_an_unknown_project_has_an_empty_document_and_a_first_change_creates_it()
    {
        var empty = await Service().GetAsync("m2lb", Spec, default);
        empty.Revision.Should().Be(0);
        empty.Groups.Should().BeEmpty();

        var saved = await Service().ApplyAsync("m2lb", Spec, Change(0, Add("FR-015a")), "tester", default);
        saved.Revision.Should().Be(1);
        (await Service().GetAsync("m2lb", "\\" + Spec.Replace('/', '\\'), default)).Decisions.Should().ContainSingle(d => d.RequirementId == "FR-015A" && d.DisplayId == "FR-015a" && d.By == "tester");
    }

    [Fact]
    public async Task V_kind_a_scenarios_manual_or_technical_choice_round_trips_and_older_documents_read_without_it()
    {
        // 2026-11-25: a reviewer's test kind per scenario is stored in the specification's grouping document, revisioned, with history.
        var saved = await Service().ApplyAsync("m2lb", Spec, new StoryGroupingChangeRequest { ExpectedRevision = 0, RequirementCatalog = Catalog(),
            Operations = [new StoryGroupingOperation { Kind = StoryGroupingOperationKind.SetScenarioTestKind, ScenarioId = "US1-AS2", StoryKey = "us1", TestKind = ScenarioTestKind.Technical, DisplayId = "AC 2" }] }, "anna", default);
        saved.Revision.Should().Be(1);
        var read = await Service().GetAsync("m2lb", Spec, default);
        StoryGroupingEngine.TestKindDecision(read, "US1-AS2").Should().Match<ScenarioTestKindDecision>(d => d.Kind == ScenarioTestKind.Technical && d.By == "anna");
        read.History.Should().ContainSingle(h => h.Action == "Endret testtype" && h.Detail.StartsWith("AC 2: Teknisk"));
        _db.StoryGroupings.Single().ContentJson.Should().Contain("\"Technical\"", "stored by name, not by number");

        // A document saved before test kinds existed reads with none.
        var row = _db.StoryGroupings.Single();
        row.ContentJson = "{\"projectKey\":\"m2lb\",\"specificationPath\":\"" + Spec + "\",\"revision\":1}";
        await _db.SaveChangesAsync();
        (await Service().GetAsync("m2lb", Spec, default)).TestKinds.Should().BeEmpty();
    }

    [Fact]
    public async Task V_kind_Begge_is_stored_as_a_scenario_decision()
    {
        var saved = await Service().ApplyAsync("m2lb", Spec, new StoryGroupingChangeRequest { ExpectedRevision = 0, RequirementCatalog = Catalog(),
            Operations = [new StoryGroupingOperation { Kind = StoryGroupingOperationKind.SetScenarioTestKind, ScenarioId = "US1-AS3", StoryKey = "us1", TestKind = ScenarioTestKind.Both, DisplayId = "AC 3" }] }, "anna", default);
        StoryGroupingEngine.TestKindDecision(saved, "US1-AS3")!.Kind.Should().Be(ScenarioTestKind.Both);
        saved.History.Last().Detail.Should().StartWith("AC 3: Begge");
        _db.StoryGroupings.Single().ContentJson.Should().Contain("\"Both\"");
    }

    [Fact]
    public async Task V02_a_stale_revision_is_refused_and_nothing_is_saved()
    {
        await Service().ApplyAsync("m2lb", Spec, Change(0, Add("FR-001")), "anna", default);
        var stale = () => Service().ApplyAsync("m2lb", Spec, Change(0, Add("FR-015")), "bo", default);
        (await stale.Should().ThrowAsync<StoryGroupingService.GroupingException>()).Which.Should().Match<StoryGroupingService.GroupingException>(e => e.Status == 409 && e.CurrentRevision == 1);
        (await Service().GetAsync("m2lb", Spec, default)).Decisions.Should().ContainSingle();
    }

    [Fact]
    public async Task V03_only_requirements_of_this_specification_can_be_linked()
    {
        var other = () => Service().ApplyAsync("m2lb", Spec, new StoryGroupingChangeRequest
        {
            ExpectedRevision = 0, RequirementCatalog = Catalog() with { Requirements = [new("FR-777", "x", "PersonService/specs/001-person-module/spec.md")] },
            Operations = [Add("FR-777")],
        }, "tester", default);
        (await other.Should().ThrowAsync<StoryGroupingService.GroupingException>("another service's requirement is not this specification's")).Which.Code.Should().Be("unknown-requirement");
        var wrongProject = () => Service().ApplyAsync("m2lb", Spec, new StoryGroupingChangeRequest { RequirementCatalog = Catalog("other"), Operations = [Add("FR-001")] }, "tester", default);
        (await wrongProject.Should().ThrowAsync<StoryGroupingService.GroupingException>()).Which.Code.Should().Be("requirement-other-project");
        var noCatalog = () => Service().ApplyAsync("m2lb", Spec, new StoryGroupingChangeRequest { Operations = [Add("FR-001")] }, "tester", default);
        (await noCatalog.Should().ThrowAsync<StoryGroupingService.GroupingException>("without a catalog no requirement is known")).Which.Code.Should().Be("unknown-requirement");
    }

    [Fact]
    public async Task V04_a_change_is_all_or_nothing()
    {
        var mixed = () => Service().ApplyAsync("m2lb", Spec, Change(0, Add("FR-001"), Add("FR-999")), "tester", default);
        await mixed.Should().ThrowAsync<StoryGroupingService.GroupingException>();
        (await _db.StoryGroupings.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task V05_documents_are_per_project_and_specification()
    {
        await Service().ApplyAsync("m2lb", Spec, Change(0, Add("FR-001")), "tester", default);
        (await Service().GetAsync("m2lb", "PersonService/specs/001-person-module/spec.md", default)).Decisions.Should().BeEmpty();
        (await Service().GetAsync("other", Spec, default)).Decisions.Should().BeEmpty();
    }

    [Fact]
    public async Task V06_grouping_changes_never_touch_plans_or_runs()
    {
        var plan = new TestPlanDraftRecord { Id = Guid.NewGuid(), GuideKey = "project:m2lb", PlanName = "Testplan", ContentJson = "{\"x\":1}", CreatedBy = "anna", UpdatedBy = "anna", Revision = 7 };
        _db.TestPlanDrafts.Add(plan);
        _db.ManualTestRuns.Add(new ManualTestRunRecord { Id = Guid.NewGuid(), DraftId = plan.Id, ProjectKey = "m2lb", CaseId = "c1", State = "Completed", Outcome = "Passed", ContentJson = "{}", Tester = "anna" });
        await _db.SaveChangesAsync();
        await Service().ApplyAsync("m2lb", Spec, Change(0,
            new StoryGroupingOperation { Kind = StoryGroupingOperationKind.CreateGroup, Name = "Mottak" },
            new StoryGroupingOperation { Kind = StoryGroupingOperationKind.PlaceStory, StoryKey = "us1", GroupId = null },
            Add("FR-001")), "tester", default);
        var after = await _db.TestPlanDrafts.AsNoTracking().SingleAsync();
        (after.Revision, after.ContentJson, after.UpdatedBy).Should().Be((7, "{\"x\":1}", "anna"));
        (await _db.ManualTestRuns.CountAsync()).Should().Be(1);
    }

    [Theory]
    [InlineData("", Spec)]
    [InlineData("m2lb", "")]
    [InlineData("m2lb", "../secrets/spec.md")]
    public async Task V07_invalid_identities_are_refused(string project, string spec)
    {
        var get = () => Service().GetAsync(project, spec, default);
        (await get.Should().ThrowAsync<StoryGroupingService.GroupingException>()).Which.Status.Should().Be(400);
    }
}
