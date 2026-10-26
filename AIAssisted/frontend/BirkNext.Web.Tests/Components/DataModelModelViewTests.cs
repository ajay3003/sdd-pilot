using BirkNext.Web.Components;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// Data Model Explorer renders the normalized model, not parser structure (DM-UX-01): the same parser now also reads field tables placed
/// directly under an entity heading, section headings with qualifiers, and C#/SQL declarations in code blocks — requiredness only from proven
/// syntax, provenance per fact, a fieldless snippet never an entity, differing definitions kept as a model discrepancy. The Model view is the
/// default, uses the available width, and keeps "Lines … · FencedCodeLine" only in collapsed, lazy technical details. Generic fixtures only.
/// </summary>
public sealed class DataModelModelViewTests : BunitContext
{
    private readonly DataModelAnalysisService _parser = new();

    public DataModelModelViewTests() => Services.AddSingleton<IDataModelAnalysisService>(_parser);

    private const string TableUnderHeading = """
        # Data Model: Placements

        ## Domain Entities — Schema: `placements`

        ### Placement

        The core domain entity.

        | Field | Type | Nullable | Notes |
        |-------|------|----------|-------|
        | `Id` | `Guid` | No | domain PK |
        | `ChildId` | `Guid` | Yes | null until resolved |
        | `StartDate` | `DateOnly` | Yes | planned start |

        ### PlacementType

        | Field | Type | Nullable | Notes |
        |-------|------|----------|-------|
        | `Id` | `Guid` | No | |
        | `Name` | `string` | No | |
        """;

    private const string CodeDeclarations = """
        # Data Model: Review workflow

        ## ReviewStatus (enum)

        ```csharp
        public enum ReviewStatus
        {
            New,          // legacy
            Accepted = 2,
            Rejected,
        }
        ```

        ## TracedItem (frontend model)

        ```csharp
        public sealed class TracedItem
        {
            public Guid CandidateId { get; init; }
            public required string Title { get; init; }
            public string? Reference { get; init; }
            public IReadOnlyList<string> LinkedTests { get; init; }
        }
        ```

        ## Registry table

        ```sql
        CREATE TABLE registry (
            id UUID PRIMARY KEY,
            event_key VARCHAR(64) NOT NULL UNIQUE,
            batch_id UUID NULL REFERENCES batch(id),
            note TEXT
        );
        ```

        ## Example only

        ```sql
        CREATE TABLE example (id INT);
        ```
        """;

    [Fact]
    public void A_field_table_directly_under_an_entity_heading_is_read_and_section_qualifiers_are_ignored()
    {
        var document = _parser.Parse(TableUnderHeading);
        document.Entities.Select(e => e.Name).Should().Equal("Placement", "PlacementType");
        var placement = document.Entities[0];
        placement.Columns.Select(c => c.Name).Should().Equal("Id", "ChildId", "StartDate");
        placement.Columns.Single(c => c.Name == "ChildId").Nullable.Should().BeTrue();
        placement.Columns.Single(c => c.Name == "Id").Nullable.Should().BeFalse();
        placement.Description.Should().NotContain("|", "table rows are fields, never description text");
        placement.Columns[0].Provenance!.Sources[0].StartLine.Should().Be(11);
        placement.Columns.Single(c => c.Name == "Id").Should().Match<DataColumn>(c => c.IsPrimaryKey && !c.KeyInferred, "\"domain PK\" declares it");
        document.Entities[1].Columns.Single(c => c.Name == "Id").Should().Match<DataColumn>(c => c.IsPrimaryKey && c.KeyInferred, "only the name says so");
    }

