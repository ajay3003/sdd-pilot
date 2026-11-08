using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BirkNext.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddExecutionEvidenceRetention : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Protected",
                table: "execution_runs",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ProtectedAt",
                table: "execution_runs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "execution_evidence_audit",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Action = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Scope = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Runs = table.Column<int>(type: "integer", nullable: false),
                    Calls = table.Column<int>(type: "integer", nullable: false),
                    Bytes = table.Column<long>(type: "bigint", nullable: false),
                    Initiator = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Detail = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_execution_evidence_audit", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "execution_retention_settings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    Mode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Days = table.Column<int>(type: "integer", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_execution_retention_settings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_execution_runs_StartedAt",
                table: "execution_runs",
                column: "StartedAt");

            migrationBuilder.CreateIndex(
                name: "IX_execution_evidence_audit_At",
                table: "execution_evidence_audit",
                column: "At");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "execution_evidence_audit");

            migrationBuilder.DropTable(
                name: "execution_retention_settings");

            migrationBuilder.DropIndex(
                name: "IX_execution_runs_StartedAt",
                table: "execution_runs");

            migrationBuilder.DropColumn(
                name: "Protected",
                table: "execution_runs");

            migrationBuilder.DropColumn(
                name: "ProtectedAt",
                table: "execution_runs");
        }
    }
}
