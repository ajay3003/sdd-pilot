using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BirkNext.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AdoTestCaseLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ado_test_case_links",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    CaseKey = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Organization = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    AdoProject = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    State = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    TestCaseId = table.Column<int>(type: "integer", nullable: true),
                    RemoteRevision = table.Column<int>(type: "integer", nullable: true),
                    CaseRevision = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    PlanRevision = table.Column<int>(type: "integer", nullable: false),
                    AdoPlanId = table.Column<int>(type: "integer", nullable: true),
                    AdoSuiteId = table.Column<int>(type: "integer", nullable: true),
                    Title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    AttemptedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ado_test_case_links", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ado_test_case_links_ProjectKey_CaseKey_Organization_AdoProj~",
                table: "ado_test_case_links",
                columns: new[] { "ProjectKey", "CaseKey", "Organization", "AdoProject" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ado_test_case_links");
        }
    }
}
