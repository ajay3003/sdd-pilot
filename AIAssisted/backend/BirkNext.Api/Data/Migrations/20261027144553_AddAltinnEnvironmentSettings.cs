using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BirkNext.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAltinnEnvironmentSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "altinn_environment_settings",
                columns: table => new
                {
                    environment_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    document_json = table.Column<string>(type: "text", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_altinn_environment_settings", x => x.environment_id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "altinn_environment_settings");
        }
    }
}
