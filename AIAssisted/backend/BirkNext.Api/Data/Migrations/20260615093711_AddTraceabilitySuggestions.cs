using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BirkNext.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTraceabilitySuggestions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "traceability_suggestions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    source_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_kind = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    target_id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_kind = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    link_type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    confidence = table.Column<double>(type: "double precision", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: false),
                    signals_json = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    confirmed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    rejected_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_traceability_suggestions", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_traceability_suggestions_pair",
                table: "traceability_suggestions",
                columns: new[] { "project_id", "source_id", "target_id", "link_type" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_traceability_suggestions_project_status",
                table: "traceability_suggestions",
                columns: new[] { "project_id", "status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "traceability_suggestions");
        }
    }
}
