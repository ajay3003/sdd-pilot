using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BirkNext.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddManualTestRunsAndDiscoveryResults : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "manual_test_runs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    DraftId = table.Column<Guid>(type: "uuid", nullable: false),
                    CaseId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    State = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Outcome = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    ContentJson = table.Column<string>(type: "text", nullable: false),
                    Tester = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_manual_test_runs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "scenario_discovery_results",
                columns: table => new
                {
                    DiscoveryId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ProjectKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    SnapshotKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RuleVersion = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ContentJson = table.Column<string>(type: "text", nullable: false),
                    DiscoveredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_scenario_discovery_results", x => x.DiscoveryId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_manual_test_runs_DraftId_CaseId",
                table: "manual_test_runs",
                columns: new[] { "DraftId", "CaseId" });

            migrationBuilder.CreateIndex(
                name: "IX_manual_test_runs_ProjectKey",
                table: "manual_test_runs",
                column: "ProjectKey");

            migrationBuilder.CreateIndex(
                name: "IX_scenario_discovery_results_ProjectKey_DiscoveredAt",
                table: "scenario_discovery_results",
                columns: new[] { "ProjectKey", "DiscoveredAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "manual_test_runs");

            migrationBuilder.DropTable(
                name: "scenario_discovery_results");
        }
    }
}
