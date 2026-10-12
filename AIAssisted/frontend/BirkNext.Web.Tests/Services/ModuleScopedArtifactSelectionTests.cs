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
/// Monorepo imports: every candidate is kept, module identity comes from Spec-Kit structure only, a role resolves per module (one candidate
/// auto-selects, several need an explicit choice) and a choice for one module never changes another. Generic fixtures only.
/// </summary>
public sealed class ModuleScopedArtifactSelectionTests
{
    private readonly WorkspaceArtifactRepository _repository = new();
    private readonly MockSampleProjectDocumentResolver _samples = new();
    private readonly ArtifactExplorerContext _explorers;
    private readonly ProjectImportActivation _activation;

    private const string SpecB = "# Feature Specification: Billing\n\n## User Scenarios & Testing\n\n### User Story 1 - Pay (Priority: P1)\n\n## Requirements\n\n### Functional Requirements\n\n- **FR-001**: System MUST charge cards.\n";
    private const string SpecC = "# Feature Specification: Refunds\n\n## User Scenarios & Testing\n\n### User Story 1 - Refund (Priority: P1)\n\n## Requirements\n\n### Functional Requirements\n\n- **FR-001**: System MUST refund payments.\n";
    private const string PlanB = "# Implementation Plan: Billing\n\n## Summary\n\n## Technical Context\n\n**Language/Version**: Java 21\n\n## Project Structure\n";

    public ModuleScopedArtifactSelectionTests()
    {
        _samples.Repository = _repository;
        _explorers = new ArtifactExplorerContext(_repository, _samples, _samples);
        _activation = new ProjectImportActivation(_repository);
    }

    /// <summary>Two modules: "cart" (one spec, one plan) and "billing" (two specs: two features), plus Spec-Kit templates.</summary>
    private ProjectImportPreview ImportMonorepo()
    {
        var preview = Preview(new string('c', 64), source: true,
            ("cart/.specify/memory/constitution.md", Constitution),
            ("cart/.specify/templates/spec-template.md", Spec),
            ("cart/specs/001-cart/spec.md", Spec),
            ("cart/specs/001-cart/plan.md", Plan),
            ("billing/.specify/memory/constitution.md", Constitution.Replace("Shop", "Billing")),
            ("billing/specs/001-pay/spec.md", SpecB),
            ("billing/specs/001-pay/plan.md", PlanB),
            ("billing/specs/002-refund/spec.md", SpecC));
        _activation.Activate(preview, ProjectImportArtifactDiscovery.From(preview), Commit(preview, ProjectImportSourceState.Created, Guid.NewGuid()));
        return preview;
    }

    [Theory]
    [InlineData("Person/specs/001-a/spec.md", "Person")]
    [InlineData("Person/.specify/memory/constitution.md", "Person")]
    [InlineData("Person/autodoc/data-model.md", "Person")]
    [InlineData("services/Orders/specs/002-b/plan.md", "services/Orders")]
    [InlineData("specs/001-root/spec.md", "")]
    [InlineData("docs/architecture.md", "")]
    [InlineData("Other/notes/spec.md", "")]
    public void ModuleOf_UsesSpecKitRootsOnly(string path, string expected)
    {
        var roots = ArtifactModules.RootsOf([
            "Person/specs/001-a/spec.md", "Person/.specify/memory/constitution.md", "services/Orders/specs/002-b/plan.md", "specs/001-root/spec.md",
            "docs/architecture.md", "Other/notes/spec.md",
        ]);
        ArtifactModules.ModuleOf(path, roots).Should().Be(expected, "a module is a folder holding .specify/ or specs/, never a name in the file or text");
    }

    [Fact]
    public void FeatureOf_IsTheSpecsSubfolder_AndNeverAuthority()
    {
        ArtifactModules.FeatureOf("Person/specs/002-barn/spec.md").Should().Be("002-barn");
        ArtifactModules.FeatureOf("Person/.specify/memory/constitution.md").Should().BeNull();
    }

