using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BirkNext.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddActiveCdcRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "active_cdc_runs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EnvironmentId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    IntegrationId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    SyntheticPersonPk = table.Column<int>(type: "integer", nullable: true),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    ResultJson = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_active_cdc_runs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_active_cdc_runs_EnvironmentId_IntegrationId_StartedAt",
                table: "active_cdc_runs",
                columns: new[] { "EnvironmentId", "IntegrationId", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_active_cdc_runs_EnvironmentId_SyntheticPersonPk",
                table: "active_cdc_runs",
                columns: new[] { "EnvironmentId", "SyntheticPersonPk" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "active_cdc_runs");
        }
    }
}
