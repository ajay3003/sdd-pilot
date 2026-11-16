using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BirkNext.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class TechnicalRunHistoryAndFindings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "api_review_runs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    EnvironmentId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    EnvironmentName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    GeneratedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Targets = table.Column<int>(type: "integer", nullable: false),
                    Completed = table.Column<int>(type: "integer", nullable: false),
                    Blocked = table.Column<int>(type: "integer", nullable: false),
                    High = table.Column<int>(type: "integer", nullable: false),
                    Medium = table.Column<int>(type: "integer", nullable: false),
                    Low = table.Column<int>(type: "integer", nullable: false),
                    Info = table.Column<int>(type: "integer", nullable: false),
                    ReportJson = table.Column<string>(type: "jsonb", nullable: false),
                    RecordedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_api_review_runs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "technical_findings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Source = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    FindingKey = table.Column<string>(type: "character varying(600)", maxLength: 600, nullable: false),
                    Status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    ContentJson = table.Column<string>(type: "jsonb", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_technical_findings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_api_review_runs_ProjectKey_GeneratedAt",
                table: "api_review_runs",
                columns: new[] { "ProjectKey", "GeneratedAt" });

            migrationBuilder.CreateIndex(
                name: "ux_api_review_runs_environment_generated",
                table: "api_review_runs",
                columns: new[] { "EnvironmentId", "GeneratedAt" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_technical_findings_identity",
                table: "technical_findings",
                columns: new[] { "ProjectKey", "Source", "FindingKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "api_review_runs");

            migrationBuilder.DropTable(
                name: "technical_findings");
        }
    }
}
