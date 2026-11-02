using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BirkNext.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddObservedOperationConfidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "confidence",
                table: "observed_operations",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Candidate");

            migrationBuilder.AddColumn<string>(
                name: "provenance",
                table: "observed_operations",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Unknown");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "confidence",
                table: "observed_operations");

            migrationBuilder.DropColumn(
                name: "provenance",
                table: "observed_operations");
        }
    }
}