    [Fact]
    public void Code_block_declarations_become_structures_with_only_proven_requiredness()
    {
        var document = _parser.Parse(CodeDeclarations);

        var status = document.Enums.Should().ContainSingle(e => e.Name == "ReviewStatus").Subject;
        status.Values.Should().Equal("New", "Accepted", "Rejected");

        var traced = document.Entities.Single(e => e.Name == "TracedItem");
        traced.Columns.Select(c => c.Name).Should().Equal("CandidateId", "Title", "Reference", "LinkedTests");
        traced.Columns.Single(c => c.Name == "Title").Nullable.Should().BeFalse("required keyword");
        traced.Columns.Single(c => c.Name == "Reference").Nullable.Should().BeTrue("nullable annotation");
        traced.Columns.Single(c => c.Name == "CandidateId").Nullable.Should().BeNull("not proven either way — never guessed");
        traced.Columns.Single(c => c.Name == "Title").Provenance!.Sources[0].StartLine.Should().Be(20);

        var registry = document.Entities.Single(e => e.Name == "registry");
        registry.IsTable.Should().BeTrue();
        registry.Columns.Single(c => c.Name == "id").IsPrimaryKey.Should().BeTrue();
        registry.Columns.Single(c => c.Name == "event_key").Should().Match<DataColumn>(c => c.Nullable == false && c.IsUnique);
        registry.Columns.Single(c => c.Name == "batch_id").Should().Match<DataColumn>(c => c.Nullable == true && c.IsForeignKey);
        registry.Columns.Single(c => c.Name == "note").Nullable.Should().BeNull();
        document.Relationships.Should().ContainSingle(r => r.Source == "registry.batch_id" && r.Target == "batch.id");

        document.Entities.Should().NotContain(e => e.Name == "example", "a fieldless one-line snippet is an example, never a data structure");
    }

    [Fact]
    public void A_code_definition_that_differs_from_the_markdown_definition_is_a_model_discrepancy()
    {
        var text = """
            ## Entities

            ### Order

            | Field | Type | Nullable |
            |---|---|---|
            | Id | Guid | No |

            ```csharp
            public class Order
            {
                public Guid Id { get; init; }
                public decimal Total { get; init; }
            }
            ```
            """;
        var document = _parser.Parse(text);
        document.Entities.Single(e => e.Name == "Order").Columns.Select(c => c.Name).Should().Equal(["Id"], "never silently merged");
        document.Findings.Should().ContainSingle(f => f.Category == "Model discrepancy" && f.EntityName == "Order").Which.Description.Should().Contain("review required");
    }

    [Fact]
    public void A_dash_inside_a_parenthetical_qualifier_never_leaves_a_half_open_name()
    {
        var document = _parser.Parse("## Events\n\n### OrderPlaced (outgoing — published via outbox)\n\n| Field | Type | Nullable |\n|---|---|---|\n| OrderId | Guid | No |\n");
        document.Entities.Should().ContainSingle().Which.Name.Should().Be("OrderPlaced");
    }

    private IRenderedComponent<DataModelExplorerPanel> Render(string text, string? path) =>
        Render<DataModelExplorerPanel>(p => p.Add(x => x.ParsedDataModel, _parser.Parse(text)).Add(x => x.DataModelText, text).Add(x => x.ArtifactPath, path).Add(x => x.ArtifactRevision, 2));

    /// <summary>The Data rules view in its technical field table (the tester view is the default inside it).</summary>
    private IRenderedComponent<DataModelExplorerPanel> Panel(string text, string? path = "specs/001-x/data-model.md")
    {
        var cut = Render(text, path);
        cut.Find("[data-testid=dme-tab-model]").Click();
        if (cut.FindAll("[data-testid=dme-field-view-technical]").Count > 0) cut.Find("[data-testid=dme-field-view-technical]").Click();
        return cut;
    }

