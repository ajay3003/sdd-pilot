using BirkNext.TestCoverage;
using BirkNext.TestPlanning;
using BirkNext.Web.Services;
using BirkNext.Web.Services.Explorers;
using FluentAssertions;
using L = BirkNext.Web.Services.RequirementLinkPresentation;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// Audit 2026-11-20 ("Duplikat melding" in a project whose source has no specification): Plan &amp; prepare showed no parent User Story and told the
/// tester to choose a specification in the Specification Explorer, although the project has none. A case now says its parent User Story (found from
/// its linked requirements) or why none is identified, and a project without any specification says "Not identified in source". Synthetic data.
/// </summary>
public sealed class ParentStoryPresentationTests
{
    private static TestCaseDraft Case(ScenarioSourceKind kind, string capability, params string[] rules) => new()
    {
        Source = new ScenarioReference { Kind = kind, Owner = "import:x", ScenarioId = "tsc-1" },
        Generated = new TestCaseContent
        {
            Title = "Duplicate message", Capability = capability, Requirements = [.. rules.Select(r => new TestCaseRequirement(r, r, null))],
            Evidence = [new CandidateEvidenceRef("guide-scenario", "journey/duplicate")],
        },
    };

    private static L.Row Linked(string id) => new(id, id, "specs/001/spec.md", false, true, "In the current specification", "ok", true, "Kari", DateTimeOffset.UnixEpoch);

    private static readonly CoverageUserStory Story = new()
    {
        Key = "us2", StoryId = "US2", Title = "Receive each report once", SpecificationPath = "specs/001/spec.md", NamedRequirements = ["FR-017"],
    };

    [Fact]
    public void A_linked_requirement_named_by_a_story_identifies_the_parent_story()
    {
        var status = L.ParentStory(Case(ScenarioSourceKind.ProjectDiscovery, "specification"), [Linked("FR-017")], [Story], noSpecification: false);
        status.Stories.Should().ContainSingle().Which.StoryId.Should().Be("US2");
        status.Via.Should().Be("via FR-017");
        status.Reason.Should().BeNull();
    }

    [Fact]
    public void A_case_from_a_storys_own_acceptance_scenario_belongs_to_that_story_without_a_linked_requirement()
    {
        // Audit 2026-11-22 (M2LB): a case prepared under User Story 6 said "Parent User Story: Not identified: no requirement is linked".
        var c = Case(ScenarioSourceKind.ProjectDiscovery, "specification");
        c = c with { Generated = c.Generated with { Story = Story, NumberInStory = 3 } };
        var status = L.ParentStory(c, [], [Story], noSpecification: false);
        status.Stories.Should().ContainSingle().Which.StoryId.Should().Be("US2");
        status.Via.Should().Be("from its acceptance scenario 3");
        status.Reason.Should().BeNull();
    }

    [Fact]
    public void A_guide_case_says_the_guide_names_no_story_and_its_rule_is_not_a_requirement()
    {
        var status = L.ParentStory(Case(ScenarioSourceKind.ProjectDiscovery, "system-guide", "r-duplicate"), [], [Story], noSpecification: true);
        status.Stories.Should().BeEmpty();
        status.Reason.Should().Be("Not identified in source: this case comes from the System Understanding guide (journey/duplicate), which names no User Story; "
            + "r-duplicate is a guide rule, not a specification requirement.");
    }

    [Fact]
    public void Without_a_story_the_reason_depends_on_the_project()
    {
        L.ParentStory(Case(ScenarioSourceKind.ProjectDiscovery, "ui-source"), [], [], noSpecification: true).Reason
            .Should().Be("Not identified in source: this project has no specification with User Stories.");
        L.ParentStory(Case(ScenarioSourceKind.ProjectDiscovery, "ui-source"), [], [Story], noSpecification: false).Reason.Should().StartWith("Not identified: no requirement is linked");
        L.ParentStory(Case(ScenarioSourceKind.ProjectDiscovery, "ui-source"), [Linked("FR-099")], [Story], noSpecification: false).Reason
            .Should().Be("Not identified: no User Story of the specification names the linked requirements.");
    }

    [Fact]
    public void A_project_without_any_specification_is_not_in_source_not_unchosen()
    {
        var evidence = new CoverageWorkspaceEvidence { SpecificationAvailable = false };
        var none = new ArtifactExplorerState(WorkspaceArtifactType.Specification, ExplorerArtifactStatus.Empty, "import-x", "Project", []);
        L.NoSpecificationInProject(none).Should().BeTrue();
        L.EmptyReason(evidence, null, none).Should().Be(L.NotInSource);

        var unchosen = new ArtifactExplorerState(WorkspaceArtifactType.Specification, ExplorerArtifactStatus.SelectionRequired, "import-x", "Project", []);
        L.NoSpecificationInProject(unchosen).Should().BeFalse();
        L.EmptyReason(evidence, null, unchosen).Should().Contain("Choose the specification under \"Specification used for manual testing\"").And.NotContain("Specification Explorer");
        L.NoSpecificationInProject(null).Should().BeFalse("an unknown state is never reported as absent");
    }
}
