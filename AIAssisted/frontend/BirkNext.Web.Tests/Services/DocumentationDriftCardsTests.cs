using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// Drift cards: one canonical primary state per module + documentation area, derived only from the existing group state and the saved
/// comparison (same matching rule as the composite's Status). Absence is neutral; priority sort, search and filter are deterministic.
/// </summary>
public sealed class DocumentationDriftCardsTests
{
    private static DriftDocument Doc(string id, DriftOrigin origin, string path, string fingerprint, string module = "Person") =>
        new(id, origin, id, System.IO.Path.GetFileName(path), path, origin == DriftOrigin.Authored ? "Data model" : "Generated · DataModel", fingerprint,
            "import-1", "M2LB · Import 1", "M2LB.zip", null, 1, null, origin == DriftOrigin.Generated ? Guid.Parse("148f9899-0000-0000-0000-000000000000") : null,
            module, null, true, true, "import:import-1");

    private static readonly DriftDocument Authored = Doc("a1", DriftOrigin.Authored, "Person/specs/001/data-model.md", "fa");
    private static readonly DriftDocument Generated = Doc("g1", DriftOrigin.Generated, "Person/autodoc/data-model.md", "fg");

    private static DriftCandidateGroup Group(string module, DriftGroupState state, DriftConcern concern = DriftConcern.DataModel, bool authored = true, bool generated = true) =>
        new("import-1", module, concern, generated ? Generated with { Module = module } : null, authored ? [Authored with { Module = module }] : [], [], state, "Comparison candidate");

    private static SddDocumentationDriftComparison Saved(params string[] kinds) => new()
    {
        CreatedAt = new DateTimeOffset(2026, 10, 29, 9, 0, 0, TimeSpan.Zero), ComparisonKind = "StructuredDataModel",
        Right = new SddDriftSide { Fingerprint = "fg" }, Contributors = [new SddDriftSide { Fingerprint = "fa" }],
        Items = [.. kinds.Select(k => new SddDriftItem { Kind = k })],
    };

    [Fact]
    public void States_ComeFromTheGroupAndTheSavedComparison_OnePrimaryStateEach()
    {
        DocumentationDriftCards.From(Group("Person", DriftGroupState.ComparisonAvailable), []).Should().Match<DriftCard>(c => c.State == DriftCardState.ReadyToCompare && c.Secondary == "Not yet reviewed");
        var consistent = DocumentationDriftCards.From(Group("Person", DriftGroupState.ComparisonAvailable), [Saved("Consistent", "Consistent")]);
        consistent.State.Should().Be(DriftCardState.Consistent);
        consistent.Secondary.Should().StartWith("Reviewed ");
        DocumentationDriftCards.From(Group("Person", DriftGroupState.ComparisonAvailable), [Saved("Consistent", "FieldDifference", "AuthoredOnly")])
            .Should().Match<DriftCard>(c => c.State == DriftCardState.Difference && c.Secondary == "2 concepts differ · review saved");
        DocumentationDriftCards.From(Group("Person", DriftGroupState.ComparisonAvailable), [Saved("NotConcludedAuthoredConflict", "FieldDifference")])
            .State.Should().Be(DriftCardState.Conflict, "only the comparison's own authored-conflict category is a conflict");
        DocumentationDriftCards.From(Group("Person", DriftGroupState.MultipleGeneratedCandidates), []).State.Should().Be(DriftCardState.ReviewRequired);
        DocumentationDriftCards.From(Group("Frontend", DriftGroupState.GeneratedNotPresent, generated: false), []).Should().Match<DriftCard>(c =>
            c.State == DriftCardState.GeneratedMissing && c.GeneratedText == "Missing" && c.Differences == 0, "missing generated documentation is not a difference");
        DocumentationDriftCards.From(Group("X", DriftGroupState.AuthoredNotPresent, authored: false), []).Should().Match<DriftCard>(c => c.State == DriftCardState.AuthoredMissing && c.AuthoredText == "Missing");
        DocumentationDriftCards.Tone(DriftCardState.ReadyToCompare).Should().Be("ready");
        DocumentationDriftCards.Tone(DriftCardState.GeneratedMissing).Should().Be("neutral", "absence is neutral");
        DocumentationDriftCards.Tone(DriftCardState.Conflict).Should().Be("conflict");
        DocumentationDriftCards.Tone(DriftCardState.Consistent).Should().Be("consistent");
    }

    [Fact]
    public void PrioritySort_SearchAndFilter_AreDeterministic()
    {
        var cards = DocumentationDriftCards.Cards(
        [
            Group("Zeta", DriftGroupState.ComparisonAvailable), Group("Frontend", DriftGroupState.GeneratedNotPresent, generated: false),
            Group("Autorisasjon", DriftGroupState.ComparisonAvailable), Group("X", DriftGroupState.AuthoredNotPresent, DriftConcern.Architecture, authored: false),
        ], []);
        cards.Select(c => c.Module).Should().Equal("Autorisasjon", "Zeta", "Frontend", "X");
        DocumentationDriftCards.Sort(cards, DriftCardSort.Module).Select(c => c.Module).Should().Equal("Autorisasjon", "Frontend", "X", "Zeta");
        DocumentationDriftCards.Filter(cards, null, "autoris").Select(c => c.Module).Should().Equal("Autorisasjon");
        DocumentationDriftCards.Filter(cards, null, "Data Model").Should().HaveCount(3);
        DocumentationDriftCards.Filter(cards, null, "autodoc/data-model").Select(c => c.Module).Should().NotContain("Frontend", "generated document paths are searchable (Frontend has none)");
        DocumentationDriftCards.Filter(cards, DriftCardState.GeneratedMissing, null).Select(c => c.Module).Should().Equal("Frontend");
        DocumentationDriftCards.Filter(cards, null, "").Should().HaveCount(4, "clearing the search restores every card");
    }

    [Fact]
    public void MissingEvidence_LinksToTheResolvingFeature_WithContext()
    {
        DocumentationDriftCards.ResolveAction(DocumentationDriftCards.From(Group("Frontend", DriftGroupState.GeneratedNotPresent, generated: false), []))
            .Should().Be(("Open Generated Documentation Health", $"{NavigationCatalog.GeneratedDocumentationHealthRoute}?import=import-1"));
        DocumentationDriftCards.ResolveAction(DocumentationDriftCards.From(Group("X", DriftGroupState.AuthoredNotPresent, authored: false), []))
            .Should().Be(("Open Data Model Explorer", "data-model-explorer"));
        DocumentationDriftCards.ResolveAction(DocumentationDriftCards.From(Group("Person", DriftGroupState.ComparisonAvailable), [])).Should().BeNull();
    }
}
