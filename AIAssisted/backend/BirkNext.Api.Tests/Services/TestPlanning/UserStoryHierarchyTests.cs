using BirkNext.Api.Services.TestPlanning;
using BirkNext.TestCoverage;
using BirkNext.TestPlanning;
using BirkNext.Web.Services;
using FluentAssertions;
using Xunit;

namespace BirkNext.Api.Tests.Services.TestPlanning;

/// <summary>
/// Feature → User Story → Acceptance Scenario from the shared specification parser: each scenario's parent story from the document structure
/// (never from nearby text), stable story identity (file + the story's own number, else its position — never the title), priority, "why this
/// priority" and Independent Test as written, in English and Norwegian; in-text story references and "Scenario N:" headings are never stories;
/// scenarios with no story stay without one. Discovery carries the story into the prepared case outside the scenario's fingerprint.
/// Synthetic specifications in own wording, in the formats found in M2LB (Spec Kit English, Norwegian, scenario headings).
/// </summary>
public sealed class UserStoryHierarchyTests
{
    private static BirkNext.Web.Models.SpecificationSemanticModel Model(string markdown) =>
        SpecExplorerService.BuildSemanticModel(SpecExplorerService.Parse(markdown), markdown);

    private const string English = """
        # Feature Specification: Attendance reporting

        ## User Scenarios & Testing *(mandatory)*

        ### User Story 1 - Accept a valid report (Priority: P1)

        A reporter submits a report and it is accepted. [US-A1, US-A3]

        **Why this priority**: Nothing reaches the service without it.

        **Independent Test**: Submit one valid report with the downstream service stubbed and verify the stored record.

        **Acceptance Scenarios**:

        1. **Given** a valid report, **When** it is submitted, **Then** it is accepted.
        2. **Given** a report without a child id, **When** it is submitted, **Then** it is rejected.

        ### User Story 6 - Expected population (Priority: P2)

        At lock the service works out which children were expected.

        **Independent Test**: Set up placements, lock the period and check every child's bucket.

        **Acceptance Scenarios**:

        1. **Given** a child in a state institution, **When** the period locks, **Then** the child is not expected.
        2. **Given** a child past compulsory school age, **When** the period locks, **Then** the child is not expected.
        3. **Given** a child with no education relationship, **When** the period locks, **Then** the child is not expected.

        ### Edge Cases

        - What happens when the period is locked twice?

        ## Requirements

        - **FR-027**: A child MUST be expected only when of compulsory school age.
        """;

    [Fact]
    public void Scenarios_are_attached_to_the_story_heading_they_are_written_under_and_numbered_within_it()
    {
        var model = Model(English);
        var stories = model.UserStories.Where(u => u.IsStoryHeading).ToList();

        stories.Select(s => s.StableKey).Should().Equal("US1", "US6");
        stories[0].Priority.Should().Be("P1");
        stories[1].Priority.Should().Be("P2");
        stories[0].Why.Should().Be("Nothing reaches the service without it.");
        stories[0].IndependentTest.Should().StartWith("Submit one valid report");
        stories[0].Description.Should().Contain("A reporter submits a report and it is accepted.").And.NotContain("Independent Test");
        stories[1].LineStart.Should().BeGreaterThan(stories[0].LineStart!.Value);

        var past = model.AcceptanceScenarios.Single(s => s.Given!.Contains("past compulsory"));
        past.ParentStoryKey.Should().Be("US6");
        past.NumberInStory.Should().Be(2, "it is the second scenario of User Story 6 (not the fifth of the file)");
        past.LinkedUserStories.Should().ContainSingle(u => u.StableKey == "US6");
        model.AcceptanceScenarios.Where(s => s.ParentStoryKey == "US1").Should().HaveCount(2);
        stories[1].LinkedAcceptanceScenarios.Should().HaveCount(3);
    }

    [Fact]
    public void An_in_text_story_reference_is_never_a_story_or_a_parent()
    {
        var model = Model(English);
        model.UserStories.Where(u => !u.IsStoryHeading).Should().OnlyContain(u => u.Ordinal == 0);
        model.AcceptanceScenarios.Should().OnlyContain(s => s.ParentStoryKey == "US1" || s.ParentStoryKey == "US6", "an in-text [US-A3] is not a parent");
    }

