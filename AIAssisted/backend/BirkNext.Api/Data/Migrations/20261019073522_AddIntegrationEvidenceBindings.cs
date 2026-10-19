using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BirkNext.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddIntegrationEvidenceBindings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "integration_evidence_bindings",
                columns: table => new
                {
                    environment_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    integration_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    snapshot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_json = table.Column<string>(type: "text", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_integration_evidence_bindings", x => new { x.environment_id, x.integration_id });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "integration_evidence_bindings");
        }
    }
}