    [Fact]
    public void The_model_view_shows_structures_fields_and_provenance_without_parser_terms()
    {
        Render(CodeDeclarations, "specs/001-x/data-model.md").Find("[data-testid=dme-tab-overview]").GetAttribute("aria-selected").Should().Be("true", "Overview is the tester's default");
        var cut = Panel(CodeDeclarations);
        cut.Find("[data-testid=dme-tab-model]").GetAttribute("aria-selected").Should().Be("true");
        cut.Find("[data-testid=dme-model-entities]").TextContent.Should().Be("2");
        cut.Find("[data-testid=dme-model-enums]").TextContent.Should().Be("1");
        cut.Find("[data-testid=dme-model-source]").TextContent.Should().Contain("specs/001-x/data-model.md").And.Contain("revision 2").And.Contain("Authored document");

        cut.FindAll("[data-testid=dme-model-item]").Select(i => i.GetAttribute("data-entity")).Should().Equal("registry", "TracedItem");
        cut.FindAll("[data-testid=dme-model-item]").Single(i => i.GetAttribute("data-entity") == "TracedItem").Click();
        cut.Find("[data-testid=dme-field-view-technical]").Click();
        var detail = cut.Find("[data-testid=dme-model-detail]");
        detail.QuerySelectorAll("thead th").Select(th => th.TextContent).Should().Equal("Field", "Type", "Required", "Nullable", "Cardinality", "Key", "Constraints", "Source");
        var rows = cut.FindAll("[data-testid=dme-model-field]").ToDictionary(r => r.GetAttribute("data-field")!, r => r.Children.Select(c => c.TextContent.Trim()).ToArray());
        rows["Title"][2].Should().Be("Required");
        rows["Reference"][2].Should().Be("Optional");
        rows["CandidateId"][2].Should().Be("Unknown");
        rows["CandidateId"][5].Should().Be("Identifier (inferred from name)");
        rows["LinkedTests"][4].Should().Be("Many");
        rows["Title"][7].Should().Be("Line 20");
        cut.Find("[data-testid=dme-model-relationships]").TextContent.Should().Contain("None declared");

        var primary = cut.Find("[data-testid=dme-model]").TextContent + cut.Find("[data-testid=dme-model-summary]").TextContent;
        primary.Should().NotContainAny("FencedCodeLine", "FencedCodeStart", "Heading ·", "Lines 1-1");
        cut.FindAll("article[data-source-block-id]").Should().BeEmpty("parser structure is technical detail, rendered only on request");
    }

    [Fact]
    public void Relationships_and_keys_come_only_from_evidence()
    {
        var cut = Panel(CodeDeclarations);
        var rows = cut.FindAll("[data-testid=dme-model-field]").ToDictionary(r => r.GetAttribute("data-field")!, r => r.Children.Select(c => c.TextContent.Trim()).ToArray());
        rows["id"][5].Should().Be("Primary key");
        rows["batch_id"][5].Should().Be("Foreign key");
        rows["event_key"][6].Should().Be("Unique");
        cut.Find("[data-testid=dme-model-relationships]").TextContent.Should().Contain("registry.batch_id → batch.id");
    }

    [Fact]
    public void Search_finds_fields_and_enum_values_and_generated_origin_is_never_canonical()
    {
        var cut = Panel(CodeDeclarations, path: "Cart/autodoc/data-model.md");
        cut.Find("[data-testid=dme-model-source]").TextContent.Should().Contain("Generated documentation — derived, not authoritative implementation evidence");
        cut.Find("[data-testid=dme-model-search]").Input("event_key");
        cut.FindAll("[data-testid=dme-model-item]").Select(i => i.GetAttribute("data-entity")).Should().Equal("registry");
        cut.Find("[data-testid=dme-model-search]").Input("Accepted");
        cut.FindAll("[data-testid=dme-model-enum]").Should().ContainSingle();
        cut.Find("[data-testid=dme-model-no-match]");
    }

    [Fact]
    public void Without_a_derivable_model_the_view_says_so_and_offers_technical_details()
    {
        var cut = Panel("# Notes\n\nSome prose about the project.\n\n```\nnot a model\n```\n");
        cut.Find("[data-testid=dme-model-empty]").TextContent.Should().Contain("No normalized data model could be derived from the selected evidence.");
        cut.Find("[data-testid=dme-model-partial]").TextContent.Should().Contain("Some document content is not represented in the structured view.")
            .And.Contain("BirkNext does not infer missing model facts from unmapped content");
        cut.Find("[data-testid=markdown-source-notes-toggle]").Click();
        cut.FindAll("article[data-source-block-id]").Should().NotBeEmpty();
    }
}
