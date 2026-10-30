using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BirkNext.Api.Data.DeliveryReporting.Migrations
{
    /// <inheritdoc />
    public partial class CreateDeliveryReportingSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "delivery_reporting");

            migrationBuilder.CreateTable(
                name: "project",
                schema: "delivery_reporting",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    external_reference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_project", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "report_source",
                schema: "delivery_reporting",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    original_file_name = table.Column<string>(type: "character varying(260)", maxLength: 260, nullable: false),
                    imported_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    git_commit = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    git_branch = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    archive_bytes = table.Column<long>(type: "bigint", nullable: false),
                    entry_count = table.Column<long>(type: "bigint", nullable: false),
                    metadata = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_report_source", x => x.id);
                    table.ForeignKey(
                        name: "FK_report_source_project_project_id",
                        column: x => x.project_id,
                        principalSchema: "delivery_reporting",
                        principalTable: "project",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "report_snapshot",
                schema: "delivery_reporting",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    report_source_id = table.Column<Guid>(type: "uuid", nullable: false),
                    label = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    effective_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    imported_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    role = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    coverage_status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    parse_status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    warnings_count = table.Column<int>(type: "integer", nullable: false),
                    feature_count = table.Column<int>(type: "integer", nullable: false),
                    task_count = table.Column<int>(type: "integer", nullable: false),
                    completed_task_count = table.Column<int>(type: "integer", nullable: false),
                    modules = table.Column<string>(type: "jsonb", nullable: false),
                    warnings = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_report_snapshot", x => x.id);
                    table.ForeignKey(
                        name: "FK_report_snapshot_project_project_id",
                        column: x => x.project_id,
                        principalSchema: "delivery_reporting",
                        principalTable: "project",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_report_snapshot_report_source_report_source_id",
                        column: x => x.report_source_id,
                        principalSchema: "delivery_reporting",
                        principalTable: "report_source",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "artifact_snapshot",
                schema: "delivery_reporting",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    snapshot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    path = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: false),
                    module = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    feature_key = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_artifact_snapshot", x => x.id);
                    table.ForeignKey(
                        name: "FK_artifact_snapshot_report_snapshot_snapshot_id",
                        column: x => x.snapshot_id,
                        principalSchema: "delivery_reporting",
                        principalTable: "report_snapshot",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "feature_snapshot",
                schema: "delivery_reporting",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    snapshot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    feature_key = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    module = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    folder = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    title = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    spec_path = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    tasks_path = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    plan_path = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    spec_fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    acceptance_fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    acceptance_criteria = table.Column<string>(type: "jsonb", nullable: false),
                    requirement_count = table.Column<int>(type: "integer", nullable: false),
                    user_story_count = table.Column<int>(type: "integer", nullable: false),
                    open_clarifications = table.Column<int>(type: "integer", nullable: false),
                    task_total = table.Column<int>(type: "integer", nullable: false),
                    task_completed = table.Column<int>(type: "integer", nullable: false),
                    untracked_task_count = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    status_reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_feature_snapshot", x => x.id);
                    table.ForeignKey(
                        name: "FK_feature_snapshot_report_snapshot_snapshot_id",
                        column: x => x.snapshot_id,
                        principalSchema: "delivery_reporting",
                        principalTable: "report_snapshot",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "snapshot_comparison",
                schema: "delivery_reporting",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    baseline_snapshot_id = table.Column<Guid>(type: "uuid", nullable: true),
                    current_snapshot_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    features_added = table.Column<int>(type: "integer", nullable: false),
                    features_removed = table.Column<int>(type: "integer", nullable: false),
                    features_changed = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_snapshot_comparison", x => x.id);
                    table.ForeignKey(
                        name: "FK_snapshot_comparison_project_project_id",
                        column: x => x.project_id,
                        principalSchema: "delivery_reporting",
                        principalTable: "project",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_snapshot_comparison_report_snapshot_baseline_snapshot_id",
                        column: x => x.baseline_snapshot_id,
                        principalSchema: "delivery_reporting",
                        principalTable: "report_snapshot",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_snapshot_comparison_report_snapshot_current_snapshot_id",
                        column: x => x.current_snapshot_id,
                        principalSchema: "delivery_reporting",
                        principalTable: "report_snapshot",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "evidence_reference",
                schema: "delivery_reporting",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    snapshot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    feature_snapshot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    path = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    line = table.Column<int>(type: "integer", nullable: false),
                    text = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_evidence_reference", x => x.id);
                    table.ForeignKey(
                        name: "FK_evidence_reference_feature_snapshot_feature_snapshot_id",
                        column: x => x.feature_snapshot_id,
                        principalSchema: "delivery_reporting",
                        principalTable: "feature_snapshot",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "task_snapshot",
                schema: "delivery_reporting",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    snapshot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    feature_snapshot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    task_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    task_id = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    text = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    completed = table.Column<bool>(type: "boolean", nullable: false),
                    phase = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    user_story = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    position = table.Column<int>(type: "integer", nullable: false),
                    source_path = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_task_snapshot", x => x.id);
                    table.ForeignKey(
                        name: "FK_task_snapshot_feature_snapshot_feature_snapshot_id",
                        column: x => x.feature_snapshot_id,
                        principalSchema: "delivery_reporting",
                        principalTable: "feature_snapshot",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "feature_delta",
                schema: "delivery_reporting",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    comparison_id = table.Column<Guid>(type: "uuid", nullable: false),
                    feature_key = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    title = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    module = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    baseline_status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    current_status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    baseline_task_completed = table.Column<int>(type: "integer", nullable: false),
                    baseline_task_total = table.Column<int>(type: "integer", nullable: false),
                    current_task_completed = table.Column<int>(type: "integer", nullable: false),
                    current_task_total = table.Column<int>(type: "integer", nullable: false),
                    task_delta = table.Column<int>(type: "integer", nullable: false),
                    specification_changed = table.Column<bool>(type: "boolean", nullable: false),
                    acceptance_criteria_changed = table.Column<bool>(type: "boolean", nullable: false),
                    new_feature = table.Column<bool>(type: "boolean", nullable: false),
                    removed_feature = table.Column<bool>(type: "boolean", nullable: false),
                    delivery_movement = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    reasons = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_feature_delta", x => x.id);
                    table.ForeignKey(
                        name: "FK_feature_delta_snapshot_comparison_comparison_id",
                        column: x => x.comparison_id,
                        principalSchema: "delivery_reporting",
                        principalTable: "snapshot_comparison",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "status_event",
                schema: "delivery_reporting",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    comparison_id = table.Column<Guid>(type: "uuid", nullable: false),
                    feature_key = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    from_value = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    to_value = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    detail = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_status_event", x => x.id);
                    table.ForeignKey(
                        name: "FK_status_event_snapshot_comparison_comparison_id",
                        column: x => x.comparison_id,
                        principalSchema: "delivery_reporting",
                        principalTable: "snapshot_comparison",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "weekly_report",
                schema: "delivery_reporting",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    comparison_id = table.Column<Guid>(type: "uuid", nullable: false),
                    baseline_snapshot_id = table.Column<Guid>(type: "uuid", nullable: true),
                    current_snapshot_id = table.Column<Guid>(type: "uuid", nullable: true),
                    title = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    week_label = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    period_start = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    period_end = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    generated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    coverage_status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    content_version = table.Column<int>(type: "integer", nullable: false),
                    content = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_weekly_report", x => x.id);
                    table.ForeignKey(
                        name: "FK_weekly_report_project_project_id",
                        column: x => x.project_id,
                        principalSchema: "delivery_reporting",
                        principalTable: "project",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_weekly_report_report_snapshot_baseline_snapshot_id",
                        column: x => x.baseline_snapshot_id,
                        principalSchema: "delivery_reporting",
                        principalTable: "report_snapshot",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_weekly_report_report_snapshot_current_snapshot_id",
                        column: x => x.current_snapshot_id,
                        principalSchema: "delivery_reporting",
                        principalTable: "report_snapshot",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_weekly_report_snapshot_comparison_comparison_id",
                        column: x => x.comparison_id,
                        principalSchema: "delivery_reporting",
                        principalTable: "snapshot_comparison",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "weekly_report_item",
                schema: "delivery_reporting",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    report_id = table.Column<Guid>(type: "uuid", nullable: false),
                    section = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    feature_key = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    title = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    movement = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    text = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_weekly_report_item", x => x.id);
                    table.ForeignKey(
                        name: "FK_weekly_report_item_weekly_report_report_id",
                        column: x => x.report_id,
                        principalSchema: "delivery_reporting",
                        principalTable: "weekly_report",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_artifact_snapshot_snapshot_id",
                schema: "delivery_reporting",
                table: "artifact_snapshot",
                column: "snapshot_id");

            migrationBuilder.CreateIndex(
                name: "IX_evidence_reference_feature_snapshot_id",
                schema: "delivery_reporting",
                table: "evidence_reference",
                column: "feature_snapshot_id");

            migrationBuilder.CreateIndex(
                name: "IX_evidence_reference_snapshot_id",
                schema: "delivery_reporting",
                table: "evidence_reference",
                column: "snapshot_id");

            migrationBuilder.CreateIndex(
                name: "IX_feature_delta_comparison_id",
                schema: "delivery_reporting",
                table: "feature_delta",
                column: "comparison_id");

            migrationBuilder.CreateIndex(
                name: "IX_feature_snapshot_snapshot_id_feature_key",
                schema: "delivery_reporting",
                table: "feature_snapshot",
                columns: new[] { "snapshot_id", "feature_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_project_name",
                schema: "delivery_reporting",
                table: "project",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_report_snapshot_project_id_effective_at",
                schema: "delivery_reporting",
                table: "report_snapshot",
                columns: new[] { "project_id", "effective_at" });

            migrationBuilder.CreateIndex(
                name: "IX_report_snapshot_report_source_id",
                schema: "delivery_reporting",
                table: "report_snapshot",
                column: "report_source_id");

            migrationBuilder.CreateIndex(
                name: "IX_report_source_project_id_fingerprint",
                schema: "delivery_reporting",
                table: "report_source",
                columns: new[] { "project_id", "fingerprint" });

            migrationBuilder.CreateIndex(
                name: "IX_report_source_project_id_git_commit",
                schema: "delivery_reporting",
                table: "report_source",
                columns: new[] { "project_id", "git_commit" });

            migrationBuilder.CreateIndex(
                name: "IX_snapshot_comparison_baseline_snapshot_id",
                schema: "delivery_reporting",
                table: "snapshot_comparison",
                column: "baseline_snapshot_id");

            migrationBuilder.CreateIndex(
                name: "IX_snapshot_comparison_current_snapshot_id",
                schema: "delivery_reporting",
                table: "snapshot_comparison",
                column: "current_snapshot_id");

            migrationBuilder.CreateIndex(
                name: "IX_snapshot_comparison_project_id",
                schema: "delivery_reporting",
                table: "snapshot_comparison",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "IX_status_event_comparison_id",
                schema: "delivery_reporting",
                table: "status_event",
                column: "comparison_id");

            migrationBuilder.CreateIndex(
                name: "IX_task_snapshot_feature_snapshot_id",
                schema: "delivery_reporting",
                table: "task_snapshot",
                column: "feature_snapshot_id");

            migrationBuilder.CreateIndex(
                name: "IX_task_snapshot_snapshot_id",
                schema: "delivery_reporting",
                table: "task_snapshot",
                column: "snapshot_id");

            migrationBuilder.CreateIndex(
                name: "IX_weekly_report_baseline_snapshot_id",
                schema: "delivery_reporting",
                table: "weekly_report",
                column: "baseline_snapshot_id");

            migrationBuilder.CreateIndex(
                name: "IX_weekly_report_comparison_id",
                schema: "delivery_reporting",
                table: "weekly_report",
                column: "comparison_id");

            migrationBuilder.CreateIndex(
                name: "IX_weekly_report_current_snapshot_id",
                schema: "delivery_reporting",
                table: "weekly_report",
                column: "current_snapshot_id");

            migrationBuilder.CreateIndex(
                name: "IX_weekly_report_project_id_generated_at",
                schema: "delivery_reporting",
                table: "weekly_report",
                columns: new[] { "project_id", "generated_at" });

            migrationBuilder.CreateIndex(
                name: "IX_weekly_report_item_report_id",
                schema: "delivery_reporting",
                table: "weekly_report_item",
                column: "report_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "artifact_snapshot",
                schema: "delivery_reporting");

            migrationBuilder.DropTable(
                name: "evidence_reference",
                schema: "delivery_reporting");

            migrationBuilder.DropTable(
                name: "feature_delta",
                schema: "delivery_reporting");

            migrationBuilder.DropTable(
                name: "status_event",
                schema: "delivery_reporting");

            migrationBuilder.DropTable(
                name: "task_snapshot",
                schema: "delivery_reporting");

            migrationBuilder.DropTable(
                name: "weekly_report_item",
                schema: "delivery_reporting");

            migrationBuilder.DropTable(
                name: "feature_snapshot",
                schema: "delivery_reporting");

            migrationBuilder.DropTable(
                name: "weekly_report",
                schema: "delivery_reporting");

            migrationBuilder.DropTable(
                name: "snapshot_comparison",
                schema: "delivery_reporting");

            migrationBuilder.DropTable(
                name: "report_snapshot",
                schema: "delivery_reporting");

            migrationBuilder.DropTable(
                name: "report_source",
                schema: "delivery_reporting");

            migrationBuilder.DropTable(
                name: "project",
                schema: "delivery_reporting");
        }
    }
}
