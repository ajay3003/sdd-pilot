using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BirkNext.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSecurityExpectationDiscovery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "security_expectation_discoveries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    environment_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    source_snapshot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    evidence_json = table.Column<string>(type: "text", nullable: false),
                    decisions_json = table.Column<string>(type: "text", nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_security_expectation_discoveries", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_security_expectation_discoveries_environment_id_created_at",
                table: "security_expectation_discoveries",
                columns: new[] { "environment_id", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "security_expectation_discoveries");
        }
    }
}
