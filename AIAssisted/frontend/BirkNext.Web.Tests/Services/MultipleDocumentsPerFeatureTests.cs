using BirkNext.ProjectImport;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using BirkNext.Web.Services.Explorers;
using BirkNext.Web.Services.ProjectImport;
using BirkNext.Web.Tests.Pages;
using FluentAssertions;
using static BirkNext.Web.Tests.Services.ProjectImportActivationTests;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// Every multiple-file situation of a service (module) with several features, for every document role: which document each explorer shows
/// after a feature is chosen, when the tester has to choose, that a choice stays with its feature and role, and that the requirements manual
/// testing reads follow the chosen feature. Generic fixtures shaped like M2LB (services with specs/NNN-feature folders, module docs, project
/// docs, module and project constitutions).
/// </summary>
public sealed class MultipleDocumentsPerFeatureTests
{
    private readonly WorkspaceArtifactRepository _repository = new();
    private readonly MockSampleProjectDocumentResolver _samples = new();
    private readonly ArtifactExplorerContext _explorers;

    public MultipleDocumentsPerFeatureTests()
    {
        _samples.Repository = _repository;
        _explorers = new ArtifactExplorerContext(_repository, _samples, _samples);
    }

    private static string SpecOf(string title, string fr = "FR-001") =>
        $"# Feature Specification: {title}\n\n## User Scenarios & Testing\n\n### User Story 1 - {title} (Priority: P1)\n\n**Acceptance Scenarios**:\n\n1. **Given** a user, **When** they use {title}, **Then** it works.\n\n## Requirements\n\n### Functional Requirements\n\n- **{fr}**: System MUST support {title}.\n";

    private static string PlanOf(string title) => $"# Implementation Plan: {title}\n\n## Summary\n\nBuild {title}.\n\n## Technical Context\n\n**Language/Version**: C# 12\n\n## Project Structure\n";

    private static string TasksOf(string title) => $"# Tasks: {title}\n\n## Phase 1: Setup\n\n- [ ] T001 Create {title} project\n- [ ] T002 [US1] Add {title} entity\n";

    private static string ModelOf(string entity) => $"# Data Model: {entity}\n\n## Entities\n\n### {entity}\n\n| Field | Type | Nullable |\n|---|---|---|\n| Id | Guid | No |\n";

    private static string ConstitutionOf(string name) => $"# {name} Constitution\n\n## Core Principles\n\n### I. Test first\nEvery change MUST have tests.\n\n## Governance\n\nThis constitution supersedes other practices.\n";

    private void Import(params (string Path, string Content)[] documents)
    {
        var preview = Preview(new string('a', 64), source: false, documents);
        new ProjectImportActivation(_repository).Activate(preview, ProjectImportArtifactDiscovery.From(preview), Commit(preview, ProjectImportSourceState.NotDetected));
    }

    private Task<ArtifactExplorerState> State(WorkspaceArtifactType role) => _explorers.GetStateAsync(role);

    private async Task<string?> Shown(WorkspaceArtifactType role) => (await State(role)) is { Status: ExplorerArtifactStatus.Loaded, Selected: { } s } ? s.SourcePath : null;

