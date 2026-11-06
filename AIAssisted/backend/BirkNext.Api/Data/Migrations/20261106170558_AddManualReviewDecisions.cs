using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BirkNext.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddManualReviewDecisions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "manual_review_decisions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    module = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    scope_key = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    subject_key = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    reviewer = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    decision = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    note = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    evidence_json = table.Column<string>(type: "text", nullable: false),
                    applies_to = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_manual_review_decisions", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_manual_review_decisions_subject",
                table: "manual_review_decisions",
                columns: new[] { "module", "scope_key", "subject_key", "decided_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "manual_review_decisions");
        }
    }
}
