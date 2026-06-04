using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BirkNext.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCodeTraceability : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "code_files",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    file_path = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    file_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_code_files", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "code_links",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    code_file_id = table.Column<Guid>(type: "uuid", nullable: false),
                    scenario_id = table.Column<Guid>(type: "uuid", nullable: false),
                    scenario_kind = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_code_links", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_code_files_project_created",
                table: "code_files",
                columns: new[] { "project_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_code_files_project_path",
                table: "code_files",
                columns: new[] { "project_id", "file_path" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_code_links_file_scenario",
                table: "code_links",
                columns: new[] { "code_file_id", "scenario_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_code_links_project_file",
                table: "code_links",
                columns: new[] { "project_id", "code_file_id" });

            migrationBuilder.CreateIndex(
                name: "ix_code_links_project_scenario",
                table: "code_links",
                columns: new[] { "project_id", "scenario_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "code_files");

            migrationBuilder.DropTable(
                name: "code_links");
        }
    }
}