    [Fact]
    public void Norwegian_stories_with_Gitt_naar_saa_scenarios_are_read_like_English_ones()
    {
        const string norwegian = """
            # Funksjonsspesifikasjon: Forside

            ## Brukerscenarier og testing

            ### Brukerhistorie 1 — Overblikk ved innlogging (Prioritet: P1)

            Brukeren faar et overblikk.

            **Hvorfor denne prioriteten**: Forsiden er det foerste brukeren ser.

            **Selvstendig test**: Logg inn med en testbruker og se at forsiden vises.

            **Akseptansekriterier**:

            1. **Gitt** at brukeren er innlogget, **naar** forsiden lastes, **saa** vises sammendraget.
            2. **Gitt** at brukeren har tilgang til moduler, **når** forsiden vises, **så** vises kun tillatte lenker.
            """;
        var model = Model(norwegian);
        var story = model.UserStories.Single(u => u.IsStoryHeading);
        story.StableKey.Should().Be("US1");
        story.Priority.Should().Be("P1");
        story.Why.Should().StartWith("Forsiden er det");
        story.IndependentTest.Should().StartWith("Logg inn med en testbruker");
        model.AcceptanceScenarios.Should().HaveCount(2).And.OnlyContain(s => s.ParentStoryKey == "US1");
        var first = model.AcceptanceScenarios[0];
        first.Given.Should().Be("at brukeren er innlogget");
        first.When.Should().Be("forsiden lastes");
        first.Then.Should().Be("vises sammendraget.");
        model.AcceptanceScenarios[1].When.Should().Be("forsiden vises");
    }

    [Fact]
    public void Scenario_headings_directly_under_the_feature_are_scenarios_without_a_story()
    {
        const string headings = """
            # Funksjonsspesifikasjon: Hendelser

            ## Brukerscenarier

            ### Scenario 1: Saksbehandler ser tidslinjen

            **Gitt** at saksbehandler har tilgang til barnet
            **Når** saksbehandler åpner hendelsesvisningen
            **Så** vises en liste over hendelser

            ### Scenario 2: Tom seksjon uten steg

            Ingen steg her.
            """;
        var model = Model(headings);
        model.UserStories.Should().NotContain(u => u.IsStoryHeading, "a \"Scenario N:\" heading is not a user story");
        var scenario = model.AcceptanceScenarios.Should().ContainSingle("a scenario heading without steps is never an empty scenario").Subject;
        scenario.Title.Should().Be("Saksbehandler ser tidslinjen");
        scenario.Given.Should().Be("at saksbehandler har tilgang til barnet");
        scenario.When.Should().Be("saksbehandler åpner hendelsesvisningen");
        scenario.ParentStoryKey.Should().BeNull("Parent user story: not identified");
    }

    [Fact]
    public void An_unnumbered_story_is_identified_by_its_position_never_by_its_title()
    {
        const string unnumbered = """
            # Feature Specification: Search

            ## User Scenarios & Testing

            ### User Story - Search by name

            **Acceptance Scenarios**:

            1. **Given** a child exists, **When** the caseworker searches, **Then** the child is listed.
            """;
        var story = Model(unnumbered).UserStories.Single(u => u.IsStoryHeading);
        story.AuthoredId.Should().BeNull();
        story.StableKey.Should().Be("story-1");
        Model(unnumbered.Replace("Search by name", "Find a child by name")).UserStories.Single(u => u.IsStoryHeading).StableKey.Should().Be("story-1", "a new title is the same story");
    }

    [Fact]
    public void The_same_story_number_in_two_features_is_two_stories()
    {
        CoverageUserStory.KeyOf("SkoleService/specs/001-skoleservice/spec.md", "US6").Should().NotBe(CoverageUserStory.KeyOf("Person/specs/001-person-module/spec.md", "US6"));
        CoverageUserStory.KeyOf("SkoleService\\specs\\001-skoleservice\\spec.md", "US6").Should().Be("SkoleService/specs/001-skoleservice/spec.md#US6");
    }