    /// <summary>A monorepo: "events" has four features with every multiple-file shape; "school" has no feature folders and two specifications;
    /// "attendance" has one feature and a module document of the same role.</summary>
    private void ImportServices() => Import(
        // Project level: a project constitution and project notes that are not any feature's documents.
        (".specify/memory/constitution.md", ConstitutionOf("Platform")),
        ("docs/analysis/notes.md", SpecOf("Project analysis", "FR-900")),
        // events: its own constitution and a module-shared data model base.
        ("events/.specify/memory/constitution.md", ConstitutionOf("Events")),
        ("events/docs/data-model.md", ModelOf("EventBase")),
        // 001: one of everything.
        ("events/specs/001-intake/spec.md", SpecOf("Intake", "FR-101")),
        ("events/specs/001-intake/plan.md", PlanOf("Intake")),
        ("events/specs/001-intake/tasks.md", TasksOf("Intake")),
        ("events/specs/001-intake/data-model.md", ModelOf("Intake")),
        // 002: the Spec-Kit files plus extra documents of the same roles in the feature folder.
        ("events/specs/002-replay/spec.md", SpecOf("Replay", "FR-201")),
        ("events/specs/002-replay/spec-appendix.md", SpecOf("Replay appendix", "FR-299")),
        ("events/specs/002-replay/plan.md", PlanOf("Replay")),
        ("events/specs/002-replay/plan-rollout.md", PlanOf("Replay rollout")),
        ("events/specs/002-replay/tasks.md", TasksOf("Replay")),
        ("events/specs/002-replay/tasks-phase2.md", TasksOf("Replay phase 2")),
        ("events/specs/002-replay/research.md", "# Research: Replay\n\n## Decision: use the outbox\n\n**Rationale**: reliable.\n\n**Alternatives considered**: none.\n"),
        // 003: two specifications and two task lists, none of them the Spec-Kit file: the tester chooses.
        ("events/specs/003-rename/requirements-a.md", SpecOf("Rename A", "FR-301")),
        ("events/specs/003-rename/requirements-b.md", SpecOf("Rename B", "FR-302")),
        ("events/specs/003-rename/plan.md", PlanOf("Rename")),
        ("events/specs/003-rename/work-a.md", TasksOf("Rename A")),
        ("events/specs/003-rename/work-b.md", TasksOf("Rename B")),
        // 004: only a plan; no spec, tasks or data model of its own.
        ("events/specs/004-cleanup/plan.md", PlanOf("Cleanup")),
        // school: a Spec-Kit module (.specify) without feature folders and two specifications.
        ("school/.specify/memory/constitution.md", ConstitutionOf("School")),
        ("school/docs/main-spec.md", SpecOf("School", "FR-401")),
        ("school/docs/annex-spec.md", SpecOf("School annex", "FR-402")),
        // attendance: one feature plus a module document of the same role (the SkoleService shape); no constitution of its own.
        ("attendance/specs/001-attendance/spec.md", SpecOf("Attendance", "FR-601")),
        ("attendance/docs/dependencies.md", SpecOf("Attendance dependencies", "FR-699")),
        ("billing/specs/001-pay/spec.md", SpecOf("Pay", "FR-501")));

    private async Task Feature(string pack)
    {
        await _explorers.SelectModuleAsync("events");
        await _explorers.SelectPackAsync(pack, "events");
    }

    [Fact]
    public async Task One_document_per_role_every_explorer_shows_the_chosen_feature_s_own()
    {
        ImportServices();
        await Feature("001-intake");
        (await Shown(WorkspaceArtifactType.Specification)).Should().Be("events/specs/001-intake/spec.md");
        (await Shown(WorkspaceArtifactType.Plan)).Should().Be("events/specs/001-intake/plan.md");
        (await Shown(WorkspaceArtifactType.Tasks)).Should().Be("events/specs/001-intake/tasks.md");
        (await Shown(WorkspaceArtifactType.DataModel)).Should().Be("events/specs/001-intake/data-model.md");
        (await Shown(WorkspaceArtifactType.Constitution)).Should().Be("events/.specify/memory/constitution.md", "the module's constitution applies to its features");
    }

    [Fact]
    public async Task Extra_documents_of_a_role_in_the_feature_the_spec_kit_file_is_shown_and_the_others_stay_choosable()
    {
        ImportServices();
        await Feature("002-replay");
        foreach (var (role, file, extra) in new[]
        {
            (WorkspaceArtifactType.Specification, "spec.md", "spec-appendix.md"),
            (WorkspaceArtifactType.Plan, "plan.md", "plan-rollout.md"),
            (WorkspaceArtifactType.Tasks, "tasks.md", "tasks-phase2.md"),
        })
        {
            var state = await State(role);
            state.Status.Should().Be(ExplorerArtifactStatus.Loaded, role.ToString());
            state.Selected!.SourcePath.Should().Be($"events/specs/002-replay/{file}");
            state.Reason.Should().Be(ExplorerSelectionReason.PackFile);
            state.Artifacts.Select(a => a.SourcePath).Should().Contain($"events/specs/002-replay/{extra}", $"the other {role} document stays choosable");
            state.Artifacts.Should().OnlyContain(a => a.SourcePath!.StartsWith("events/specs/002-replay/") || a.Module == "events",
                "never another feature's or a project-level document");
        }
        (await State(WorkspaceArtifactType.Plan)).Artifacts.Should().NotContain(a => a.SourcePath!.EndsWith("research.md"), "research is its own role");
    }

