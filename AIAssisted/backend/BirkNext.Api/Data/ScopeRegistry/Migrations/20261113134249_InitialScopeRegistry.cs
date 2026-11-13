using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BirkNext.Api.Data.ScopeRegistry.Migrations
{
    /// <inheritdoc />
    public partial class InitialScopeRegistry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "scope_registry");

            migrationBuilder.CreateTable(
                name: "scope",
                schema: "scope_registry",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_key = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    purpose = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    owner = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    current_revision = table.Column<int>(type: "integer", nullable: false),
                    approved_revision = table.Column<int>(type: "integer", nullable: true),
                    version = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_scope", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "scope_revision",
                schema: "scope_registry",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    scope_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<int>(type: "integer", nullable: false),
                    state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    members_json = table.Column<string>(type: "jsonb", nullable: false),
                    change_summary = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    content_fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    approved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    approved_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    approval_rationale = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_scope_revision", x => x.id);
                    table.ForeignKey(
                        name: "FK_scope_revision_scope_scope_id",
                        column: x => x.scope_id,
                        principalSchema: "scope_registry",
                        principalTable: "scope",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_scope_project",
                schema: "scope_registry",
                table: "scope",
                column: "project_key");

            migrationBuilder.CreateIndex(
                name: "ux_scope_project_name",
                schema: "scope_registry",
                table: "scope",
                columns: new[] { "project_key", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_scope_revision_number",
                schema: "scope_registry",
                table: "scope_revision",
                columns: new[] { "scope_id", "number" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "scope_revision",
                schema: "scope_registry");

            migrationBuilder.DropTable(
                name: "scope",
                schema: "scope_registry");
        }
    }
}
