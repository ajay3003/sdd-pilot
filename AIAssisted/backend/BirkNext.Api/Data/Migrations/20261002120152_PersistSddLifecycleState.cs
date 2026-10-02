using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BirkNext.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class PersistSddLifecycleState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER TABLE saved_workspaces ADD COLUMN IF NOT EXISTS sdd_lifecycle_json text NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER TABLE saved_workspaces DROP COLUMN IF EXISTS sdd_lifecycle_json;");
        }
    }
}
