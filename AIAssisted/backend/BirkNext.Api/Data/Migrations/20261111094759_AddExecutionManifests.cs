using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BirkNext.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddExecutionManifests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "execution_manifests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Owner = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    SchemaVersion = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    DependencyFingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DocumentJson = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_execution_manifests", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_execution_manifests_DependencyFingerprint",
                table: "execution_manifests",
                column: "DependencyFingerprint");

            migrationBuilder.CreateIndex(
                name: "IX_execution_manifests_Owner_RunId",
                table: "execution_manifests",
                columns: new[] { "Owner", "RunId" },
                unique: true);

            // Manifests are immutable: an UPDATE is refused by the database itself. DELETE stays possible, because a manifest is removed together
            // with its run (evidence deletion, retention, Local Data Reset).
            if (migrationBuilder.ActiveProvider == "Npgsql.EntityFrameworkCore.PostgreSQL")
            {
                migrationBuilder.Sql("""
                    CREATE OR REPLACE FUNCTION execution_manifests_immutable() RETURNS trigger LANGUAGE plpgsql AS $$
                    BEGIN
                        RAISE EXCEPTION 'execution manifests are immutable (run %)', OLD."RunId";
                    END;
                    $$;
                    CREATE TRIGGER execution_manifests_no_update BEFORE UPDATE ON execution_manifests
                        FOR EACH ROW EXECUTE FUNCTION execution_manifests_immutable();
                    """);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider == "Npgsql.EntityFrameworkCore.PostgreSQL")
                migrationBuilder.Sql("DROP TRIGGER IF EXISTS execution_manifests_no_update ON execution_manifests; DROP FUNCTION IF EXISTS execution_manifests_immutable();");
            migrationBuilder.DropTable(
                name: "execution_manifests");
        }
    }
}
