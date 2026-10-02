using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BirkNext.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPerformanceTestReview : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "performance_baselines",
                columns: table => new
                {
                    id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    environment_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    definition_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    comparison_fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    document_json = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_performance_baselines", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "performance_test_data_profiles",
                columns: table => new
                {
                    id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    environment_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    document_json = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_performance_test_data_profiles", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "performance_test_definitions",
                columns: table => new
                {
                    id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    environment_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    archived = table.Column<bool>(type: "boolean", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    document_json = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_performance_test_definitions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "performance_test_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    environment_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    definition_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    state = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    document_json = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_performance_test_runs", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_performance_baselines_scope",
                table: "performance_baselines",
                columns: new[] { "environment_id", "definition_id" });

            migrationBuilder.CreateIndex(
                name: "ix_performance_test_data_profiles_environment",
                table: "performance_test_data_profiles",
                column: "environment_id");

            migrationBuilder.CreateIndex(
                name: "ix_performance_test_definitions_environment",
                table: "performance_test_definitions",
                column: "environment_id");

            migrationBuilder.CreateIndex(
                name: "ix_performance_test_runs_definition",
                table: "performance_test_runs",
                column: "definition_id");

            migrationBuilder.CreateIndex(
                name: "ix_performance_test_runs_environment_created",
                table: "performance_test_runs",
                columns: new[] { "environment_id", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "performance_baselines");

            migrationBuilder.DropTable(
                name: "performance_test_data_profiles");

            migrationBuilder.DropTable(
                name: "performance_test_definitions");

            migrationBuilder.DropTable(
                name: "performance_test_runs");
        }
    }
}
