using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BirkNext.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class GuardSourceDecisions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Source decisions (analysis baselines, declared branch labels) are append-only history: the database itself refuses to rewrite one.
            // DELETE stays possible, as for execution manifests, because Local Data Reset removes a workspace's decisions explicitly.
            // No schema change: an existing table gains only a trigger, so existing rows and the model are untouched. Columns are snake_case here.
            if (migrationBuilder.ActiveProvider == "Npgsql.EntityFrameworkCore.PostgreSQL")
            {
                migrationBuilder.Sql("""
                    CREATE OR REPLACE FUNCTION source_decisions_append_only() RETURNS trigger LANGUAGE plpgsql AS $$
                    BEGIN
                        RAISE EXCEPTION 'source decisions are append-only (decision %)', OLD.id;
                    END;
                    $$;
                    DROP TRIGGER IF EXISTS source_decisions_no_update ON source_decisions;
                    CREATE TRIGGER source_decisions_no_update BEFORE UPDATE ON source_decisions
                        FOR EACH ROW EXECUTE FUNCTION source_decisions_append_only();
                    """);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider == "Npgsql.EntityFrameworkCore.PostgreSQL")
                migrationBuilder.Sql("DROP TRIGGER IF EXISTS source_decisions_no_update ON source_decisions; DROP FUNCTION IF EXISTS source_decisions_append_only();");
        }
    }
}
