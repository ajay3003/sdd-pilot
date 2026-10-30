using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BirkNext.Api.Data.DeliveryReporting.Migrations
{
    /// <inheritdoc />
    public partial class AddDeliveryHierarchy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "phase_key",
                schema: "delivery_reporting",
                table: "task_snapshot",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "user_story_key",
                schema: "delivery_reporting",
                table: "task_snapshot",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "hierarchy_captured",
                schema: "delivery_reporting",
                table: "feature_snapshot",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "acceptance_criterion_snapshot",
                schema: "delivery_reporting",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    snapshot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    feature_snapshot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    story_key = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    criterion_key = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    text = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    source_line = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_acceptance_criterion_snapshot", x => x.id);
                    table.ForeignKey(
                        name: "FK_acceptance_criterion_snapshot_feature_snapshot_feature_snap~",
                        column: x => x.feature_snapshot_id,
                        principalSchema: "delivery_reporting",
                        principalTable: "feature_snapshot",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "phase_snapshot",
                schema: "delivery_reporting",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    snapshot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    feature_snapshot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    phase_key = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    title = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_phase_snapshot", x => x.id);
                    table.ForeignKey(
                        name: "FK_phase_snapshot_feature_snapshot_feature_snapshot_id",
                        column: x => x.feature_snapshot_id,
                        principalSchema: "delivery_reporting",
                        principalTable: "feature_snapshot",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_story_snapshot",
                schema: "delivery_reporting",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    snapshot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    feature_snapshot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    story_key = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    description = table.Column<string>(type: "character varying(1500)", maxLength: 1500, nullable: true),
                    priority = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    phase_keys_json = table.Column<string>(type: "text", nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    source_line = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_story_snapshot", x => x.id);
                    table.ForeignKey(
                        name: "FK_user_story_snapshot_feature_snapshot_feature_snapshot_id",
                        column: x => x.feature_snapshot_id,
                        principalSchema: "delivery_reporting",
                        principalTable: "feature_snapshot",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_acceptance_criterion_snapshot_feature_snapshot_id",
                schema: "delivery_reporting",
                table: "acceptance_criterion_snapshot",
                column: "feature_snapshot_id");

            migrationBuilder.CreateIndex(
                name: "IX_acceptance_criterion_snapshot_snapshot_id",
                schema: "delivery_reporting",
                table: "acceptance_criterion_snapshot",
                column: "snapshot_id");

            migrationBuilder.CreateIndex(
                name: "IX_phase_snapshot_feature_snapshot_id_phase_key",
                schema: "delivery_reporting",
                table: "phase_snapshot",
                columns: new[] { "feature_snapshot_id", "phase_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_phase_snapshot_snapshot_id",
                schema: "delivery_reporting",
                table: "phase_snapshot",
                column: "snapshot_id");

            migrationBuilder.CreateIndex(
                name: "IX_user_story_snapshot_feature_snapshot_id_story_key",
                schema: "delivery_reporting",
                table: "user_story_snapshot",
                columns: new[] { "feature_snapshot_id", "story_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_user_story_snapshot_snapshot_id",
                schema: "delivery_reporting",
                table: "user_story_snapshot",
                column: "snapshot_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "acceptance_criterion_snapshot",
                schema: "delivery_reporting");

            migrationBuilder.DropTable(
                name: "phase_snapshot",
                schema: "delivery_reporting");

            migrationBuilder.DropTable(
                name: "user_story_snapshot",
                schema: "delivery_reporting");

            migrationBuilder.DropColumn(
                name: "phase_key",
                schema: "delivery_reporting",
                table: "task_snapshot");

            migrationBuilder.DropColumn(
                name: "user_story_key",
                schema: "delivery_reporting",
                table: "task_snapshot");

            migrationBuilder.DropColumn(
                name: "hierarchy_captured",
                schema: "delivery_reporting",
                table: "feature_snapshot");
        }
    }
}
