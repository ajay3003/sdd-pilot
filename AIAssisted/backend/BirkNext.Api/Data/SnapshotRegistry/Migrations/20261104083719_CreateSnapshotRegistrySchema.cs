using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BirkNext.Api.Data.SnapshotRegistry.Migrations
{
    /// <inheritdoc />
    public partial class CreateSnapshotRegistrySchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "snapshot_registry");

            migrationBuilder.CreateTable(
                name: "snapshot_edge",
                schema: "snapshot_registry",
                columns: table => new
                {
                    from_type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    from_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    kind = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    to_type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    to_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    recorded_by = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_snapshot_edge", x => new { x.from_type, x.from_id, x.kind, x.to_type, x.to_id });
                });

            migrationBuilder.CreateTable(
                name: "snapshot_entry",
                schema: "snapshot_registry",
                columns: table => new
                {
                    snapshot_type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    snapshot_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    project = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    module = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    label = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    effective_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    source_revision = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    analysis_version = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    storage_reference = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    content_fingerprint = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    retention = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    retention_set_by_user = table.Column<bool>(type: "boolean", nullable: false),
                    frozen = table.Column<bool>(type: "boolean", nullable: false),
                    metadata_version = table.Column<int>(type: "integer", nullable: false),
                    payload_available = table.Column<bool>(type: "boolean", nullable: false),
                    registered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_snapshot_entry", x => new { x.snapshot_type, x.snapshot_id });
                });

            migrationBuilder.CreateTable(
                name: "snapshot_pointer",
                schema: "snapshot_registry",
                columns: table => new
                {
                    project = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    snapshot_type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    module = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    kind = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    snapshot_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    set_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    set_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_snapshot_pointer", x => new { x.project, x.snapshot_type, x.module, x.kind });
                });

            migrationBuilder.CreateIndex(
                name: "IX_snapshot_edge_to_type_to_id",
                schema: "snapshot_registry",
                table: "snapshot_edge",
                columns: new[] { "to_type", "to_id" });

            migrationBuilder.CreateIndex(
                name: "IX_snapshot_entry_project_snapshot_type_module_created_at",
                schema: "snapshot_registry",
                table: "snapshot_entry",
                columns: new[] { "project", "snapshot_type", "module", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_snapshot_entry_snapshot_type_created_at",
                schema: "snapshot_registry",
                table: "snapshot_entry",
                columns: new[] { "snapshot_type", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "snapshot_edge",
                schema: "snapshot_registry");

            migrationBuilder.DropTable(
                name: "snapshot_entry",
                schema: "snapshot_registry");

            migrationBuilder.DropTable(
                name: "snapshot_pointer",
                schema: "snapshot_registry");
        }
    }
}
