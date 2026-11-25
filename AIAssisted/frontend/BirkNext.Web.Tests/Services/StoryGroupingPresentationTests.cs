using BirkNext.Sdd;
using BirkNext.TestCoverage;
using BirkNext.TestPlanning;
using BirkNext.Web.Services;
using FluentAssertions;
using G = BirkNext.Web.Services.StoryGroupingPresentation;
using S = BirkNext.Web.Services.ScenarioBrowserPresentation;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// Finn og forstå's FR-based grouping (2026-11-24): inputs from what the page holds (the browser's own tasks.md links included), the navigation tree
/// per mode (every story once, ungroupable stories never hidden) and the preview of a change. Synthetic but shaped like SkoleService.
/// </summary>
public sealed class StoryGroupingPresentationTests
{
    private const string Path = "SkoleService/specs/001-skoleservice/spec.md";

    private static readonly RequirementSectionMap Sections = RequirementSections.Read("""
        ## Requirements
        ### Functional Requirements
        **Intake and identity**
        - **FR-001**: Accept a valid report.
        - **FR-002**: Resolve the child through Person.
        **Quality control (accept and flag)**
        - **FR-014**: Merge duplicate absence codes.
        - **FR-015**: Flag inconsistent totals.
        - **FR-015a**: Flag attendance above plan.
        **Period lock**
        - **FR-019**: Lock the period.
        """);

    private static CoverageUserStory Story(string id, int ordinal) => new()
    {
        Key = CoverageUserStory.KeyOf(Path, $"us{ordinal}"), StoryId = id, Ordinal = ordinal, Title = $"User Story {ordinal} - Story {id} (Priority: P1)", SpecificationPath = Path,
    };

    private static DiscoveredScenario C(string id, CoverageUserStory story, int number, params string[] requirements) => new()
    {
        CandidateId = id, ProjectKey = "m2lb", CapabilityId = ScenarioOwnership.Specification, Title = id, Purpose = id, Type = ScenarioCandidateType.BusinessFunctional,
        Confidence = CandidateConfidence.ExplicitSpecification, ExpectedBehaviour = "ok", Story = story, NumberInStory = number, RequirementIds = [.. requirements],
        Evidence = [new("specification", $"{Path}#{id}", "Akseptansescenario")],
    };

    private static readonly CoverageUserStory Us1 = Story("US1", 1), Us2 = Story("US2", 2), Us3 = Story("US3", 3);

    private static List<S.Item> Items() => S.Items([C("US1-AS1", Us1, 1), C("US1-AS2", Us1, 2), C("US2-AS1", Us2, 1, "FR-019"), C("US3-AS1", Us3, 1)], new Dictionary<string, S.Prepared>());

    /// <summary>The browser's own evidence of US1: tasks.md lines tagged [US1] (discovery's stored copy predates the tasks.md fix).</summary>
    private static CoverageWorkspaceEvidence Browser() => new()
    {
        Stories = [Us1 with { TaskRequirements = [new("FR-001", "T061", "[US1] intake", "tasks.md", 10), new("FR-002", "T062", "[US1] identity", "tasks.md", 11), new("FR-014", "T063", "[US1] flags", "tasks.md", 12)] }],
    };

    [Fact]
    public void P01_inputs_merge_the_browsers_tasks_links_into_the_discovered_story_and_scenario_links_are_kept()
    {
        var inputs = G.Inputs(Items(), Browser(), null);
        inputs.Should().HaveCount(3);
        inputs.Single(i => i.Story.StoryId == "US1").Story.TaskRequirements.Select(t => t.RequirementId).Should().Equal("FR-001", "FR-002", "FR-014");
        inputs.Single(i => i.Story.StoryId == "US2").ScenarioLinks.Select(l => l.RequirementId).Should().Equal("FR-019");
    }

