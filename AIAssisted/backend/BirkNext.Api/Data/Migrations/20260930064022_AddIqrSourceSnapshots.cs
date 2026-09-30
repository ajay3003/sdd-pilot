using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BirkNext.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddIqrSourceSnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "iqr_source_snapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EnvironmentId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    IntegrationId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    AnalyzedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EvidenceJson = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_iqr_source_snapshots", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_iqr_source_snapshots_EnvironmentId_IntegrationId_AnalyzedAt",
                table: "iqr_source_snapshots",
                columns: new[] { "EnvironmentId", "IntegrationId", "AnalyzedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "iqr_source_snapshots");
        }
    }
}
