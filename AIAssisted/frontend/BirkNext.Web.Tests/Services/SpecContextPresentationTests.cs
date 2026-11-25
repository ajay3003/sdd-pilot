using System.Runtime.CompilerServices;
using BirkNext.Web.Services;
using FluentAssertions;
using SC = BirkNext.Web.Services.SpecContextPresentation;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// «Fra spesifikasjonen» in Manual Testing (2026-11-25) on the real SkoleService specification (repository fixture): the existing semantic model's
/// edge cases, clarifications, success criteria, dependencies and out-of-scope items are shown, directly related to a scenario only through an
/// identifier the item itself names (FR-023 ≠ FR-023a), everything else at specification level; the source's own status markers are kept.
/// </summary>
public sealed class SpecContextPresentationTests
{
    private static string Fixture(string relative, [CallerFilePath] string here = "") =>
        File.ReadAllText(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "..", "backend", "BirkNext.Api.Tests", "Services", "TestPlanning", "Fixtures", "m2lb-specs", relative));

    private static readonly Lazy<IReadOnlyList<SC.Item>> Items = new(() =>
    {
        var text = Fixture("SkoleService/specs/001-skoleservice/spec.md");
        return SC.Items(SpecExplorerService.BuildSemanticModel(SpecExplorerService.Parse(text), text));
    });

    [Fact]
    public void Every_context_item_of_the_specification_is_reachable_at_specification_level()
    {
        var all = SC.For(Items.Value, [], null);
        all.Count(SC.Kind.EdgeCase, direct: false).Should().Be(27);
        all.Count(SC.Kind.Clarification, direct: false).Should().Be(48);
        all.Count(SC.Kind.SuccessCriterion, direct: false).Should().Be(11);
        all.Count(SC.Kind.Dependency, direct: false).Should().Be(13);
        all.Count(SC.Kind.OutOfScope, direct: false).Should().Be(7);
        all.Direct.Should().BeEmpty("nothing is related to a scenario that names nothing");
        SC.Summary(all).Should().StartWith("Ingen punkter i spesifikasjonen nevner");
    }

    [Fact]
    public void An_item_is_directly_related_only_through_an_identifier_it_names_and_FR_015_is_not_FR_015a()
    {
        var withA = SC.For(Items.Value, ["FR-023a"], null);
        withA.Direct.Should().Contain(i => i.Kind == SC.Kind.EdgeCase && i.Title.StartsWith("Intake outage around the deadline", StringComparison.Ordinal));
        var without = SC.For(Items.Value, ["FR-023"], null);
        without.Direct.Should().NotContain(i => i.Title.StartsWith("Intake outage around the deadline", StringComparison.Ordinal), "FR-023 is a different requirement from FR-023a");
        withA.Direct.Should().NotContain(i => i.Kind == SC.Kind.OutOfScope, "out of scope is specification-level context");
        SC.Summary(withA).Should().Contain("spesielle situasjoner");
    }

    [Fact]
    public void A_dependency_and_a_success_criterion_relate_through_the_requirements_they_name()
    {
        SC.For(Items.Value, ["FR-008a"], null).Direct.Should().Contain(i => i.Kind == SC.Kind.Dependency && i.Id == "DEP-06");
        SC.For(Items.Value, ["FR-045"], null).Direct.Should().Contain(i => i.Kind == SC.Kind.SuccessCriterion && i.Id == "SC-007");
    }

    [Fact]
    public void The_sources_own_markers_are_kept_superseded_clarifications_and_closed_or_withdrawn_dependencies()
    {
        var clarifications = Items.Value.Where(i => i.Kind == SC.Kind.Clarification).ToList();
        clarifications.Count(i => i.Status == "Erstattet").Should().Be(3);
        clarifications.Count(i => i.Status == "Endret").Should().Be(4);
        clarifications.Where(i => i.Status == "Erstattet").Should().OnlyContain(i => i.Historical && !string.IsNullOrWhiteSpace(i.StatusNote), "a replaced answer stays visible with what replaced it");
        Items.Value.Where(i => i.Kind == SC.Kind.Dependency).Should().Contain(i => i.Status == "Lukket i spesifikasjonen").And.Contain(i => i.Status == "Trukket");
        Items.Value.Where(i => i.Kind == SC.Kind.Dependency).Should().NotContain(i => i.Status!.Contains("blokkert", StringComparison.OrdinalIgnoreCase),
            "a dependency in the document is never presented as a live blocker");
    }

    [Fact]
    public void Story_ids_compare_by_number_and_never_match_a_requirement()
    {
        SC.StoryNumber("US5").Should().Be("5");
        SC.StoryNumber("US05").Should().Be("5");
        SC.StoryNumber("User Story 5").Should().Be("5");
        SC.StoryNumber("FR-005").Should().BeNull();
        SC.StoryNumber("SC-005").Should().BeNull();
    }
}
