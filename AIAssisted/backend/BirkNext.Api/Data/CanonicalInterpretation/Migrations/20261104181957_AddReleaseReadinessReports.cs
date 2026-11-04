using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BirkNext.Api.Data.CanonicalInterpretation.Migrations
{
    /// <inheritdoc />
    public partial class AddReleaseReadinessReports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "release_readiness_report",
                schema: "canonical_interpretation",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    scope = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    frozen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    decision = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    modules = table.Column<string>(type: "jsonb", nullable: false),
                    interpretation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    rule_set_version = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    policy_set_version = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    assessment = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_release_readiness_report", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "release_sign_off",
                schema: "canonical_interpretation",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    report_id = table.Column<Guid>(type: "uuid", nullable: false),
                    scope = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    outcome = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    signed_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    signed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    note = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_release_sign_off", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_release_readiness_report_scope_frozen_at",
                schema: "canonical_interpretation",
                table: "release_readiness_report",
                columns: new[] { "scope", "frozen_at" });

            migrationBuilder.CreateIndex(
                name: "IX_release_sign_off_scope_report_id_signed_at",
                schema: "canonical_interpretation",
                table: "release_sign_off",
                columns: new[] { "scope", "report_id", "signed_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "release_readiness_report",
                schema: "canonical_interpretation");

            migrationBuilder.DropTable(
                name: "release_sign_off",
                schema: "canonical_interpretation");
        }
    }
}
