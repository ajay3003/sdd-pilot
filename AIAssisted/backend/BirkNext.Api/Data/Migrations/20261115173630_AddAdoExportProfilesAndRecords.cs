using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BirkNext.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAdoExportProfilesAndRecords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ado_export_profiles",
                columns: table => new
                {
                    ProjectKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ProfileJson = table.Column<string>(type: "text", nullable: false),
                    UpdatedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ado_export_profiles", x => x.ProjectKey);
                });

            migrationBuilder.CreateTable(
                name: "ado_export_records",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    DraftId = table.Column<Guid>(type: "uuid", nullable: false),
                    PlanName = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    PlanRevision = table.Column<int>(type: "integer", nullable: false),
                    Format = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Mode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Cases = table.Column<int>(type: "integer", nullable: false),
                    Rows = table.Column<int>(type: "integer", nullable: false),
                    Destination = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    ContentJson = table.Column<string>(type: "text", nullable: false),
                    ExportedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ExportedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ado_export_records", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ado_export_records_ProjectKey_ExportedAt",
                table: "ado_export_records",
                columns: new[] { "ProjectKey", "ExportedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ado_export_profiles");

            migrationBuilder.DropTable(
                name: "ado_export_records");
        }
    }
}