    [Fact]
    public async Task Several_documents_and_no_spec_kit_file_the_tester_chooses_and_the_choice_stays_with_that_feature_and_role()
    {
        ImportServices();
        await Feature("003-rename");
        var spec = await State(WorkspaceArtifactType.Specification);
        spec.Status.Should().Be(ExplorerArtifactStatus.SelectionRequired);
        spec.AmbiguousInPack.Should().BeTrue();
        spec.Artifacts.Select(a => a.SourcePath).Should().BeEquivalentTo(["events/specs/003-rename/requirements-a.md", "events/specs/003-rename/requirements-b.md"]);
        (await State(WorkspaceArtifactType.Tasks)).Status.Should().Be(ExplorerArtifactStatus.SelectionRequired);
        (await Shown(WorkspaceArtifactType.Plan)).Should().Be("events/specs/003-rename/plan.md", "one plan needs no choice");

        _explorers.Select(WorkspaceArtifactType.Specification, spec.Artifacts.Single(a => a.SourcePath!.EndsWith("requirements-b.md")).Id);
        (await Shown(WorkspaceArtifactType.Specification)).Should().Be("events/specs/003-rename/requirements-b.md");
        (await State(WorkspaceArtifactType.Tasks)).Status.Should().Be(ExplorerArtifactStatus.SelectionRequired, "a specification choice never decides the task list");

        await Feature("002-replay");
        (await Shown(WorkspaceArtifactType.Specification)).Should().Be("events/specs/002-replay/spec.md", "the choice made for 003 does not reach 002");
        await Feature("003-rename");
        (await Shown(WorkspaceArtifactType.Specification)).Should().Be("events/specs/003-rename/requirements-b.md", "feature 003 keeps its choice");
    }

    [Fact]
    public async Task A_feature_missing_a_role_uses_the_module_s_shared_one_never_a_sibling_s_or_a_project_note()
    {
        ImportServices();
        await Feature("004-cleanup");
        (await Shown(WorkspaceArtifactType.Plan)).Should().Be("events/specs/004-cleanup/plan.md");
        (await Shown(WorkspaceArtifactType.DataModel)).Should().Be("events/docs/data-model.md", "the module's shared data model applies to its features");
        var spec = await State(WorkspaceArtifactType.Specification);
        spec.Status.Should().Be(ExplorerArtifactStatus.Empty, "no sibling's spec and no project note is used for the feature");
        spec.Artifacts.Select(a => a.SourcePath).Should().Equal(["docs/analysis/notes.md"], "the project note is only offered");
        var tasks = await State(WorkspaceArtifactType.Tasks);
        tasks.Status.Should().Be(ExplorerArtifactStatus.Empty);
        tasks.Artifacts.Should().BeEmpty("no other feature's task list is borrowed");
    }

    [Fact]
    public async Task A_module_without_feature_folders_and_two_specifications_asks_once_and_other_modules_are_unaffected()
    {
        ImportServices();
        await _explorers.SelectModuleAsync("school");
        var spec = await State(WorkspaceArtifactType.Specification);
        spec.Status.Should().Be(ExplorerArtifactStatus.SelectionRequired);
        spec.Artifacts.Should().HaveCount(2).And.OnlyContain(a => a.Module == "school");
        (await Shown(WorkspaceArtifactType.Constitution)).Should().Be("school/.specify/memory/constitution.md");

        _explorers.Select(WorkspaceArtifactType.Specification, spec.Artifacts.Single(a => a.SourcePath!.EndsWith("annex-spec.md")).Id);
        (await Shown(WorkspaceArtifactType.Specification)).Should().Be("school/docs/annex-spec.md");

        await _explorers.SelectModuleAsync("billing");
        await _explorers.SelectPackAsync("001-pay");
        (await Shown(WorkspaceArtifactType.Specification)).Should().Be("billing/specs/001-pay/spec.md");
        await _explorers.SelectModuleAsync("school");
        (await Shown(WorkspaceArtifactType.Specification)).Should().Be("school/docs/annex-spec.md", "the module keeps its choice");
    }

    [Fact]
    public async Task A_single_feature_module_asks_for_its_spec_and_keeps_the_module_document_as_an_alternative()
    {
        // V8 decision (2026-11-26): no automatic specification
        ImportServices();
        await _explorers.SelectModuleAsync("attendance");
        var pending = await State(WorkspaceArtifactType.Specification);
        pending.Status.Should().Be(ExplorerArtifactStatus.SelectionRequired, "the only feature is not chosen automatically");
        pending.PackRequired.Should().BeTrue();
        pending.Selected.Should().BeNull();

        await _explorers.SelectPackAsync("001-attendance");
        var spec = await State(WorkspaceArtifactType.Specification);
        spec.Status.Should().Be(ExplorerArtifactStatus.Loaded);
        spec.Selected!.SourcePath.Should().Be("attendance/specs/001-attendance/spec.md");
        spec.Artifacts.Select(a => a.SourcePath).Should().BeEquivalentTo(["attendance/specs/001-attendance/spec.md", "attendance/docs/dependencies.md"],
            "the module's own document stays choosable; no project note or other module's document is listed");
        (await Shown(WorkspaceArtifactType.Constitution)).Should().Be(".specify/memory/constitution.md", "without its own, the project's constitution applies");
    }

