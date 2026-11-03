using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BirkNext.Api.Data.DeliveryReporting.Migrations
{
    /// <inheritdoc />
    public partial class AddDeliveryWorkItemRelationships : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "kind",
                schema: "delivery_reporting",
                table: "user_story_snapshot",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "removed",
                schema: "delivery_reporting",
                table: "user_story_snapshot",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "parser_version",
                schema: "delivery_reporting",
                table: "report_snapshot",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "kind",
                schema: "delivery_reporting",
                table: "user_story_snapshot");

            migrationBuilder.DropColumn(
                name: "removed",
                schema: "delivery_reporting",
                table: "user_story_snapshot");

            migrationBuilder.DropColumn(
                name: "parser_version",
                schema: "delivery_reporting",
                table: "report_snapshot");
        }
    }
}
