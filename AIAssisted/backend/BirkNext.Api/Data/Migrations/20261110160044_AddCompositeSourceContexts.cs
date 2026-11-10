using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BirkNext.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCompositeSourceContexts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PlanJson",
                table: "integration_suite_runs",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "composite_source_contexts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ContextKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    ScopeId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    OwningProject = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    DefinitionJson = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_composite_source_contexts", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_composite_source_contexts_ContextKey_Revision",
                table: "composite_source_contexts",
                columns: new[] { "ContextKey", "Revision" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "composite_source_contexts");

            migrationBuilder.DropColumn(
                name: "PlanJson",
                table: "integration_suite_runs");
        }
    }
}
