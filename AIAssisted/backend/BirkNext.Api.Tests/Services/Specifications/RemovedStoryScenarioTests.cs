using BirkNext.Api.Services.Specifications;
using BirkNext.Interpretation;
using FluentAssertions;

namespace BirkNext.Api.Tests.Services.Specifications;

/// <summary>Finn og forstå v3 (2026-11-26): acceptance scenarios of a REMOVED user story (struck through in spec.md, as M2LB TjenesteService 004
/// does for User Stories 3 and 5) are not offered — before, they surfaced as «scenarios without a user story».</summary>
public sealed class RemovedStoryScenarioTests
{
    private const string Spec = """
        # Feature Specification: Children in Placements per Period

        ## User Scenarios & Testing *(mandatory)*

        ### User Story 1 - List the children in placements during a period (Priority: P1)

        **Acceptance Scenarios**:

        1. **Given** a child in a foster home, **When** the period is requested, **Then** the child is returned.
        2. **Given** a placement that ended earlier, **When** the period is requested, **Then** it is not returned.

        ### ~~User Story 3 - Get the dates as they were recorded (Priority: P2)~~

        **Acceptance Scenarios**:

        1. **Given** a corrected end date, **When** the period is requested as recorded, **Then** the earlier date is returned.
        2. **Given** a removed placement, **When** it is requested as recorded, **Then** it is returned.

        ### User Story 4 - Narrow the question to a set of children (Priority: P2)

        **Acceptance Scenarios**:

        1. **Given** a request naming a set of children, **When** it is answered, **Then** only those children are returned.

        ## Requirements
        """;

    [Fact]
    public void Scenarios_under_a_struck_through_story_are_not_offered_and_none_becomes_a_scenario_without_a_story()
    {
        SpecStoryHeadings.RemovedSections(Spec).Should().ContainSingle();
        var evidence = SpecificationEvidenceBuilder.Build("TjenesteService/specs/004-x/spec.md", Spec, "TjenesteService");
        evidence.AcceptanceScenarios.Should().HaveCount(3, "two of User Story 1 and one of User Story 4; the two of the removed story are gone");
        evidence.AcceptanceScenarios.Should().OnlyContain(s => s.StoryKey != null, "no scenario is left without its story");
        evidence.AcceptanceScenarios.Should().NotContain(s => (s.Then ?? "").Contains("earlier date") || (s.Then ?? "").Contains("it is returned."));
        evidence.Stories.Select(s => s.StoryId).Should().Contain(["US1", "US4"]);
    }

    [Fact]
    public void A_removed_marker_after_the_title_also_removes_the_section_but_a_word_in_a_title_does_not()
    {
        SpecStoryHeadings.RemovedSections("### User Story 2 - Old flow — Removed 2026-10-27\n1. **Given** x, **When** y, **Then** z.\n### User Story 3 - New\n").Should().Equal([(1, 2)]);
        SpecStoryHeadings.RemovedSections("### User Story 5 - User deactivated when removed from Entra\n1. **Given** x\n").Should().BeEmpty();
    }
}