    [Fact]
    public async Task AllCandidatesAreKept_GroupedByModule_TemplatesExcluded()
    {
        ImportMonorepo();

        var overview = await _explorers.GetModuleOverviewAsync();
        overview.HasModules.Should().BeTrue();
        overview.Modules.Should().Equal("billing", "cart");
        var spec = overview.Role(WorkspaceArtifactType.Specification)!;
        spec.Candidates.Select(c => c.SourcePath).Should().BeEquivalentTo("cart/specs/001-cart/spec.md", "billing/specs/001-pay/spec.md", "billing/specs/002-refund/spec.md");
        spec.Candidates.Should().NotContain(c => c.SourcePath!.Contains("templates"), "Spec-Kit templates are never candidates");
        spec.Modules.Single(m => m.Module == "cart").Should().Match<ArtifactModuleRoleState>(m => m.Candidates == 1 && m.SelectedId == "workspace:cart/specs/001-cart/spec.md" && m.Reason == ExplorerSelectionReason.OnlyArtifact);
        spec.Modules.Single(m => m.Module == "billing").Should().Match<ArtifactModuleRoleState>(m => m.Candidates == 2 && m.SelectedId == null && m.NeedsSelection);
        spec.SelectedModules.Should().Be(1);
        spec.ModulesNeedingSelection.Should().Be(1);
    }

    [Fact]
    public async Task WithoutAFocusedModule_NothingIsPicked()
    {
        ImportMonorepo();

        var spec = await _explorers.GetStateAsync(WorkspaceArtifactType.Specification);
        spec.Status.Should().Be(ExplorerArtifactStatus.SelectionRequired);
        spec.ModuleRequired.Should().BeTrue("BirkNext never picks one module's specification for the whole project");
        spec.Artifacts.Should().HaveCount(3);
        spec.Selected.Should().BeNull();
    }

    [Fact]
    public async Task FocusedModule_WithOneCandidate_AutoSelects_AndOtherModulesDoNotLeak()
    {
        ImportMonorepo();

        await _explorers.SelectModuleAsync("cart");

        var spec = await _explorers.GetStateAsync(WorkspaceArtifactType.Specification);
        spec.Status.Should().Be(ExplorerArtifactStatus.Loaded);
        spec.Module.Should().Be("cart");
        spec.ModuleLabel.Should().Be("cart");
        spec.Reason.Should().Be(ExplorerSelectionReason.OnlyArtifact);
        spec.Artifacts.Should().OnlyContain(a => a.Module == "cart", "a module's view lists only its own candidates");
        spec.Selected!.Feature.Should().Be("001-cart");
        _repository.Specification!.Text.Should().Be(Spec, "older consumers read the focused module's selection");
    }

    [Fact]
    public async Task FocusedModule_WithSeveralCandidates_NeedsSelection_UntilChosen()
    {
        ImportMonorepo();
        await _explorers.SelectModuleAsync("billing");

        (await _explorers.GetStateAsync(WorkspaceArtifactType.Specification)).Status.Should().Be(ExplorerArtifactStatus.SelectionRequired);
        _repository.Specification.Should().BeNull("an ambiguous role has no current artifact, not the first file");

        _explorers.Select(WorkspaceArtifactType.Specification, "workspace:billing/specs/002-refund/spec.md");

        var spec = await _explorers.GetStateAsync(WorkspaceArtifactType.Specification);
        spec.Status.Should().Be(ExplorerArtifactStatus.Loaded);
        spec.Selected!.SourcePath.Should().Be("billing/specs/002-refund/spec.md");
        spec.Reason.Should().Be(ExplorerSelectionReason.Explicit);
        _repository.Specification!.Text.Should().Be(SpecC);
    }

    [Fact]
    public async Task SelectionForOneModule_DoesNotChangeAnother()
    {
        ImportMonorepo();
        await _explorers.SelectModuleAsync("cart");

        _explorers.Select(WorkspaceArtifactType.Specification, "workspace:billing/specs/001-pay/spec.md", "billing");

        var cart = await _explorers.GetStateAsync(WorkspaceArtifactType.Specification);
        cart.Selected!.SourcePath.Should().Be("cart/specs/001-cart/spec.md", "a choice for billing never changes the focused cart module");
        _repository.Specification!.Text.Should().Be(Spec);

        await _explorers.SelectModuleAsync("billing");
        (await _explorers.GetStateAsync(WorkspaceArtifactType.Specification)).Selected!.SourcePath.Should().Be("billing/specs/001-pay/spec.md");
        _repository.Specification!.Text.Should().Be(SpecB);
    }

