using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BirkNext.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddEndpointObservationHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "endpoint_discovery_runs",
                columns: table => new
                {
                    id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    environment_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    host = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    observed_count = table.Column<int>(type: "integer", nullable: false),
                    new_count = table.Column<int>(type: "integer", nullable: false),
                    observed_again_count = table.Column<int>(type: "integer", nullable: false),
                    not_seen_count = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_endpoint_discovery_runs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "observed_operations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    environment_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    host = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    port = table.Column<int>(type: "integer", nullable: false),
                    identity = table.Column<string>(type: "character varying(600)", maxLength: 600, nullable: false),
                    protocol = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    method = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    path = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    graphql_operation_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    graphql_operation_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    first_observed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_observed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    observation_count = table.Column<int>(type: "integer", nullable: false),
                    first_run_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    last_run_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    last_status = table.Column<int>(type: "integer", nullable: false),
                    auth_observed = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_observed_operations", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_endpoint_discovery_runs_environment_id_host_started_at",
                table: "endpoint_discovery_runs",
                columns: new[] { "environment_id", "host", "started_at" });

            migrationBuilder.CreateIndex(
                name: "IX_observed_operations_environment_id_host_identity",
                table: "observed_operations",
                columns: new[] { "environment_id", "host", "identity" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "endpoint_discovery_runs");

            migrationBuilder.DropTable(
                name: "observed_operations");
        }
    }
}
