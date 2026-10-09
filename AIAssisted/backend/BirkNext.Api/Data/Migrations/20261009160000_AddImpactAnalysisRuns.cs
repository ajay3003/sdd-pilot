using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BirkNext.Api.Data.Migrations;

public sealed partial class AddImpactAnalysisRuns : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "impact_analysis_runs",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                project_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                project_import_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                project_display_name = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                baseline_snapshot_id = table.Column<Guid>(type: "uuid", nullable: false),
                current_snapshot_id = table.Column<Guid>(type: "uuid", nullable: false),
                baseline_fingerprint = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                current_fingerprint = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                result_json = table.Column<string>(type: "text", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_impact_analysis_runs", x => x.id));

        migrationBuilder.CreateIndex(
            name: "IX_impact_analysis_runs_project_id_project_import_id_created_at",
            table: "impact_analysis_runs",
            columns: new[] { "project_id", "project_import_id", "created_at" });
    }

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropTable(name: "impact_analysis_runs");
}
