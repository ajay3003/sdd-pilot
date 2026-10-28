using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>Documentation Drift presentation: one status vocabulary with text + tone, entity/section grouping, and the deterministic Advanced comparison compatibility.</summary>
public sealed class DocumentationDriftPresentationTests
{
    private static DriftDocument Doc(string id, string type, string? module = "cart", string lineage = "project:cart", bool content = true, string? key = null) =>
        new(id, DriftOrigin.Authored, key ?? id, id, $"{id}.md", type, "fp-" + id, "import-a", "cart · Import 1", null, null, 1, null, null, module, null, content, true, null, lineage);

    [Theory]
    [InlineData(ConsistencyCategory.Consistent, DriftStatus.Consistent, "green")]
    [InlineData(ConsistencyCategory.FieldDifference, DriftStatus.Difference, "amber")]
    [InlineData(ConsistencyCategory.GeneratedOnly, DriftStatus.GeneratedOnly, "blue")]
    [InlineData(ConsistencyCategory.AuthoredOnly, DriftStatus.AuthoredOnly, "blue")]
    [InlineData(ConsistencyCategory.NotConcludedAuthoredConflict, DriftStatus.Conflict, "red")]
    [InlineData(ConsistencyCategory.NotComparable, DriftStatus.Unmatched, "grey")]
    public void Structured_categories_map_to_the_shared_vocabulary(ConsistencyCategory category, DriftStatus status, string tone)
    {
        DocumentationDriftPresentation.Status(category).Should().Be(status);
        DocumentationDriftPresentation.Tone(status).Should().Be(tone);
    }

    [Fact]
    public void Labels_use_the_documented_words()
    {
        DocumentationDriftPresentation.Statuses.Select(DocumentationDriftPresentation.Label).Should().Equal(
            "Consistent", "Difference", "Generated-only", "Authored-only", "Conflict", "Review candidate", "Unmatched");
    }

    [Fact]
    public void Grouping_uses_entity_and_top_level_section()
    {
        DocumentationDriftPresentation.EntityOf("Order.Total").Should().Be("Order");
        DocumentationDriftPresentation.EntityOf("Order").Should().Be("Order");
        DocumentationDriftPresentation.SectionOf("Overview › Flow").Should().Be("Overview");
        DocumentationDriftPresentation.SectionOf("").Should().Be("Document");
    }

    [Fact]
    public void Compatibility_is_deterministic_and_never_assumes_scope()
    {
        DocumentationDriftPresentation.Compatibility(Doc("a", "Specification", key: "k"), Doc("b", "Specification", key: "k")).Level.Should().Be(DriftCompatibility.High);
        DocumentationDriftPresentation.Compatibility(Doc("a", "Data Model"), Doc("b", "Data Model")).Level.Should().Be(DriftCompatibility.High);
        DocumentationDriftPresentation.Compatibility(Doc("a", "Data Model"), Doc("b", "Data Model", module: "orders")).Level.Should().Be(DriftCompatibility.Partial);
        DocumentationDriftPresentation.Compatibility(Doc("a", "Data Model"), Doc("b", "Architecture")).Level.Should().Be(DriftCompatibility.Low);
        DocumentationDriftPresentation.Compatibility(Doc("a", "Data Model"), Doc("b", "Data Model", content: false)).Level.Should().Be(DriftCompatibility.Unknown);
        DocumentationDriftPresentation.NeedsConfirmation(DriftCompatibility.Low).Should().BeTrue();
        DocumentationDriftPresentation.NeedsConfirmation(DriftCompatibility.Partial).Should().BeFalse();
    }

    [Fact]
    public void Filter_and_search_cover_group_title_detail_and_sides()
    {
        DriftResultRow[] rows =
        [
            new("Order", "Order.Total", DriftStatus.Difference, "type differs", "decimal", "int", []),
            new("Order", "Order.Id", DriftStatus.Consistent, "", "Guid", "Guid", []),
            new("Cart", "Cart", DriftStatus.GeneratedOnly, "", null, "Entity", []),
        ];
        DocumentationDriftPresentation.Filter(rows, DriftStatus.Difference, null).Should().ContainSingle();
        DocumentationDriftPresentation.Filter(rows, null, "guid").Select(r => r.Title).Should().Equal("Order.Id");
        DocumentationDriftPresentation.Filter(rows, null, "cart").Should().ContainSingle();
        DocumentationDriftPresentation.Counts(rows).Should().Equal((DriftStatus.Consistent, 1), (DriftStatus.Difference, 1), (DriftStatus.GeneratedOnly, 1));
    }
}
