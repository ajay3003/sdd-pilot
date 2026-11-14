using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BirkNext.Api.Data.TaskAlignment.Migrations
{
    /// <inheritdoc />
    public partial class InitialTaskAlignmentRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "task_alignment");

            migrationBuilder.CreateTable(
                name: "run",
                schema: "task_alignment",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_key = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    project_name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    module = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    feature_key = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    tasks_path = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    tasks_hash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    spec_path = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    spec_hash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    source_snapshot_id = table.Column<Guid>(type: "uuid", nullable: true),
                    rules_version = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    design_inputs_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    record_version = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    completeness = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    analyzed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    total_tasks = table.Column<int>(type: "integer", nullable: false),
                    finding_count = table.Column<int>(type: "integer", nullable: false),
                    counts_json = table.Column<string>(type: "jsonb", nullable: false),
                    tasks_json = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_run", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_run_project_tasks_analyzed",
                schema: "task_alignment",
                table: "run",
                columns: new[] { "project_key", "tasks_path", "analyzed_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "run",
                schema: "task_alignment");
        }
    }
}
