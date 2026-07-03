using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BirkNext.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class RefactorWorkspaceReviewProgressSeparateDefinition : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Drop old indexes
            migrationBuilder.DropIndex(
                name: "ix_workspace_review_steps_workspace_approval",
                table: "workspace_review_steps");

            migrationBuilder.DropIndex(
                name: "ix_workspace_review_steps_workspace_key",
                table: "workspace_review_steps");

            // Rename table
            migrationBuilder.RenameTable(
                name: "workspace_review_steps",
                newName: "workspace_review_progress");

            // Drop computed state columns
            migrationBuilder.DropColumn(
                name: "step_title",
                table: "workspace_review_progress");

            migrationBuilder.DropColumn(
                name: "prerequisite_state",
                table: "workspace_review_progress");

            migrationBuilder.DropColumn(
                name: "required_artifact_types_json",
                table: "workspace_review_progress");

            // Add new columns for artifact/version tracking
            migrationBuilder.AddColumn<string>(
                name: "artifact_set_hash_at_review",
                table: "workspace_review_progress",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "review_context_version_at_approval",
                table: "workspace_review_progress",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "workspace_version_at_approval",
                table: "workspace_review_progress",
                type: "integer",
                nullable: true);

            // Recreate indexes with new names
            migrationBuilder.CreateIndex(
                name: "ix_workspace_review_progress_workspace_key",
                table: "workspace_review_progress",
                columns: new[] { "workspace_id", "step_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_workspace_review_progress_workspace_approval",
                table: "workspace_review_progress",
                columns: new[] { "workspace_id", "approval_state" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Drop new indexes
            migrationBuilder.DropIndex(
                name: "ix_workspace_review_progress_workspace_approval",
                table: "workspace_review_progress");

            migrationBuilder.DropIndex(
                name: "ix_workspace_review_progress_workspace_key",
                table: "workspace_review_progress");

            // Drop new columns
            migrationBuilder.DropColumn(
                name: "artifact_set_hash_at_review",
                table: "workspace_review_progress");

            migrationBuilder.DropColumn(
                name: "review_context_version_at_approval",
                table: "workspace_review_progress");

            migrationBuilder.DropColumn(
                name: "workspace_version_at_approval",
                table: "workspace_review_progress");

            // Rename table back
            migrationBuilder.RenameTable(
                name: "workspace_review_progress",
                newName: "workspace_review_steps");

            // Add back computed columns with default values
            migrationBuilder.AddColumn<string>(
                name: "step_title",
                table: "workspace_review_steps",
                type: "character varying(500)",
                maxLength: 500,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "prerequisite_state",
                table: "workspace_review_steps",
                type: "character varying(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "Missing");

            migrationBuilder.AddColumn<string>(
                name: "required_artifact_types_json",
                table: "workspace_review_steps",
                type: "text",
                nullable: true);

            // Recreate old indexes
            migrationBuilder.CreateIndex(
                name: "ix_workspace_review_steps_workspace_approval",
                table: "workspace_review_steps",
                columns: new[] { "workspace_id", "approval_state" });

            migrationBuilder.CreateIndex(
                name: "ix_workspace_review_steps_workspace_key",
                table: "workspace_review_steps",
                columns: new[] { "workspace_id", "step_key" },
                unique: true);
        }
    }
}
