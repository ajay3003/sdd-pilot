using BirkNext.Web.Components;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BirkNext.Web.Tests.Components;

public sealed class DataModelExplorerSemanticsTests : BunitContext
{
    private readonly DataModelAnalysisService _analysis = new();

    public DataModelExplorerSemanticsTests()
    {
        Services.AddSingleton<IDataModelAnalysisService>(_analysis);
        Services.AddSingleton<MarkdownRenderingService>();
    }

    [Fact]
    public void MissingFieldEvidence_IsNotRenderedAsZeroOrFinding()
    {
        var document = _analysis.Parse("""
            # Event contracts
            ## In-Transit Objects
            ### PersonRecord
            Record delivered to a downstream service.
            """);

        document.ColumnEvidence.Should().Be(DataModelEvidenceState.NotExtracted);
        document.RelationshipEvidence.Should().Be(DataModelEvidenceState.NotRepresented);
        document.IndexEvidence.Should().Be(DataModelEvidenceState.NotApplicable);
        document.Findings.Should().BeEmpty();
        document.EvidenceGaps.Should().Contain(g => g.Category == "Fields / columns");
        document.Entities.Should().ContainSingle().Which.Kind.Should().Be(DataStructureKind.Record);

        var cut = RenderDocument(document);
        cut.Find("[data-testid=dme-source-disclaimer]").TextContent.Should().Contain("not verified here");
        var zeroMetricText = cut.FindAll(".dme-stat-value").Where(v => v.TextContent.Trim() == "0")
            .Select(v => v.ParentElement!.TextContent).ToList();
        zeroMetricText.Should().NotContain(text => text.Contains("Fields / columns", StringComparison.Ordinal));
        cut.Markup.Should().Contain("Not extracted").And.Contain("Not represented").And.NotContain("No columns defined");

        ClickTab(cut, "Data Structures");
        cut.Markup.Should().Contain("Record / DTO");
        cut.Markup.Should().Contain("Fields or properties were not extracted");

        ClickTab(cut, "Findings");
        cut.Markup.Should().Contain("Evidence gaps · not model findings");
        cut.Markup.Should().NotContain("the data model looks clean");
    }

    [Fact]
    public void ExplicitEmptySections_AreDifferentFromUnextractedEvidence()
    {
        var document = _analysis.Parse("""
            # Relational model
            ## Persistent Entities
            ### Account
            Account table.
            ## Columns
            ## Relationships
            ## Indexes
            """);

        document.ColumnEvidence.Should().Be(DataModelEvidenceState.NoneDeclared);
        document.RelationshipEvidence.Should().Be(DataModelEvidenceState.NoneDeclared);
        document.IndexEvidence.Should().Be(DataModelEvidenceState.NoneDeclared);

        var cut = RenderDocument(document);
        cut.Markup.Should().Contain("None declared in artifact");
        cut.Markup.Should().Contain("0");
    }

    [Fact]
    public void ExplicitRelationalColumnEvidence_StillProducesActualSchemaFinding()
    {
        var document = _analysis.Parse("""
            # Relational model
            ## Persistent Entities

            ### Accounts — `accounts` table

            ### Columns
            | Column | Type | Nullable |
            |---|---|---|
            | DisplayName | text | Yes |
            """);

        document.ColumnEvidence.Should().Be(DataModelEvidenceState.Extracted);
        document.Findings.Should().Contain(f => f.Category == "Schema" && f.Description.Contains("primary key", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Overview_RendersMarkdownAndExportPreservesEvidenceState()
    {
        var document = _analysis.Parse("""
            # Model
            ## Overview
            1. **Stream Checkpoint** — stored in Blob Storage.
            2. **Delivery Record** — sent to the Person API.
            ## Events
            ### UserEvent
            """);
        var cut = RenderDocument(document);
        var overview = cut.Find("[data-testid=dme-structured-overview]");
        overview.InnerHtml.Should().Contain("<strong>Stream Checkpoint</strong>");
        overview.InnerHtml.Should().NotContain("**Stream Checkpoint**");

        var html = new ReportExportService().ExportDataModel(document, "Test");
        html.Should().Contain("Fields / columns: not extracted");
        html.Should().Contain("Source-derived model only");
        html.Should().Contain("<strong>Stream Checkpoint</strong>");
        html.Should().NotContain("0</div><div class=\"kpi\"><span>Columns");
    }

    private IRenderedComponent<DataModelExplorerPanel> RenderDocument(DataModelDocument document) =>
        Render<DataModelExplorerPanel>(parameters => parameters.Add(p => p.ParsedDataModel, document));

    private static void ClickTab(IRenderedComponent<DataModelExplorerPanel> cut, string label) =>
        cut.FindAll(".dme-tab").Single(button => button.TextContent.Contains(label, StringComparison.Ordinal)).Click();
}
