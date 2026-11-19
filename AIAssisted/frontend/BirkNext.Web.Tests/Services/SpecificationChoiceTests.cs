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
        text.Should().StartWith("This project contains 14 specifications in 3 modules and none is chosen yet.").And.Contain("Specification Explorer");
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
}