    /// <summary>
    /// Regression (2026-11-24, M2LB SkoleService): the module document was listed as an alternative, the choice was stored, but resolution looked for
    /// a stored choice among the feature's own files only — the feature's spec.md came back every time and the choice was silently undone.
    /// </summary>
    [Fact]
    public async Task Choosing_the_module_document_keeps_it_and_switching_back_and_forth_works()
    {
        ImportServices();
        await _explorers.SelectModuleAsync("attendance");
        await _explorers.SelectPackAsync("001-attendance");
        var listed = (await State(WorkspaceArtifactType.Specification)).Artifacts;
        string Id(string path) => listed.Single(a => a.SourcePath == path).Id;

        for (var round = 0; round < 3; round++)
        {
            _explorers.Select(WorkspaceArtifactType.Specification, Id("attendance/docs/dependencies.md"));
            var chosen = await State(WorkspaceArtifactType.Specification);
            chosen.Selected!.SourcePath.Should().Be("attendance/docs/dependencies.md", $"round {round}: the choice stays");
            chosen.Reason.Should().Be(ExplorerSelectionReason.Explicit);
            chosen.Module.Should().Be("attendance", "the module stays chosen");
            chosen.Content.Should().Contain("FR-699", "the chosen document's own content is read");

            _explorers.Select(WorkspaceArtifactType.Specification, Id("attendance/specs/001-attendance/spec.md"));
            (await Shown(WorkspaceArtifactType.Specification)).Should().Be("attendance/specs/001-attendance/spec.md", $"round {round}: switching back works");
        }
    }

    [Fact]
    public async Task Clearing_module_selection_keeps_no_specification_and_all_modules_restores_application_scope()
    {
        ImportServices();
        await _explorers.SelectModuleAsync("school");
        var candidates = await State(WorkspaceArtifactType.Specification);
        var selected = candidates.Artifacts.Single(a => a.SourcePath!.EndsWith("annex-spec.md", StringComparison.Ordinal));
        _explorers.Select(WorkspaceArtifactType.Specification, selected.Id);
        (await State(WorkspaceArtifactType.Specification)).Selected!.SourcePath.Should().EndWith("annex-spec.md");

        await _explorers.ClearSelectionAsync(WorkspaceArtifactType.Specification, "school");
        var cleared = await State(WorkspaceArtifactType.Specification);
        cleared.Module.Should().Be("school", "clearing the child specification preserves its parent module");
        cleared.Status.Should().Be(ExplorerArtifactStatus.SelectionRequired);
        cleared.Selected.Should().BeNull("the explicit empty choice prevents unique/previous selection fallback");

        await _explorers.ClearSelectionAsync(WorkspaceArtifactType.Specification, keepEmptyChoice: false);
        await _explorers.SelectModuleAsync(null);
        var project = await State(WorkspaceArtifactType.Specification);
        project.Module.Should().BeNull("Alle moduler returns to the project-level aggregate");
        project.Selected.Should().BeNull();
        project.Artifacts.Should().Contain(a => a.SourcePath == "events/specs/001-intake/spec.md");
        project.Artifacts.Should().Contain(a => a.SourcePath == "school/docs/annex-spec.md");
    }

    [Fact]
    public async Task Clearing_document_preserves_selected_pack_but_clearing_pack_returns_to_module_scope()
    {
        ImportServices();
        await _explorers.SelectModuleAsync("events");
        await _explorers.SelectPackAsync("003-rename", "events");
        var docs = await State(WorkspaceArtifactType.Specification);
        docs.Status.Should().Be(ExplorerArtifactStatus.SelectionRequired);
        docs.Artifacts.Should().HaveCount(2);
        _explorers.Select(WorkspaceArtifactType.Specification, docs.Artifacts[0].Id);

        await _explorers.ClearSelectionAsync(WorkspaceArtifactType.Specification, "events", "003-rename");
        var documentCleared = await State(WorkspaceArtifactType.Specification);
        documentCleared.Module.Should().Be("events");
        documentCleared.Pack.Should().Be("003-rename", "clearing the document keeps the specification pack");
        documentCleared.Selected.Should().BeNull();
        documentCleared.Artifacts.Should().HaveCount(2);

        await _explorers.ClearSelectionAsync(WorkspaceArtifactType.Specification, "events", keepEmptyChoice: false);
        await _explorers.SelectPackAsync(null, "events");
        var specificationCleared = await State(WorkspaceArtifactType.Specification);
        specificationCleared.Module.Should().Be("events");
        specificationCleared.Pack.Should().BeNull();
        specificationCleared.PackRequired.Should().BeTrue("clearing the specification keeps only its parent module in scope");
    }

