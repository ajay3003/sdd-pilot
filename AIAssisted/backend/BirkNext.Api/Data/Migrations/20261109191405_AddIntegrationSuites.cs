using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BirkNext.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddIntegrationSuites : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "integration_suite_runs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SuiteKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    SuiteId = table.Column<Guid>(type: "uuid", nullable: false),
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Initiator = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    TestIdsJson = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_integration_suite_runs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "integration_suites",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SuiteKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    SnapshotId = table.Column<Guid>(type: "uuid", nullable: false),
                    ScopeId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    EnvironmentId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    GeneratedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    DefinitionJson = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_integration_suites", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_integration_suite_runs_SuiteKey_StartedAt",
                table: "integration_suite_runs",
                columns: new[] { "SuiteKey", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_integration_suites_SuiteKey_Revision",
                table: "integration_suites",
                columns: new[] { "SuiteKey", "Revision" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "integration_suite_runs");

            migrationBuilder.DropTable(
                name: "integration_suites");
        }
    }
}