    [Fact]
    public async Task RoleMissingInFocusedModule_IsEmpty_NotAnotherModulesArtifact()
    {
        var preview = Preview(new string('d', 64), source: false,
            ("cart/specs/001-cart/spec.md", Spec), ("cart/specs/001-cart/tasks.md", Tasks),
            ("billing/specs/001-pay/spec.md", SpecB));
        _activation.Activate(preview, ProjectImportArtifactDiscovery.From(preview), Commit(preview, ProjectImportSourceState.NotDetected));
        await _explorers.SelectModuleAsync("billing");

        var tasks = await _explorers.GetStateAsync(WorkspaceArtifactType.Tasks);
        tasks.Status.Should().Be(ExplorerArtifactStatus.Empty, "billing has no tasks; cart's tasks never stand in for it");
        tasks.Module.Should().Be("billing");
        _repository.Tasks.Should().BeNull();
    }

    [Fact]
    public async Task SelectionsAndFocus_SurviveASaveAndRestore()
    {
        ImportMonorepo();
        await _explorers.SelectModuleAsync("billing");
        _explorers.Select(WorkspaceArtifactType.Specification, "workspace:billing/specs/001-pay/spec.md");
        var json = System.Text.Json.JsonSerializer.Serialize(_repository.SddLifecycle);

        var restored = new WorkspaceArtifactRepository();
        restored.RestoreSddLifecycle(json);
        var samples = new MockSampleProjectDocumentResolver { Repository = restored };
        var explorers = new ArtifactExplorerContext(restored, samples, samples);

        var spec = await explorers.GetStateAsync(WorkspaceArtifactType.Specification);
        spec.Module.Should().Be("billing");
        spec.Selected!.SourcePath.Should().Be("billing/specs/001-pay/spec.md");
        restored.SddLifecycle.ExplorerSelections.Single().Module.Should().Be("billing");
    }

    [Fact]
    public async Task SimpleProject_StaysProjectLevel_AndAutoSelectsAsBefore()
    {
        var preview = Preview(new string('e', 64), source: true,
            ("shop/.specify/memory/constitution.md", Constitution), ("shop/specs/001-cart/spec.md", Spec),
            ("shop/specs/001-cart/plan.md", Plan), ("shop/specs/001-cart/tasks.md", Tasks));
        _activation.Activate(preview, ProjectImportArtifactDiscovery.From(preview), Commit(preview, ProjectImportSourceState.Created, Guid.NewGuid()));

        var spec = await _explorers.GetStateAsync(WorkspaceArtifactType.Specification);
        spec.Status.Should().Be(ExplorerArtifactStatus.Loaded);
        spec.HasModules.Should().BeFalse("one Spec-Kit project is not a monorepo: no module choice appears");
        spec.ModuleLabel.Should().BeNull();
        (await _explorers.GetModuleOverviewAsync()).HasModules.Should().BeFalse();
    }

    [Fact]
    public async Task SimpleProject_WithTwoSpecs_StillNeedsSelection_AndLegacySelectionsStillApply()
    {
        var preview = Preview(new string('f', 64), source: false,
            ("shop/specs/001-cart/spec.md", Spec), ("shop/specs/002-pay/spec.md", SpecB));
        _activation.Activate(preview, ProjectImportArtifactDiscovery.From(preview), Commit(preview, ProjectImportSourceState.NotDetected));

        (await _explorers.GetStateAsync(WorkspaceArtifactType.Specification)).Status.Should().Be(ExplorerArtifactStatus.SelectionRequired);

        // A selection saved before module scoping has no Module.
        _repository.SddLifecycle.ExplorerSelections.Add(new SddExplorerSelection
        {
            WorkspaceScope = _explorers.CurrentScope, Role = nameof(WorkspaceArtifactType.Specification), ArtifactId = "workspace:shop/specs/002-pay/spec.md",
        });
        (await _explorers.GetStateAsync(WorkspaceArtifactType.Specification)).Selected!.SourcePath.Should().Be("shop/specs/002-pay/spec.md");
    }

    [Fact]
    public async Task ProjectLevelDocuments_BesideModules_FormTheirOwnGroup()
    {
        var preview = Preview(new string('9', 64), source: false,
            ("cart/specs/001-cart/spec.md", Spec), ("billing/specs/001-pay/spec.md", SpecB), ("docs/plan.md", Plan));
        _activation.Activate(preview, ProjectImportArtifactDiscovery.From(preview), Commit(preview, ProjectImportSourceState.NotDetected));

        var overview = await _explorers.GetModuleOverviewAsync();
        overview.Modules.Should().Equal("billing", "cart", ArtifactModules.ProjectLevel);
        overview.Role(WorkspaceArtifactType.Plan)!.Candidates.Single().ModuleLabel.Should().Be(ArtifactModules.ProjectLevelLabel, "unknown module = project-level, never a guess");
    }
}
