using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BirkNext.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkspaceDocumentBodies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "workspace_document_bodies",
                columns: table => new
                {
                    sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    content = table.Column<string>(type: "text", nullable: false),
                    length = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workspace_document_bodies", x => x.sha256);
                });

            // A body is addressed by the SHA-256 of its content: it can never change. The database refuses updates; DELETE stays possible
            // (Local Data Reset deletes every table).
            if (migrationBuilder.ActiveProvider == "Npgsql.EntityFrameworkCore.PostgreSQL")
            {
                migrationBuilder.Sql("""
                    CREATE OR REPLACE FUNCTION workspace_document_bodies_immutable() RETURNS trigger LANGUAGE plpgsql AS $$
                    BEGIN
                        RAISE EXCEPTION 'workspace document bodies are immutable (body %)', OLD.sha256;
                    END;
                    $$;
                    DROP TRIGGER IF EXISTS workspace_document_bodies_no_update ON workspace_document_bodies;
                    CREATE TRIGGER workspace_document_bodies_no_update BEFORE UPDATE ON workspace_document_bodies
                        FOR EACH ROW EXECUTE FUNCTION workspace_document_bodies_immutable();
                    """);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider == "Npgsql.EntityFrameworkCore.PostgreSQL")
                migrationBuilder.Sql("DROP TRIGGER IF EXISTS workspace_document_bodies_no_update ON workspace_document_bodies; DROP FUNCTION IF EXISTS workspace_document_bodies_immutable();");
            migrationBuilder.DropTable(
                name: "workspace_document_bodies");
        }
    }
}
