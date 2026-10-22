using BirkNext.DatabaseArchitecture;
using BirkNext.Web.Components;
using Bunit;
using FluentAssertions;

namespace BirkNext.Web.Tests.Components;

/// <summary>Shared data model evidence in Source Analysis Data and Data Model Explorer: explicit vs convention mapping counts, migrations
/// as declarations, documented entities compared with the source model, and a data dictionary without raw source.</summary>
public sealed class DataModelEvidenceUiTests : BunitContext
{
    private static DatabaseArchitectureSnapshot Model() => new()
    {
        SourceSnapshotId = Guid.NewGuid(),
        Databases =
        [
            new DatabaseModel { Id = "db", LogicalName = "Orders", DbContext = "OrdersDbContext", SourceProject = "Orders.Api", Schemas = [new SchemaModel { Name = "dbo", Tables =
            [
                new TableModel { Id = "t1", LogicalName = "Orders", PhysicalName = "tbl_order", EntityTypeName = "Order", EvidenceState = DatabaseEvidenceState.Confirmed,
                    Columns = [new ColumnModel { Name = "Id", ClrType = "Guid", Nullable = false, IsPrimaryKey = true }] },
                new TableModel { Id = "t2", LogicalName = "Lines", EntityTypeName = "Line", EvidenceState = DatabaseEvidenceState.Inferred,
                    Columns = [new ColumnModel { Name = "Id", ClrType = "int", Nullable = false, IsPrimaryKey = true,
                        Evidence = [new(Guid.Empty, "fp", "Orders.Api", "Migrations/20261001_Init.cs", "Init", 1, 1, "EF migration syntax", "Column", "", DatabaseEvidenceState.ObservedFromMigration)] }] },
            ] }] },
        ],
    };

    [Fact]
    public void Panel_shows_mapping_confidence_declared_migrations_and_documented_entities()
    {
        var cut = Render<DataModelEvidencePanel>(p => p.Add(x => x.Snapshot, Model()).Add(x => x.DocumentedEntities, ["Order", "Invoice"]));
        (cut.Find("[data-testid=data-model-evidence-explicit]").TextContent, cut.Find("[data-testid=data-model-evidence-convention]").TextContent,
            cut.Find("[data-testid=data-model-evidence-migrations]").TextContent).Should().Be(("1", "1", "1"));
        cut.Find("[data-testid=data-model-evidence-documented]").TextContent.Should().Contain("1 of 2 documented entities found in source").And.Contain("not found: Invoice");
        cut.Markup.Should().Contain("not proof they were applied").And.Contain("no live database or row data");
        cut.Find("[data-testid=data-model-evidence-dictionary-text]").TextContent.Should().Contain("tbl_order").And.Contain("Explicit");
    }
}
