using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BirkNext.Api.Data.Migrations;

public partial class AddActiveEventRuns : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "active_event_runs",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                EnvironmentId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                IntegrationId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                Status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                ResultJson = table.Column<string>(type: "text", nullable: false),
            },
            constraints: table => table.PrimaryKey("PK_active_event_runs", x => x.Id));

        migrationBuilder.CreateIndex(
            name: "IX_active_event_runs_EnvironmentId_IntegrationId_StartedAt",
            table: "active_event_runs",
            columns: new[] { "EnvironmentId", "IntegrationId", "StartedAt" });
    }

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropTable(name: "active_event_runs");
}
