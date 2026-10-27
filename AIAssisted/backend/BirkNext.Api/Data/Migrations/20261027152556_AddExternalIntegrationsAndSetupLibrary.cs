using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BirkNext.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddExternalIntegrationsAndSetupLibrary : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "external_integrations",
                columns: table => new
                {
                    id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    environment_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    document_json = table.Column<string>(type: "text", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_external_integrations", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "setup_library_items",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    document_json = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_setup_library_items", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "setup_library_versions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version_number = table.Column<int>(type: "integer", nullable: false),
                    schema_version = table.Column<int>(type: "integer", nullable: false),
                    payload = table.Column<string>(type: "text", nullable: false),
                    manifest_json = table.Column<string>(type: "text", nullable: false),
                    change_summary = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_setup_library_versions", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_external_integrations_environment_id",
                table: "external_integrations",
                column: "environment_id");

            migrationBuilder.CreateIndex(
                name: "IX_setup_library_versions_item_id_version_number",
                table: "setup_library_versions",
                columns: new[] { "item_id", "version_number" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "external_integrations");

            migrationBuilder.DropTable(
                name: "setup_library_items");

            migrationBuilder.DropTable(
                name: "setup_library_versions");
        }
    }
}
