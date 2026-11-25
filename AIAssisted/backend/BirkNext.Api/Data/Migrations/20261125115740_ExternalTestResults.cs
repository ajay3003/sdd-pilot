using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BirkNext.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class ExternalTestResults : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "external_test_results",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Source = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ProjectKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    DraftId = table.Column<Guid>(type: "uuid", nullable: false),
                    CaseKey = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Organization = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    AdoProject = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    TestCaseId = table.Column<int>(type: "integer", nullable: false),
                    PlanId = table.Column<int>(type: "integer", nullable: false),
                    SuiteId = table.Column<int>(type: "integer", nullable: false),
                    PointId = table.Column<int>(type: "integer", nullable: false),
                    RunId = table.Column<int>(type: "integer", nullable: false),
                    ResultId = table.Column<int>(type: "integer", nullable: false),
                    Outcome = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Tester = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Configuration = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ExportedRevision = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    FetchedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    FetchedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_external_test_results", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_external_test_results_ProjectKey_DraftId",
                table: "external_test_results",
                columns: new[] { "ProjectKey", "DraftId" });

            migrationBuilder.CreateIndex(
                name: "IX_external_test_results_Source_Organization_AdoProject_RunId_~",
                table: "external_test_results",
                columns: new[] { "Source", "Organization", "AdoProject", "RunId", "ResultId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "external_test_results");
        }
    }
}