    [Fact]
    public async Task The_chosen_module_document_survives_other_modules_and_a_rerender()
    {
        ImportServices();
        await _explorers.SelectModuleAsync("attendance");
        await _explorers.SelectPackAsync("001-attendance");
        var dependencies = (await State(WorkspaceArtifactType.Specification)).Artifacts.Single(a => a.SourcePath == "attendance/docs/dependencies.md").Id;
        _explorers.Select(WorkspaceArtifactType.Specification, dependencies);

        await Feature("002-replay");
        (await Shown(WorkspaceArtifactType.Specification)).Should().Be("events/specs/002-replay/spec.md", "another module never borrows the choice");
        await _explorers.SelectModuleAsync("attendance");
        (await Shown(WorkspaceArtifactType.Specification)).Should().Be("attendance/docs/dependencies.md", "back in the module, its choice stands");
        (await Shown(WorkspaceArtifactType.Specification)).Should().Be("attendance/docs/dependencies.md", "reading the state again changes nothing");
    }

    [Fact]
    public async Task The_requirements_manual_testing_reads_follow_the_chosen_module_document()
    {
        ImportServices();
        var provider = new ReviewContextProvider(_repository, new WorkspaceUpdateCoordinator(), new ConstitutionAnalysisService(), new PlanAnalysisService(), new DataModelAnalysisService(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ReviewContextProvider>.Instance, _explorers);
        await _explorers.SelectModuleAsync("attendance");
        await _explorers.SelectPackAsync("001-attendance");
        await provider.RebuildAsync();
        provider.GetCurrent()!.GetRequirements().Select(r => r.Id).Should().Equal("FR-601");

        var dependencies = (await State(WorkspaceArtifactType.Specification)).Artifacts.Single(a => a.SourcePath == "attendance/docs/dependencies.md").Id;
        _explorers.Select(WorkspaceArtifactType.Specification, dependencies);
        await provider.RebuildAsync();

        provider.GetCurrent()!.GetRequirements().Select(r => r.Id).Should().Equal(["FR-699"], "the chosen specification's requirements, not the feature's");
    }

    [Fact]
    public async Task Switching_modules_and_back_restores_the_feature_and_every_role_follows_it()
    {
        ImportServices();
        await Feature("002-replay");
        await _explorers.SelectModuleAsync("school");
        await _explorers.SelectModuleAsync("events");
        foreach (var role in new[] { WorkspaceArtifactType.Specification, WorkspaceArtifactType.Plan, WorkspaceArtifactType.Tasks })
            (await State(role)).Pack.Should().Be("002-replay", $"{role} follows the feature last viewed in the module");
        (await Shown(WorkspaceArtifactType.Tasks)).Should().Be("events/specs/002-replay/tasks.md");
    }

    [Fact]
    public async Task The_requirements_manual_testing_reads_follow_the_chosen_feature_even_after_quick_switches()
    {
        ImportServices();
        var coordinator = new WorkspaceUpdateCoordinator();
        var provider = new ReviewContextProvider(_repository, coordinator, new ConstitutionAnalysisService(), new PlanAnalysisService(), new DataModelAnalysisService(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ReviewContextProvider>.Instance, _explorers);
        static IEnumerable<string> Ids(ReviewContext? c) => c?.GetRequirements().Select(r => r.Id) ?? [];

        await Feature("001-intake");
        await provider.RebuildAsync();
        Ids(provider.GetCurrent()).Should().Equal("FR-101");

        // Quick switches: each raises a rebuild while the previous may still run; the last one always wins.
        await Feature("002-replay");
        var running = provider.RebuildAsync();
        await Feature("003-rename");
        _explorers.Select(WorkspaceArtifactType.Specification, (await State(WorkspaceArtifactType.Specification)).Artifacts.Single(a => a.SourcePath!.EndsWith("requirements-a.md")).Id);
        await provider.RebuildAsync();
        await running;
        Ids(provider.GetCurrent()).Should().Equal(["FR-301"], "the context ends on the last chosen specification");

        await Feature("004-cleanup");
        await provider.RebuildAsync();
        Ids(provider.GetCurrent()).Should().BeEmpty("a feature without a specification has no requirements, never a project note's");
    }
}
