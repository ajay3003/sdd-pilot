using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BirkNext.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddGraphQlSchemaArtifacts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "graphql_schema_artifacts",
                columns: table => new
                {
                    environment_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    target_id = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    content_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    content = table.Column<string>(type: "text", nullable: false),
                    document_json = table.Column<string>(type: "text", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_graphql_schema_artifacts", x => new { x.environment_id, x.target_id });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "graphql_schema_artifacts");
        }
    }
}