    [Fact]
    public void P02_functional_tree_puts_every_story_once_in_source_order_and_keeps_unplaceable_ones_visible()
    {
        var profiles = StoryGroupingEngine.Profiles(G.Inputs(Items(), Browser(), null), Sections, null);
        var tree = G.Tree(G.Mode.Functional, profiles, null, Sections);
        // US2 only mentions FR-019 in a scenario: an incidental reference never places a story (2026-11-25), so US2 waits under «Uten gruppe».
        tree.Select(n => n.Title).Should().Equal("Intake and identity", "Uten gruppe");
        tree.SelectMany(n => n.Stories).Select(p => p.StoryKey).Should().OnlyHaveUniqueItems().And.HaveCount(3);
        tree.Single(n => n.Key == G.UngroupedKey).Stories.Select(p => p.StoryKey).Should().Equal(Us2.Key, Us3.Key);
        tree.First().Detail.Should().Be("2 krav i denne delen av spesifikasjonen");
    }

    [Fact]
    public void P03_business_tree_lists_groups_in_order_and_ungrouped_stories_under_uten_gruppe()
    {
        var doc = new StoryGroupingDocument
        {
            Groups = [new() { Id = "g2", Name = "Leveranse", Order = 1, FunctionalAreas = ["Period lock"] }, new() { Id = "g1", Name = "Mottak og behandling", Order = 0, FunctionalAreas = ["Intake and identity"] }],
        };
        var profiles = StoryGroupingEngine.Profiles(G.Inputs(Items(), Browser(), null), Sections, doc);
        var tree = G.Tree(G.Mode.Business, profiles, doc, Sections);
        tree.Select(n => n.Title).Should().Equal("Mottak og behandling", "Leveranse", "Uten gruppe");
        tree[0].Stories.Single().StoryKey.Should().Be(Us1.Key);
        tree[2].Stories.Select(p => p.StoryKey).Should().Equal(Us2.Key, Us3.Key);
        G.DefaultMode(doc, Sections).Should().Be(G.Mode.Business);
        G.DefaultMode(null, Sections).Should().Be(G.Mode.Functional);
        G.DefaultMode(null, RequirementSectionMap.Empty).Should().Be(G.Mode.Stories);
    }

    [Fact]
    public void P04_the_preview_names_link_and_suggestion_changes_and_what_stays_unchanged()
    {
        var inputs = G.Inputs(Items(), Browser(), null);
        var known = new HashSet<string>(["FR-001", "FR-002", "FR-014", "FR-015", "FR-015A", "FR-019"], StringComparer.OrdinalIgnoreCase);
        var ops = new List<StoryGroupingOperation>
        {
            new() { Kind = StoryGroupingOperationKind.AddLink, StoryKey = Us1.Key, RequirementId = "FR-015", DisplayId = "FR-015" },
            new() { Kind = StoryGroupingOperationKind.AddLink, StoryKey = Us1.Key, RequirementId = "FR-015a", DisplayId = "FR-015a" },
        };
        var lines = G.Preview(inputs, Sections, new StoryGroupingDocument(), ops, known.Contains);
        lines.Should().Contain(l => l.StartsWith("US1: kravkoblinger") && l.Contains("+FR-015") && l.Contains("+FR-015a"));
        lines.Should().Contain("US1: grupperingsforslaget endres fra «Intake and identity» til «Quality control (accept and flag)».");
        lines.Last().Should().Contain("testtilfeller").And.Contain("endres ikke");
        G.Preview(inputs, Sections, new StoryGroupingDocument(), [new() { Kind = StoryGroupingOperationKind.AddLink, StoryKey = Us1.Key, RequirementId = "FR-999" }], known.Contains)
            .Single().Should().StartWith("Kan ikke lagres");
    }

    [Fact]
    public void P05_labels_are_norwegian_and_keep_provenance_distinct()
    {
        G.BasisLabel(StoryLinkBasis.SourceExplicit).Should().Be("Nevnt i brukerhistorien");
        G.BasisLabel(StoryLinkBasis.HumanConfirmed).Should().Be("Bekreftet av en person");
        G.BasisLabel(StoryLinkBasis.Suggested).Should().Be("Foreslått");
        G.BasisTone(StoryLinkBasis.Suggested).Should().NotBe(G.BasisTone(StoryLinkBasis.HumanConfirmed), "a suggestion never looks confirmed");
        G.StateLabel(GroupingState.NeedsGrouping).Should().Be("Flere mulige grupper");
        G.ModeLabel(G.Mode.Functional).Should().Be("Funksjonelle områder");
    }
}
