using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BirkNext.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddImplementationGapEvaluations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "implementation_gap_evaluations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ScopeKey = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    ProjectImportId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    SnapshotId = table.Column<Guid>(type: "uuid", nullable: true),
                    EvaluatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    InputsJson = table.Column<string>(type: "text", nullable: false),
                    ReportJson = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_implementation_gap_evaluations", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_implementation_gap_evaluations_ScopeKey_EvaluatedAt",
                table: "implementation_gap_evaluations",
                columns: new[] { "ScopeKey", "EvaluatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "implementation_gap_evaluations");
        }
    }
}
