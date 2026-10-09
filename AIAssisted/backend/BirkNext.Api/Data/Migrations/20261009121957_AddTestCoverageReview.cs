using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BirkNext.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTestCoverageReview : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "test_coverage_decisions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    repository_key = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    subject_key = table.Column<string>(type: "character varying(600)", maxLength: 600, nullable: false),
                    value = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_test_coverage_decisions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "test_coverage_review_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    current_snapshot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    baseline_snapshot_id = table.Column<Guid>(type: "uuid", nullable: true),
                    label = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    result_json = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_test_coverage_review_runs", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_test_coverage_decisions_subject",
                table: "test_coverage_decisions",
                columns: new[] { "repository_key", "kind", "subject_key" });

            migrationBuilder.CreateIndex(
                name: "ix_test_coverage_review_runs_completed",
                table: "test_coverage_review_runs",
                column: "completed_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "test_coverage_decisions");

            migrationBuilder.DropTable(
                name: "test_coverage_review_runs");
        }
    }
}