    [Fact]
    public void Discovery_carries_the_story_into_the_case_outside_the_scenario_fingerprint()
    {
        var story = new CoverageUserStory { Key = "spec.md#US6", StoryId = "US6", Ordinal = 2, Title = "User Story 6 - Expected population (Priority: P2)", Priority = "P2", IndependentTest = "Lock and check buckets." };
        var scenario = new QaTestCandidate { Id = "scenario-5", Title = "Child past compulsory school age", Given = "a child past compulsory school age", When = "the period locks", Then = "the child is not expected", StoryKey = story.Key, NumberInStory = 2 };
        CoverageWorkspaceEvidence Workspace(CoverageUserStory? s) => new()
        {
            SpecificationAvailable = true, SpecificationArtifacts = [new("a", "spec.md", null, "fp")], AcceptanceScenarios = [scenario with { StoryKey = s?.Key }], Stories = s is null ? [] : [s],
        };
        var with = TestScenarioDiscoveryEngine.FromSpecification("p", Workspace(story)).Candidates.Single();
        var without = TestScenarioDiscoveryEngine.FromSpecification("p", Workspace(null)).Candidates.Single();

        with.Story!.StoryId.Should().Be("US6");
        with.NumberInStory.Should().Be(2);
        with.Evidence.Should().Contain(e => e.Kind == "story" && e.Reference == "spec.md#US6");
        with.Fingerprint.Should().Be(without.Fingerprint, "identifying the story is not a change of the scenario");
        with.CandidateId.Should().Be(without.CandidateId, "the scenario's identity does not change");
        without.Story.Should().BeNull();
        TestScenarioDiscoveryEngine.Content(with).Story!.IndependentTest.Should().Be("Lock and check buckets.");
    }

    [Fact]
    public void The_story_number_feeds_structural_requirement_suggestions_never_inherited_coverage()
    {
        var c = new TestCaseDraft
        {
            Source = new ScenarioReference { Kind = ScenarioSourceKind.ProjectDiscovery, ScenarioId = "tsc-1" },
            Generated = new TestCaseContent { Title = "Child past school age", Story = new CoverageUserStory { Key = "spec.md#US6", StoryId = "US6" } },
        };
        ScenarioPlanning.StructuralReferences(c).Should().Contain("US6").And.Contain("User Story 6");
        c.RequirementLinks.Should().BeEmpty("a story's requirements are never copied into its cases");
    }

    [Fact]
    public void Scenarios_of_one_story_are_prepared_in_the_specifications_order_and_nothing_else_moves()
    {
        var us6 = new CoverageUserStory { Key = "spec.md#US6", StoryId = "US6" };
        var us7 = new CoverageUserStory { Key = "spec.md#US7", StoryId = "US7" };
        DiscoveredScenario S(string id, CoverageUserStory? story, int? number) => new() { CandidateId = id, Story = story, NumberInStory = number };
        var listed = new List<DiscoveredScenario> { S("s42", us6, 3), S("ui", null, null), S("s40", us6, 1), S("s50", us7, 2), S("s49", us7, 1) };

        TestPlanSuggestionService.SpecificationOrder(listed).Select(c => c.CandidateId).Should().Equal(["s40", "ui", "s42", "s49", "s50"],
            "each story's scenarios take its slots in their number order; the scenario without a story keeps its place");
    }

    [Fact]
    public void A_case_follows_its_scenario_text_to_a_new_position_only_when_that_is_unambiguous()
    {
        DiscoveredScenario C(string id, string fp) => new() { CandidateId = id, Fingerprint = fp, CapabilityId = TestScenarioDiscoveryEngine.Specification };
        TestCaseDraft Case(string id, string fp) => new()
        {
            Source = new ScenarioReference { Kind = ScenarioSourceKind.ProjectDiscovery, ScenarioId = id, Fingerprint = fp }, Generated = new TestCaseContent { Title = id },
        };
        // A scenario was inserted before scenario 3: "c" moved from s3 to s4, "d" from s4 to s5; s3 now holds the new text "x".
        var plan = new TestPlanDraft { Cases = [Case("s1", "a"), Case("s3", "c"), Case("s4", "d")] };
        var latest = new[] { C("s1", "a"), C("s3", "x"), C("s4", "c"), C("s5", "d") };

        var moves = TestPlanSuggestionService.Renumbered(plan, latest);
        moves.ToDictionary(m => m.Key, m => m.Value.CandidateId).Should().BeEquivalentTo(new Dictionary<string, string> { ["s3"] = "s4", ["s4"] = "s5" });

        // The same text twice in the file, or a target held by a case that stays: nothing moves (the case is flagged as changed instead).
        TestPlanSuggestionService.Renumbered(plan, [C("s1", "a"), C("s3", "x"), C("s4", "c"), C("s9", "c"), C("s5", "d")]).Keys.Should().Equal("s4");
        TestPlanSuggestionService.Renumbered(new TestPlanDraft { Cases = [Case("s1", "a"), Case("s2", "b")] }, [C("s1", "b"), C("s2", "z")]).Should().BeEmpty();
    }
}
