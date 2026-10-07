using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BirkNext.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class BindReviewDecisionsToArtifactRevisions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_workspace_review_progress_workspace_key",
                table: "workspace_review_progress");

            migrationBuilder.AddColumn<string>(
                name: "artifact_identity_hash",
                table: "workspace_review_progress",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "artifact_references",
                table: "workspace_review_progress",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "artifact_set_hash",
                table: "workspace_review_progress",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_workspace_review_progress_workspace_key_binding",
                table: "workspace_review_progress",
                columns: new[] { "workspace_id", "step_key", "artifact_set_hash" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_workspace_review_progress_workspace_key_binding",
                table: "workspace_review_progress");

            migrationBuilder.DropColumn(
                name: "artifact_identity_hash",
                table: "workspace_review_progress");

            migrationBuilder.DropColumn(
                name: "artifact_references",
                table: "workspace_review_progress");

            migrationBuilder.DropColumn(
                name: "artifact_set_hash",
                table: "workspace_review_progress");

            migrationBuilder.CreateIndex(
                name: "ix_workspace_review_progress_workspace_key",
                table: "workspace_review_progress",
                columns: new[] { "workspace_id", "step_key" },
                unique: true);
        }
    }
}
