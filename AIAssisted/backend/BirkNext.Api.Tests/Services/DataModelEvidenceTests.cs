using BirkNext.DatabaseArchitecture;
using BirkNext.DataModelEvidence;
using BirkNext.SourceDomains;
using FluentAssertions;

namespace BirkNext.Api.Tests.Services;

/// <summary>
/// Shared Data Model Evidence over the stored database architecture: scoped identities (same table name in two stores never collides),
/// explicit vs convention mapping (a name never confirms), separate nullability layers, migrations as declarations (never applied),
/// potential migration gaps, candidate contract lineage and engine-level infrastructure correlation.
/// </summary>
public sealed class DataModelEvidenceTests
{
    private static readonly Guid Snapshot = Guid.Parse("da000000-0000-0000-0000-000000000017");

    private static DatabaseEvidence Ev(string path, DatabaseEvidenceState state, string extractor = "EF Core syntax") =>
        new(Snapshot, "fp", "Orders.Api", path, "Order", 12, 12, extractor, "Entity", "", state);

    private static TableModel Table(string id, string logical, string? physical, string? entity, DatabaseEvidenceState state, params ColumnModel[] columns) => new()
    {
        Id = id, LogicalName = logical, PhysicalName = physical, EntityTypeName = entity, SourceProject = "Orders.Api", EvidenceState = state,
        Evidence = [Ev($"src/{entity ?? logical}.cs", state)], Columns = [.. columns],
    };

    private static ColumnModel Column(string name, string? clr, bool? nullable, int? max = null, bool key = false, DatabaseEvidenceState state = DatabaseEvidenceState.Confirmed) =>
        new() { Name = name, PropertyName = name, ClrType = clr, Nullable = nullable, MaxLength = max, IsPrimaryKey = key, Evidence = [Ev("src/Order.cs", state)] };

    private static DatabaseArchitectureSnapshot Model(params ColumnModel[] extraOrderColumns) => new()
    {
        SourceSnapshotId = Snapshot,
        Databases =
        [
            new DatabaseModel { Id = "orders-db", LogicalName = "Orders", DbContext = "OrdersDbContext", Provider = "Microsoft.EntityFrameworkCore.SqlServer", SourceProject = "Orders.Api",
                Schemas = [new SchemaModel { Name = "dbo", Tables =
                [
                    Table("t-order", "Orders", "tbl_order", "Order", DatabaseEvidenceState.Confirmed, [Column("Id", "Guid", false, key: true), Column("Reference", "string", true, 40), .. extraOrderColumns]),
                    Table("t-line:entity:Line", "Lines", null, "Line", DatabaseEvidenceState.Inferred, Column("Id", "int", false, key: true)),
                ] }] },
            new DatabaseModel { Id = "billing-db", LogicalName = "Billing", DbContext = "BillingDbContext", Provider = "Npgsql", SourceProject = "Billing.Api",
                Schemas = [new SchemaModel { Name = "public", Tables = [Table("billing-t-order", "Orders", "orders", "Order", DatabaseEvidenceState.Confirmed, Column("Id", "Guid", false, key: true))] }] },
        ],
    };

    [Fact]
    public void Entities_are_scoped_and_mapping_confidence_separates_explicit_from_convention()
    {
        var entities = DataModelQueries.GetEntities(Model());
        entities.Select(e => e.Id).Should().OnlyHaveUniqueItems("the same table id in two stores never collides").And.Contain("orders-db/t-order").And.Contain("billing-db/billing-t-order");
        entities.Single(e => e.Id == "orders-db/t-order").Should().Match<DataEntity>(e => e.Mapping == DataMappingConfidence.Explicit && e.Table == "tbl_order" && e.Entity == "Order");
        entities.Single(e => e.Entity == "Line").Should().Match<DataEntity>(e => e.Mapping == DataMappingConfidence.Strong && e.MappingBasis.Contains("convention"));
        DataModelProviders.All.Single(p => p.Key == "ef-migrations").CannotProve.Should().Contain("never proves it was applied");
        DataModelProviders.All.Single(p => p.Key == "runtime-db-metadata").CannotProve.Should().Contain("no row data");
    }

    [Fact]
    public void Nullability_layers_stay_apart_and_differences_need_review()
    {
        var order = DataModelQueries.GetEntities(Model(Column("Note", "string", true), Column("Total", "decimal?", false))).Single(e => e.Id == "orders-db/t-order");
        order.Fields.Single(f => f.Column == "Total").Should().Match<DataField>(f => f.LanguageNullable == true && f.DbNullable == false && f.NullabilityDiffers);
        order.Fields.Single(f => f.Column == "Note").NullabilityDiffers.Should().BeTrue("a non-nullable CLR string on a nullable column is a difference to review");
        DataModelQueries.Constraints(order).Should().Contain("Reference: max length 40").And.Contain("Id: key").And.Contain("Id: required");
    }

    [Fact]
    public void Migrations_are_declarations_and_a_model_change_without_migration_evidence_is_a_potential_gap()
    {
        var before = Model();
        var withoutMigration = Model(Column("Status", "string", false));
        var gap = DataModelQueries.Changes(before, withoutMigration).Single(c => c.Object.Contains("Status"));
        gap.Classification.Should().Contain("Potential migration gap");
        DataModelQueries.Migrations(withoutMigration).Should().BeEmpty();

        var withMigration = Model(Column("Status", "string", false, state: DatabaseEvidenceState.ObservedFromMigration));
        withMigration.Databases[0].Schemas[0].Tables[0].Columns.Last().Evidence = [Ev("Migrations/20261022_AddStatus.cs", DatabaseEvidenceState.ObservedFromMigration, "EF migration syntax")];
        DataModelQueries.Changes(before, withMigration).Single(c => c.Object.Contains("Status")).Classification.Should().NotContain("migration gap");
        DataModelQueries.Migrations(withMigration).Should().ContainSingle().Which.State.Should().Contain("not proven applied");
        DataModelQueries.Dictionary(withMigration).Should().Contain("tbl_order").And.Contain("not proven applied").And.Contain("no data was read");
    }

    [Fact]
    public void Contract_lineage_and_infrastructure_correlation_are_candidates_never_confirmed()
    {
        var contract = new SourceContract { Id = "openapi:orders", Type = SourceContractType.OpenApi, Types = [new("OrderDto", "object", [new("reference", "string", true)])] };
        var lineage = DataModelQueries.ContractLineage([contract], DataModelQueries.GetEntities(Model()));
        lineage.Should().ContainSingle().Which.Should().Match<DataLineageLink>(l => l.Column == "Reference" && l.Confidence == DataMappingConfidence.Candidate);

        var sql = new InfrastructureResource { Id = "tf:sql", ResourceType = "azurerm_mssql_database", Category = InfrastructureCategory.Database };
        DataModelQueries.InfrastructureCorrelation(Model(), [sql]).Should().ContainSingle(c => c.StoreId == "orders-db" && c.Confidence == DataMappingConfidence.Candidate);
        DataModelQueries.InfrastructureCorrelation(Model(), [sql, sql with { Id = "tf:sql2" }]).Should().OnlyContain(c => c.Confidence == DataMappingConfidence.Ambiguous);
        DataModelQueries.TestsFor(DataModelQueries.GetEntities(Model()), ["Order_total_is_positive", "Unrelated"]).Should().Contain(t => t.Entity == "Order");
        DataModelQueries.ForRequirement(DataModelQueries.GetEntities(Model()), "Each order shall have a reference").Should().Contain(x => x.Contains("runtime database not assessed"));
    }
}
