using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BirkNext.Api.Data.Migrations
{
    /// <summary>
    /// Snapshot reconciliation only. No schema change.
    ///
    /// Migration 20260702140000_RefactorWorkspaceReviewProgressSeparateDefinition already renames
    /// workspace_review_steps to workspace_review_progress, but AppDbContextModelSnapshot was never
    /// regenerated afterwards, so the WorkspaceReviewProgress entity was missing from EF's recorded
    /// model entirely. Every subsequent "migrations add" therefore proposed creating that table
    /// again, which would have bundled an unrelated CreateTable into unrelated migrations.
    ///
    /// The table already exists both in the dev database and on any database built from the
    /// migration chain, so Up and Down are intentionally empty: applying or reverting this
    /// migration changes no schema and touches no data. Its only effect is to carry the corrected
    /// model snapshot forward.
    /// </summary>
    public partial class ReconcileWorkspaceReviewProgressModelSnapshot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Intentionally empty. See class summary.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Intentionally empty. See class summary.
        }
    }
}
