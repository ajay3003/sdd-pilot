using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BirkNext.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddIntegrationCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "integration_definitions",
                columns: table => new
                {
                    environment_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    id = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    platform_id = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    display_name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    document_json = table.Column<string>(type: "text", nullable: false),
                    user_modified = table.Column<bool>(type: "boolean", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_integration_definitions", x => new { x.environment_id, x.id });
                });

            migrationBuilder.CreateTable(
                name: "integration_environment_states",
                columns: table => new
                {
                    environment_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    seed_version = table.Column<int>(type: "integer", nullable: false),
                    seed_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    legacy_imported_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_integration_environment_states", x => x.environment_id);
                });

            migrationBuilder.CreateTable(
                name: "integration_platforms",
                columns: table => new
                {
                    environment_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    id = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    document_json = table.Column<string>(type: "text", nullable: false),
                    user_modified = table.Column<bool>(type: "boolean", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_integration_platforms", x => new { x.environment_id, x.id });
                });

            migrationBuilder.CreateTable(
                name: "integration_review_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    environment_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    outcome = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    result_json = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_integration_review_runs", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_integration_definitions_environment_platform",
                table: "integration_definitions",
                columns: new[] { "environment_id", "platform_id" });

            migrationBuilder.CreateIndex(
                name: "ix_integration_review_runs_environment_completed",
                table: "integration_review_runs",
                columns: new[] { "environment_id", "completed_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "integration_definitions");

            migrationBuilder.DropTable(
                name: "integration_environment_states");

            migrationBuilder.DropTable(
                name: "integration_platforms");

            migrationBuilder.DropTable(
                name: "integration_review_runs");
        }
    }
}
