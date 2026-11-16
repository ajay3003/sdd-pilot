using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BirkNext.Api.Data.IntegrationJourneys.Migrations
{
    /// <inheritdoc />
    public partial class JourneyRequirementLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "journey_requirement_link",
                schema: "integration_journeys",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_key = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    journey_id = table.Column<Guid>(type: "uuid", nullable: false),
                    requirement_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    specification_path = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    content = table.Column<string>(type: "jsonb", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_journey_requirement_link", x => x.id);
                    table.ForeignKey(
                        name: "FK_journey_requirement_link_journey_journey_id",
                        column: x => x.journey_id,
                        principalSchema: "integration_journeys",
                        principalTable: "journey",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_journey_requirement_link_journey_id",
                schema: "integration_journeys",
                table: "journey_requirement_link",
                column: "journey_id");

            migrationBuilder.CreateIndex(
                name: "ux_journey_requirement_link",
                schema: "integration_journeys",
                table: "journey_requirement_link",
                columns: new[] { "project_key", "journey_id", "requirement_id", "specification_path" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "journey_requirement_link",
                schema: "integration_journeys");
        }
    }
}
