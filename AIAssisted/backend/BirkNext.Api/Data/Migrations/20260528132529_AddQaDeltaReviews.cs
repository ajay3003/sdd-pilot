using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BirkNext.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddQaDeltaReviews : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "qa_delta_reviews",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    title = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    project_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    old_spec_file_name = table.Column<string>(type: "text", nullable: true),
                    new_spec_file_name = table.Column<string>(type: "text", nullable: true),
                    old_spec_hash = table.Column<string>(type: "text", nullable: true),
                    new_spec_hash = table.Column<string>(type: "text", nullable: true),
                    old_spec_size = table.Column<int>(type: "integer", nullable: true),
                    new_spec_size = table.Column<int>(type: "integer", nullable: true),
                    analysis_profile = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    summary_json = table.Column<string>(type: "text", nullable: false),
                    delta_items_json = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_qa_delta_reviews", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_qa_delta_reviews_project_id_created_at",
                table: "qa_delta_reviews",
                columns: new[] { "project_id", "created_at" },
                descending: new[] { false, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "qa_delta_reviews");
        }
    }
}
