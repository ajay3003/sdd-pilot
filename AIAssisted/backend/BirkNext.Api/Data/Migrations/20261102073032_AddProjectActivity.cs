using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BirkNext.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectActivity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "project_activity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_context_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    project_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    activity_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    source_module = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    summary = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    outcome = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    related_entity_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    related_entity_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    package_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    package_name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    environment_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    environment_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    route = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    idempotency_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_project_activity", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_project_activity_project_context_id_idempotency_key",
                table: "project_activity",
                columns: new[] { "project_context_id", "idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_project_activity_project_context_id_occurred_at",
                table: "project_activity",
                columns: new[] { "project_context_id", "occurred_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "project_activity");
        }
    }
}
