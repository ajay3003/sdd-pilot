using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BirkNext.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkspacePersistence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "saved_workspaces",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    name = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    project_name = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_opened_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    version = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    parser_version = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    review_context_version = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    artifact_set_hash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    auto_saved = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    favorite = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    tags_json = table.Column<string>(type: "text", nullable: true),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_saved_workspaces", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "saved_workspace_artifacts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    artifact_type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    file_name = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    original_path = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    content = table.Column<string>(type: "text", nullable: false),
                    content_hash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    encoding = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    last_modified = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    parse_version = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_saved_workspace_artifacts", x => x.id);
                    table.ForeignKey(
                        name: "FK_saved_workspace_artifacts_saved_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "saved_workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_saved_artifacts_workspace_type",
                table: "saved_workspace_artifacts",
                columns: new[] { "workspace_id", "artifact_type" });

            migrationBuilder.CreateIndex(
                name: "ix_saved_workspaces_user_not_deleted",
                table: "saved_workspaces",
                columns: new[] { "user_id", "is_deleted" });

            migrationBuilder.CreateIndex(
                name: "ix_saved_workspaces_user_updated",
                table: "saved_workspaces",
                columns: new[] { "user_id", "updated_at" },
                descending: new[] { false, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "saved_workspace_artifacts");

            migrationBuilder.DropTable(
                name: "saved_workspaces");
        }
    }
}
