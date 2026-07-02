using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BirkNext.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkspaceReviewSteps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "workspace_review_steps",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    step_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    step_title = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    required_artifact_types_json = table.Column<string>(type: "text", nullable: true),
                    prerequisite_state = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    review_state = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    approval_state = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    reviewed_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    reviewed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    approved_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    approved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    rejected_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    rejected_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    comment = table.Column<string>(type: "text", nullable: true),
                    last_opened_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    artifact_set_hash_at_approval = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_workspace_review_steps", x => x.id);
                    table.ForeignKey(
                        name: "fk_workspace_review_steps_saved_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "saved_workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_workspace_review_steps_workspace_key",
                table: "workspace_review_steps",
                columns: new[] { "workspace_id", "step_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_workspace_review_steps_workspace_approval",
                table: "workspace_review_steps",
                columns: new[] { "workspace_id", "approval_state" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "workspace_review_steps");
        }
    }
}
