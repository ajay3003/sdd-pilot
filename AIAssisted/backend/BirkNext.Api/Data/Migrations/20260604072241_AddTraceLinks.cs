using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BirkNext.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTraceLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "trace_links",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    source_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_kind = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    target_id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_kind = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    link_type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    notes = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_trace_links", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_trace_links_project_source",
                table: "trace_links",
                columns: new[] { "project_id", "source_kind", "source_id" });

            migrationBuilder.CreateIndex(
                name: "ix_trace_links_project_target",
                table: "trace_links",
                columns: new[] { "project_id", "target_kind", "target_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "trace_links");
        }
    }
}
