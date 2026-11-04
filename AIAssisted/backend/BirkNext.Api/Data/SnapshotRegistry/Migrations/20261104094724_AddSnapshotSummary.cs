using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BirkNext.Api.Data.SnapshotRegistry.Migrations
{
    /// <inheritdoc />
    public partial class AddSnapshotSummary : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "summary",
                schema: "snapshot_registry",
                table: "snapshot_entry",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "summary",
                schema: "snapshot_registry",
                table: "snapshot_entry");
        }
    }
}
