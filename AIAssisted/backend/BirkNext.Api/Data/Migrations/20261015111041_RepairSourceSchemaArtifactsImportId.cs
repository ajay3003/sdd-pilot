using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BirkNext.Api.Data.Migrations
{
    /// <summary>
    /// Repairs databases where <c>20261014082517_AddSourceSchemaArtifacts</c> was applied before its <c>project_import_id</c> column was added to
    /// the migration in place: EF records the migration as applied, so the column is never created and every Source Analysis snapshot save
    /// fails (the schema artifact is saved with the snapshot). Idempotent: a no-op where the column already exists.
    /// </summary>
    public partial class RepairSourceSchemaArtifactsImportId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER TABLE source_schema_artifacts ADD COLUMN IF NOT EXISTS project_import_id character varying(128) NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The column belongs to AddSourceSchemaArtifacts; reverting this repair must not remove it.
        }
    }
}
