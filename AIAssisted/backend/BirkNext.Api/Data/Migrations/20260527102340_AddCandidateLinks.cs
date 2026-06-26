using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BirkNext.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCandidateLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "candidate_links",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    session_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    source_candidate_ref = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    target_candidate_ref = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    link_type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_candidate_links", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_candidate_links_project_session",
                table: "candidate_links",
                columns: new[] { "project_id", "session_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "candidate_links");
        }
    }
}
