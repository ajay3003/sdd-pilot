using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BirkNext.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddReviewedCandidates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "reviewed_candidates",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    title = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    classification = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    review_status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    source_document = table.Column<string>(type: "text", nullable: true),
                    source_section = table.Column<string>(type: "text", nullable: true),
                    project_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    session_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    reviewed_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    reviewed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_reviewed_candidates", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_reviewed_candidates_project_session",
                table: "reviewed_candidates",
                columns: new[] { "project_id", "session_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "reviewed_candidates");
        }
    }
}
