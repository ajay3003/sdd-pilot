using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BirkNext.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class PersistCurrentWorkspaceSelection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "is_current",
                table: "saved_workspaces",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "ix_saved_workspaces_user_current",
                table: "saved_workspaces",
                columns: new[] { "user_id", "is_current" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_saved_workspaces_user_current",
                table: "saved_workspaces");

            migrationBuilder.DropColumn(
                name: "is_current",
                table: "saved_workspaces");
        }
    }
}
