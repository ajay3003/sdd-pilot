using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BirkNext.Api.Data.DeliveryReporting.Migrations
{
    /// <inheritdoc />
    public partial class AddDeliveryTraceability : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "is_parallel",
                schema: "delivery_reporting",
                table: "task_snapshot",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "effective_at_corrections",
                schema: "delivery_reporting",
                table: "report_snapshot",
                type: "jsonb",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "effective_at_provenance",
                schema: "delivery_reporting",
                table: "report_snapshot",
                type: "character varying(80)",
                maxLength: 80,
                nullable: false,
                defaultValue: "Entered at import");

            migrationBuilder.AddColumn<string>(
                name: "requirements",
                schema: "delivery_reporting",
                table: "feature_snapshot",
                type: "jsonb",
                nullable: false,
                defaultValue: "[]");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "is_parallel",
                schema: "delivery_reporting",
                table: "task_snapshot");

            migrationBuilder.DropColumn(
                name: "effective_at_corrections",
                schema: "delivery_reporting",
                table: "report_snapshot");

            migrationBuilder.DropColumn(
                name: "effective_at_provenance",
                schema: "delivery_reporting",
                table: "report_snapshot");

            migrationBuilder.DropColumn(
                name: "requirements",
                schema: "delivery_reporting",
                table: "feature_snapshot");
        }
    }
}
