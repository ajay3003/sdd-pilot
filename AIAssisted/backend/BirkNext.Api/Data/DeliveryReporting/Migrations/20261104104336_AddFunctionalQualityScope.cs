using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BirkNext.Api.Data.DeliveryReporting.Migrations
{
    /// <inheritdoc />
    public partial class AddFunctionalQualityScope : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "functional_scope",
                schema: "delivery_reporting",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    roots = table.Column<string>(type: "jsonb", nullable: false),
                    include_related = table.Column<bool>(type: "boolean", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_functional_scope", x => x.id);
                    table.ForeignKey(
                        name: "FK_functional_scope_project_project_id",
                        column: x => x.project_id,
                        principalSchema: "delivery_reporting",
                        principalTable: "project",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "test_design_assessment",
                schema: "delivery_reporting",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    item_key = table.Column<string>(type: "character varying(600)", maxLength: 600, nullable: false),
                    testability = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    verification_types = table.Column<string>(type: "jsonb", nullable: false),
                    required_test_data = table.Column<string>(type: "jsonb", nullable: false),
                    comment = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    reviewed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_test_design_assessment", x => x.id);
                    table.ForeignKey(
                        name: "FK_test_design_assessment_project_project_id",
                        column: x => x.project_id,
                        principalSchema: "delivery_reporting",
                        principalTable: "project",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "functional_scope_reference",
                schema: "delivery_reporting",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    scope_id = table.Column<Guid>(type: "uuid", nullable: false),
                    item_key = table.Column<string>(type: "character varying(600)", maxLength: 600, nullable: false),
                    mode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    added_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_functional_scope_reference", x => x.id);
                    table.ForeignKey(
                        name: "FK_functional_scope_reference_functional_scope_scope_id",
                        column: x => x.scope_id,
                        principalSchema: "delivery_reporting",
                        principalTable: "functional_scope",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "functional_scope_snapshot",
                schema: "delivery_reporting",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    scope_id = table.Column<Guid>(type: "uuid", nullable: true),
                    scope_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    scope_version = table.Column<int>(type: "integer", nullable: false),
                    delivery_snapshot_id = table.Column<Guid>(type: "uuid", nullable: true),
                    rule_set_version = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    items = table.Column<string>(type: "jsonb", nullable: false),
                    item_count = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_functional_scope_snapshot", x => x.id);
                    table.ForeignKey(
                        name: "FK_functional_scope_snapshot_functional_scope_scope_id",
                        column: x => x.scope_id,
                        principalSchema: "delivery_reporting",
                        principalTable: "functional_scope",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_functional_scope_snapshot_project_project_id",
                        column: x => x.project_id,
                        principalSchema: "delivery_reporting",
                        principalTable: "project",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_functional_scope_project_id_name",
                schema: "delivery_reporting",
                table: "functional_scope",
                columns: new[] { "project_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_functional_scope_reference_scope_id_item_key",
                schema: "delivery_reporting",
                table: "functional_scope_reference",
                columns: new[] { "scope_id", "item_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_functional_scope_snapshot_project_id_created_at",
                schema: "delivery_reporting",
                table: "functional_scope_snapshot",
                columns: new[] { "project_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_functional_scope_snapshot_scope_id",
                schema: "delivery_reporting",
                table: "functional_scope_snapshot",
                column: "scope_id");

            migrationBuilder.CreateIndex(
                name: "IX_test_design_assessment_project_id_item_key",
                schema: "delivery_reporting",
                table: "test_design_assessment",
                columns: new[] { "project_id", "item_key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "functional_scope_reference",
                schema: "delivery_reporting");

            migrationBuilder.DropTable(
                name: "functional_scope_snapshot",
                schema: "delivery_reporting");

            migrationBuilder.DropTable(
                name: "test_design_assessment",
                schema: "delivery_reporting");

            migrationBuilder.DropTable(
                name: "functional_scope",
                schema: "delivery_reporting");
        }
    }
}
