using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BirkNext.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddActiveCdcControlPersonPk : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SyntheticPersonPkControl",
                table: "active_cdc_runs",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_active_cdc_runs_EnvironmentId_SyntheticPersonPkControl",
                table: "active_cdc_runs",
                columns: new[] { "EnvironmentId", "SyntheticPersonPkControl" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_active_cdc_runs_EnvironmentId_SyntheticPersonPkControl",
                table: "active_cdc_runs");

            migrationBuilder.DropColumn(
                name: "SyntheticPersonPkControl",
                table: "active_cdc_runs");
        }
    }
}
