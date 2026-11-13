using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// Generated EF Core data-model documentation ("## Context: XDbContext (schema s)" with "### table (ClrType)" sections and a
/// "| Column | Type | Nullable | Constraints |" table each) is read by the same parser: the context section is an explicit persistent
/// section, the table name is the identity, declared keys/FKs/nullability come from the table, and a relationship-table row is read only
/// when From and To each name one structure. Synthetic fixture shaped like the real generated documents — no product data.
/// </summary>
public sealed class DataModelEfCoreContextTests
{
    private const string Generated = """
        # Data Model

        Source of truth: the EF Core model snapshots (SQL Server).

        ## Context: OrdersDbContext (schema orders)

        ### order_header (OrderHeader)
        One order per customer and day (FR-001).

        | Column | Type | Nullable | Constraints |
        |---|---|---|---|
        | order_id | uniqueidentifier | No | PK |
        | customer_ref | uniqueidentifier | No | UQ (customer_ref, order_date) |
        | order_date | date | No | in UQ |
        | message_id | uniqueidentifier | No | UQ |
        | note | nvarchar(200) | Yes | |

        ### order_line (OrderLine, owned by OrderVersion)

        | Column | Type | Nullable | Constraints |
        |---|---|---|---|
        | order_id | uniqueidentifier | No | PK 1; FK → order_header (Restrict) |
        | line_no | int | No | PK 2 |

        ### processed_event (ProcessedEventRow)

        | Column | Type | Nullable | Constraints |
        |---|---|---|---|
        | event_id | uniqueidentifier | No | PK |

        ## Context: BillingDbContext (schema billing)

        ### invoice_version / credit_version (BillingVersion)

        | Column | Type | Nullable | Constraints |
        |---|---|---|---|
        | record_id | uniqueidentifier | No | PK 1 |

        ### processed_event (ProcessedEventRow)
        Same shape as `orders.processed_event`.

        ## Relationships

        | From | Cardinality | To | Foreign Key |
        |---|---|---|---|
        | order_line | N:1 (owned) | order_header | order_id |
        | order_header | 1:1 (logical) | order_line | order_id |
        | order_header, order_line | N:1 (logical) | processed_event | event_id |
        | every `customer_ref` | N:1 (cross-module) | Customer registry | no FK |
        """;

    private readonly DataModelDocument _doc = new DataModelAnalysisService().Parse(Generated);

    [Fact]
    public void Each_table_section_of_an_explicit_db_context_is_a_normalized_table()
    {
        _doc.Entities.Select(e => e.Name).Should().Equal("order_header", "order_line", "processed_event", "BillingVersion", "processed_event (billing)");
        _doc.Entities.Should().OnlyContain(e => e.IsTable && e.Kind == DataStructureKind.Table);
        var header = _doc.Entities[0];
        header.Description.Should().StartWith("EF Core entity OrderHeader in OrdersDbContext (schema orders).").And.Contain("One order per customer");
        _doc.Entities[1].Description.Should().Contain("owned by OrderVersion");
        _doc.Entities[3].Description.Should().Contain("tables: invoice_version, credit_version", "one CLR type over two tables keeps the type name, never a guessed table");
        _doc.Entities[4].Columns.Should().BeEmpty("'same shape as' is prose — no columns are copied from the other context");
        header.Provenance!.Sources[0].StartLine.Should().Be(7);
    }

    [Fact]
    public void Declared_types_nullability_keys_and_unique_markers_come_from_the_table_and_composite_parts_are_not_column_unique()
    {
        var header = _doc.Entities[0].Columns.ToDictionary(c => c.Name);
        header["order_id"].Should().Match<DataColumn>(c => c.IsPrimaryKey && !c.KeyInferred && c.Type == "uniqueidentifier" && c.Nullable == false);
        header["note"].Nullable.Should().BeTrue();
        header["message_id"].IsUnique.Should().BeTrue("a standalone UQ declares a unique column");
        header["customer_ref"].IsUnique.Should().BeFalse("UQ (a, b) is a composite key");
        header["order_date"].IsUnique.Should().BeFalse("'in UQ' is part of a composite key");

        var line = _doc.Entities[1].Columns.ToDictionary(c => c.Name);
        line["order_id"].Should().Match<DataColumn>(c => c.IsPrimaryKey && c.IsForeignKey && !c.KeyInferred);
        line["line_no"].IsPrimaryKey.Should().BeTrue();
    }

    [Fact]
    public void Relationship_table_rows_are_read_only_when_from_and_to_name_one_structure_and_logical_references_are_not_foreign_keys()
    {
        _doc.Relationships.Select(r => (r.Source, r.Target, r.RelationshipType)).Should().BeEquivalentTo(new[]
        {
            ("order_line.order_id", "order_header", "FK"),            // from the column's "FK → order_header"; the table row repeats it (not duplicated)
            ("order_header.order_id", "order_line", "1:1 (logical)"),
        });
        // Lists and prose rows are not turned into relationships.
        _doc.Relationships.Should().NotContain(r => r.Target == "processed_event" || r.Target.Contains("registry", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("## Context")]
    [InlineData("## Context: shared conventions")]
    public void A_context_heading_without_a_named_db_context_does_not_open_a_table_section(string heading)
    {
        var doc = new DataModelAnalysisService().Parse($"{heading}\n\n### order_header (OrderHeader)\n\n| Column | Type | Nullable | Constraints |\n|---|---|---|---|\n| order_id | uniqueidentifier | No | PK |\n");
        doc.Entities.Should().BeEmpty("only an explicitly named EF Core context declares tables");
    }

    [Fact]
    public void Inline_IX_and_composite_UQ_declarations_become_indexes_and_unique_keys_once()
    {
        const string md = """
            ## Context: ReportsDbContext (schema reports)

            ### report (Report)

            | Column | Type | Nullable | Constraints |
            |---|---|---|---|
            | report_id | uniqueidentifier | No | PK |
            | child_id | uniqueidentifier | No | UQ (child_id, period) |
            | period | char(7) | No | in UQ; IX (period, status) |
            | status | tinyint | No | in IX (period, status) |
            | chain_id | uniqueidentifier | No | IX |
            | reporter | varchar(11) | No | in UQ (EXC-03) |
            """;
        var doc = new DataModelAnalysisService().Parse(md);

        doc.Indexes.Select(i => (i.EntityName, string.Join(",", i.Columns), i.Name)).Should().BeEquivalentTo(new[]
        {
            ("report", "period,status", "IX (period, status)"),
            ("report", "chain_id", "IX (chain_id)"),
        });
        doc.Constraints.Should().ContainSingle(c => c.ConstraintType == "UNIQUE").Which.Name.Should().Be("UQ (child_id, period)");
        doc.Constraints.Should().NotContain(c => c.Name.Contains("EXC-03"), "a note in parentheses is not a key");
        doc.Entities.Single().Columns.Single(c => c.Name == "child_id").IsUnique.Should().BeFalse("a composite key does not make one column unique");
    }
}
