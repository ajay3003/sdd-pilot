using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BirkNext.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSourceSchemaArtifacts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "source_schema_artifacts",
                columns: table => new
                {
                    snapshot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    snapshot_fingerprint = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    source_archive_name = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    project_import_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    captured_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    is_complete = table.Column<bool>(type: "boolean", nullable: false),
                    omitted_files = table.Column<int>(type: "integer", nullable: false),
                    package_json = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_source_schema_artifacts", x => x.snapshot_id);
                    table.ForeignKey(
                        name: "FK_source_schema_artifacts_iqr_source_snapshots_snapshot_id",
                        column: x => x.snapshot_id,
                        principalTable: "iqr_source_snapshots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "source_schema_artifacts");
        }
    }
}
