using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BirkNext.Api.Data.DeliveryReporting.Migrations
{
    /// <inheritdoc />
    public partial class AddDeliveryDecisionReferences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "decision_references",
                schema: "delivery_reporting",
                table: "feature_snapshot",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "decision_references",
                schema: "delivery_reporting",
                table: "feature_snapshot");
        }
    }
}
