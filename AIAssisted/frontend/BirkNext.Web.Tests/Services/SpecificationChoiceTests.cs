using BirkNext.Web.Models;
using BirkNext.Web.Services;
using BirkNext.Web.Services.Explorers;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// Manual Testing → Discover on a monorepo (M2LB, 2026-11-19): 39 specifications in 14 modules and none chosen used to read "This project has no
/// selected specification", which a tester read as "the import lost the specifications". The reason now states the counts and the choice to make.
/// </summary>
public sealed class SpecificationChoiceTests
{
    private static ArtifactExplorerState State(ExplorerArtifactStatus status, params (string Module, int Candidates)[] modules) =>
        new(WorkspaceArtifactType.Specification, status, "m2lb", "M2LB", [], Modules: [.. modules.Select(m => new ArtifactModuleRoleState(m.Module, m.Candidates, null, null))]);

    [Fact]
    public void A_monorepo_without_a_chosen_specification_names_the_counts_and_the_choice()
    {
        var text = RequirementLinkPresentation.SpecificationChoiceNeeded(State(ExplorerArtifactStatus.SelectionRequired, ("Frontend", 7), ("PersonService", 3), ("specs", 4)));
        text.Should().StartWith("This project contains 14 specifications in 3 modules and none is chosen yet.").And.Contain("Choose the module and specification you are testing");
    }

    [Fact]
    public void One_or_no_specification_or_a_loaded_choice_needs_no_choice()
    {
        RequirementLinkPresentation.SpecificationChoiceNeeded(null).Should().BeNull();
        RequirementLinkPresentation.SpecificationChoiceNeeded(State(ExplorerArtifactStatus.Empty)).Should().BeNull();
        RequirementLinkPresentation.SpecificationChoiceNeeded(State(ExplorerArtifactStatus.Empty, ("root", 1))).Should().BeNull();
    }

    [Fact]
    public void A_requirement_is_shown_as_its_statement_without_the_repeated_id_and_markup()
    {
        RequirementLinkPresentation.Statement("FR-001", "- **FR-001**: The system MUST accept one period summary per child.").Should().Be("The system MUST accept one period summary per child.");
        RequirementLinkPresentation.Statement("FR-002", "The system MUST resolve the child's `identifier`.").Should().Be("The system MUST resolve the child's identifier.");
        RequirementLinkPresentation.Statement("FR-003", "**FR-003**").Should().BeNull();
    }

    [Fact]
    public void A_chosen_module_with_several_specifications_names_the_module_not_the_project()
    {
        static ExplorerArtifact artifact(string id) => new(id, WorkspaceArtifactType.Specification, id, id + ".md", null, ExplorerArtifactSource.SampleProject, null, null, 1, "Discovered", ExplorerArtifactCurrentness.Current, null, Module: "SkoleService");
        var files = State(ExplorerArtifactStatus.SelectionRequired, ("SkoleService", 3), ("PersonService", 2)) with { Module = "SkoleService", Artifacts = [artifact("a"), artifact("b"), artifact("c")] };
        RequirementLinkPresentation.SpecificationChoiceNeeded(files).Should().StartWith("Module SkoleService has 3 specifications and none is chosen yet.").And.Contain("discover again");

        var packs = files with { PackRequired = true, Packs = [new ArtifactPackSummary("SkoleService", "001-a", null, "A", [], 2), new ArtifactPackSummary("SkoleService", "002-b", null, "B", [], 2)] };
        RequirementLinkPresentation.SpecificationChoiceNeeded(packs).Should().StartWith("Module SkoleService has 2 features, each with its own specification, and none is chosen yet.");

        var noOwn = packs with { Status = ExplorerArtifactStatus.Empty, PackRequired = false, Pack = "001-a", Artifacts = [] };
        RequirementLinkPresentation.SpecificationChoiceNeeded(noOwn).Should().StartWith("Feature A of module SkoleService has no specification of its own")
            .And.NotContain("none is chosen", "the feature is chosen; it simply has no specification");
    }
}
