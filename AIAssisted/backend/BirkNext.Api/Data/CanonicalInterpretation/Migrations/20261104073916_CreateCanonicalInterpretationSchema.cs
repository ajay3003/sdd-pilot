using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BirkNext.Api.Data.CanonicalInterpretation.Migrations
{
    /// <inheritdoc />
    public partial class CreateCanonicalInterpretationSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "canonical_interpretation");

            migrationBuilder.CreateTable(
                name: "entity",
                schema: "canonical_interpretation",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    scope = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    entity_type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    identity_key = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    display_name = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    source_identity = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    resolution_method = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    version = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    attributes = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_interpretation_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_entity", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "interpretation_snapshot",
                schema: "canonical_interpretation",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    scope = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    rule_set_versions = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    source_snapshots = table.Column<string>(type: "jsonb", nullable: false),
                    source_set_fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    decision_sequence = table.Column<long>(type: "bigint", nullable: false),
                    entity_count = table.Column<int>(type: "integer", nullable: false),
                    relation_count = table.Column<int>(type: "integer", nullable: false),
                    content_fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    limitations = table.Column<string>(type: "jsonb", nullable: false),
                    trigger = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    previous_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_interpretation_snapshot", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "review_decision",
                schema: "canonical_interpretation",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    sequence = table.Column<long>(type: "bigint", nullable: false),
                    scope = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    target_kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    target_key = table.Column<string>(type: "character varying(2200)", maxLength: 2200, nullable: false),
                    target_relation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    target_alias_id = table.Column<Guid>(type: "uuid", nullable: true),
                    proposed = table.Column<string>(type: "jsonb", nullable: false),
                    outcome = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    reviewer = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    evidence_fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    rule_set_version = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    interpretation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    supersedes_id = table.Column<Guid>(type: "uuid", nullable: true),
                    superseded_by_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_review_decision", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "alias",
                schema: "canonical_interpretation",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    scope = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    alias = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    normalized_alias = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    source = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    rule_id = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_alias", x => x.id);
                    table.ForeignKey(
                        name: "FK_alias_entity_entity_id",
                        column: x => x.entity_id,
                        principalSchema: "canonical_interpretation",
                        principalTable: "entity",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "occurrence",
                schema: "canonical_interpretation",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    scope = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    source_snapshot_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    occurrence_key = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    kind = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    detail = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    first_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_occurrence", x => x.id);
                    table.ForeignKey(
                        name: "FK_occurrence_entity_entity_id",
                        column: x => x.entity_id,
                        principalSchema: "canonical_interpretation",
                        principalTable: "entity",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "relation",
                schema: "canonical_interpretation",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    scope = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    source_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    relation_type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    relation_key = table.Column<string>(type: "character varying(2200)", maxLength: 2200, nullable: false),
                    resolution = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    confidence = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    rule_id = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    rule_set_version = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    evidence = table.Column<string>(type: "jsonb", nullable: false),
                    evidence_fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    explanation = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    review_state = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    review_decision_id = table.Column<Guid>(type: "uuid", nullable: true),
                    active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_relation", x => x.id);
                    table.ForeignKey(
                        name: "FK_relation_entity_source_entity_id",
                        column: x => x.source_entity_id,
                        principalSchema: "canonical_interpretation",
                        principalTable: "entity",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_relation_entity_target_entity_id",
                        column: x => x.target_entity_id,
                        principalSchema: "canonical_interpretation",
                        principalTable: "entity",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "interpretation_snapshot_entity",
                schema: "canonical_interpretation",
                columns: table => new
                {
                    snapshot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    display_name = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    version = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_interpretation_snapshot_entity", x => new { x.snapshot_id, x.entity_id });
                    table.ForeignKey(
                        name: "FK_interpretation_snapshot_entity_entity_entity_id",
                        column: x => x.entity_id,
                        principalSchema: "canonical_interpretation",
                        principalTable: "entity",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_interpretation_snapshot_entity_interpretation_snapshot_snap~",
                        column: x => x.snapshot_id,
                        principalSchema: "canonical_interpretation",
                        principalTable: "interpretation_snapshot",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "interpretation_snapshot_relation",
                schema: "canonical_interpretation",
                columns: table => new
                {
                    snapshot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    relation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    resolution = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    confidence = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    rule_id = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    review_state = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    evidence_fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_interpretation_snapshot_relation", x => new { x.snapshot_id, x.relation_id });
                    table.ForeignKey(
                        name: "FK_interpretation_snapshot_relation_interpretation_snapshot_sn~",
                        column: x => x.snapshot_id,
                        principalSchema: "canonical_interpretation",
                        principalTable: "interpretation_snapshot",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_interpretation_snapshot_relation_relation_relation_id",
                        column: x => x.relation_id,
                        principalSchema: "canonical_interpretation",
                        principalTable: "relation",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_alias_entity_id",
                schema: "canonical_interpretation",
                table: "alias",
                column: "entity_id");

            migrationBuilder.CreateIndex(
                name: "IX_alias_scope_entity_id_normalized_alias",
                schema: "canonical_interpretation",
                table: "alias",
                columns: new[] { "scope", "entity_id", "normalized_alias" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_alias_scope_normalized_alias",
                schema: "canonical_interpretation",
                table: "alias",
                columns: new[] { "scope", "normalized_alias" });

            migrationBuilder.CreateIndex(
                name: "IX_entity_scope_entity_type_identity_key",
                schema: "canonical_interpretation",
                table: "entity",
                columns: new[] { "scope", "entity_type", "identity_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_interpretation_snapshot_scope_created_at",
                schema: "canonical_interpretation",
                table: "interpretation_snapshot",
                columns: new[] { "scope", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_interpretation_snapshot_entity_entity_id",
                schema: "canonical_interpretation",
                table: "interpretation_snapshot_entity",
                column: "entity_id");

            migrationBuilder.CreateIndex(
                name: "IX_interpretation_snapshot_relation_relation_id",
                schema: "canonical_interpretation",
                table: "interpretation_snapshot_relation",
                column: "relation_id");

            migrationBuilder.CreateIndex(
                name: "IX_occurrence_entity_id_source_snapshot_id_occurrence_key",
                schema: "canonical_interpretation",
                table: "occurrence",
                columns: new[] { "entity_id", "source_snapshot_id", "occurrence_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_occurrence_scope_source_snapshot_id",
                schema: "canonical_interpretation",
                table: "occurrence",
                columns: new[] { "scope", "source_snapshot_id" });

            migrationBuilder.CreateIndex(
                name: "IX_relation_scope_resolution_review_state",
                schema: "canonical_interpretation",
                table: "relation",
                columns: new[] { "scope", "resolution", "review_state" });

            migrationBuilder.CreateIndex(
                name: "IX_relation_scope_source_entity_id_target_entity_id_relation_t~",
                schema: "canonical_interpretation",
                table: "relation",
                columns: new[] { "scope", "source_entity_id", "target_entity_id", "relation_type" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_relation_scope_target_entity_id",
                schema: "canonical_interpretation",
                table: "relation",
                columns: new[] { "scope", "target_entity_id" });

            migrationBuilder.CreateIndex(
                name: "IX_relation_source_entity_id",
                schema: "canonical_interpretation",
                table: "relation",
                column: "source_entity_id");

            migrationBuilder.CreateIndex(
                name: "IX_relation_target_entity_id",
                schema: "canonical_interpretation",
                table: "relation",
                column: "target_entity_id");

            migrationBuilder.CreateIndex(
                name: "IX_review_decision_scope_sequence",
                schema: "canonical_interpretation",
                table: "review_decision",
                columns: new[] { "scope", "sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_review_decision_scope_target_key",
                schema: "canonical_interpretation",
                table: "review_decision",
                columns: new[] { "scope", "target_key" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "alias",
                schema: "canonical_interpretation");

            migrationBuilder.DropTable(
                name: "interpretation_snapshot_entity",
                schema: "canonical_interpretation");

            migrationBuilder.DropTable(
                name: "interpretation_snapshot_relation",
                schema: "canonical_interpretation");

            migrationBuilder.DropTable(
                name: "occurrence",
                schema: "canonical_interpretation");

            migrationBuilder.DropTable(
                name: "review_decision",
                schema: "canonical_interpretation");

            migrationBuilder.DropTable(
                name: "interpretation_snapshot",
                schema: "canonical_interpretation");

            migrationBuilder.DropTable(
                name: "relation",
                schema: "canonical_interpretation");

            migrationBuilder.DropTable(
                name: "entity",
                schema: "canonical_interpretation");
        }
    }
}
