using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BirkNext.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTestPlanSuggestions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "test_plan_drafts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GuideKey = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    PlanName = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    DeliveryLabel = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    ContentJson = table.Column<string>(type: "text", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_test_plan_drafts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "test_plan_snapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DraftId = table.Column<Guid>(type: "uuid", nullable: false),
                    GuideKey = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    PlanName = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    DeliveryLabel = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    DraftRevision = table.Column<int>(type: "integer", nullable: false),
                    GuideRevision = table.Column<int>(type: "integer", nullable: false),
                    RuleVersion = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CaseCount = table.Column<int>(type: "integer", nullable: false),
                    ContentFingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ContentJson = table.Column<string>(type: "text", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_test_plan_snapshots", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_test_plan_drafts_GuideKey",
                table: "test_plan_drafts",
                column: "GuideKey");

            migrationBuilder.CreateIndex(
                name: "IX_test_plan_snapshots_DraftId_ContentFingerprint",
                table: "test_plan_snapshots",
                columns: new[] { "DraftId", "ContentFingerprint" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_test_plan_snapshots_DraftId_Sequence",
                table: "test_plan_snapshots",
                columns: new[] { "DraftId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_test_plan_snapshots_GuideKey",
                table: "test_plan_snapshots",
                column: "GuideKey");

            // A saved Test Plan Suggestion snapshot is immutable: the database refuses every UPDATE (content, fingerprint, identity, author).
            // A later change is a new snapshot. DELETE stays possible (Local Data Reset deletes every model table).
            if (migrationBuilder.ActiveProvider == "Npgsql.EntityFrameworkCore.PostgreSQL")
            {
                migrationBuilder.Sql("""
                    CREATE OR REPLACE FUNCTION test_plan_snapshots_immutable() RETURNS trigger LANGUAGE plpgsql AS $$
                    BEGIN
                        RAISE EXCEPTION 'test plan snapshots are immutable (snapshot %)', OLD."Id";
                    END;
                    $$;
                    CREATE TRIGGER test_plan_snapshots_no_update BEFORE UPDATE ON test_plan_snapshots
                        FOR EACH ROW EXECUTE FUNCTION test_plan_snapshots_immutable();
                    """);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider == "Npgsql.EntityFrameworkCore.PostgreSQL")
                migrationBuilder.Sql("DROP TRIGGER IF EXISTS test_plan_snapshots_no_update ON test_plan_snapshots; DROP FUNCTION IF EXISTS test_plan_snapshots_immutable();");
            migrationBuilder.DropTable(
                name: "test_plan_drafts");

            migrationBuilder.DropTable(
                name: "test_plan_snapshots");
        }
    }
}
