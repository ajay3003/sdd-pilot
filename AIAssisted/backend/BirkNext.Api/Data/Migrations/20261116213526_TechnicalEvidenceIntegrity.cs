using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BirkNext.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class TechnicalEvidenceIntegrity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OriginalFileName",
                table: "manual_run_attachments",
                type: "character varying(500)",
                maxLength: 500,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ScanStatus",
                table: "manual_run_attachments",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "not-scanned");

            migrationBuilder.AddColumn<string>(
                name: "WithdrawReason",
                table: "manual_run_attachments",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "WithdrawnAt",
                table: "manual_run_attachments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WithdrawnBy",
                table: "manual_run_attachments",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DefinitionJson",
                table: "integration_suite_runs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "RetestOfRunId",
                table: "integration_suite_runs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RetestStageId",
                table: "integration_suite_runs",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "specification_catalogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    Fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ArchiveSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    SnapshotId = table.Column<Guid>(type: "uuid", nullable: true),
                    CapturedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DocumentsJson = table.Column<string>(type: "jsonb", nullable: false),
                    EntriesJson = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_specification_catalogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "technical_review_runs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    ProjectKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    EnvironmentId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    EnvironmentName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    TargetUrl = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    SourceSnapshotId = table.Column<Guid>(type: "uuid", nullable: true),
                    GeneratedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Outcome = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    Checks = table.Column<int>(type: "integer", nullable: false),
                    Problems = table.Column<int>(type: "integer", nullable: false),
                    Summary = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    ReportSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ReportJson = table.Column<string>(type: "jsonb", nullable: false),
                    RecordedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_technical_review_runs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_integration_suite_runs_RetestOfRunId",
                table: "integration_suite_runs",
                column: "RetestOfRunId");

            migrationBuilder.CreateIndex(
                name: "ux_integration_suite_runs_run",
                table: "integration_suite_runs",
                column: "RunId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_specification_catalogs_content",
                table: "specification_catalogs",
                columns: new[] { "ProjectKey", "Fingerprint" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_specification_catalogs_revision",
                table: "specification_catalogs",
                columns: new[] { "ProjectKey", "Revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_technical_review_runs_ProjectKey_Kind_GeneratedAt",
                table: "technical_review_runs",
                columns: new[] { "ProjectKey", "Kind", "GeneratedAt" });

            migrationBuilder.CreateIndex(
                name: "ux_technical_review_runs_identity",
                table: "technical_review_runs",
                columns: new[] { "Kind", "EnvironmentId", "GeneratedAt" },
                unique: true);

            // Historical immutability enforced by the database, not only by the code: a pinned composite run record never changes; a stored
            // specification catalog and a stored specialist review are append-only; an evidence file changes only by withdrawal (content removed,
            // who/when/why recorded, everything else unchanged). Deletion stays possible for the explicit local data reset.
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION integration_suite_runs_pinned() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF OLD."DefinitionJson" IS NULL THEN RETURN NEW; END IF;
                    IF NEW."RunId" = OLD."RunId" AND NEW."SuiteId" = OLD."SuiteId" AND NEW."SuiteKey" = OLD."SuiteKey" AND NEW."Kind" = OLD."Kind"
                       AND NEW."StartedAt" = OLD."StartedAt" AND NEW."Initiator" = OLD."Initiator" AND NEW."TestIdsJson" = OLD."TestIdsJson"
                       AND NEW."PlanJson" IS NOT DISTINCT FROM OLD."PlanJson" AND NEW."DefinitionJson" = OLD."DefinitionJson"
                       AND NEW."RetestOfRunId" IS NOT DISTINCT FROM OLD."RetestOfRunId" AND NEW."RetestStageId" IS NOT DISTINCT FROM OLD."RetestStageId" THEN
                        RETURN NEW;
                    END IF;
                    RAISE EXCEPTION 'integration suite run % is a pinned historical run and cannot be changed', OLD."RunId";
                END $$;
                DROP TRIGGER IF EXISTS integration_suite_runs_no_rewrite ON integration_suite_runs;
                CREATE TRIGGER integration_suite_runs_no_rewrite BEFORE UPDATE ON integration_suite_runs FOR EACH ROW EXECUTE FUNCTION integration_suite_runs_pinned();

                CREATE OR REPLACE FUNCTION birknext_append_only() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION '% rows are append-only', TG_TABLE_NAME;
                END $$;
                DROP TRIGGER IF EXISTS specification_catalogs_append_only ON specification_catalogs;
                CREATE TRIGGER specification_catalogs_append_only BEFORE UPDATE ON specification_catalogs FOR EACH ROW EXECUTE FUNCTION birknext_append_only();
                DROP TRIGGER IF EXISTS technical_review_runs_append_only ON technical_review_runs;
                CREATE TRIGGER technical_review_runs_append_only BEFORE UPDATE ON technical_review_runs FOR EACH ROW EXECUTE FUNCTION birknext_append_only();

                CREATE OR REPLACE FUNCTION manual_run_attachments_withdraw_only() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF OLD."WithdrawnAt" IS NULL AND NEW."WithdrawnAt" IS NOT NULL AND NEW."WithdrawnBy" IS NOT NULL AND NEW."WithdrawReason" IS NOT NULL
                       AND octet_length(NEW."Content") = 0
                       AND NEW."Id" = OLD."Id" AND NEW."RunId" = OLD."RunId" AND NEW."ProjectKey" = OLD."ProjectKey" AND NEW."FileName" = OLD."FileName"
                       AND NEW."OriginalFileName" = OLD."OriginalFileName" AND NEW."ContentType" = OLD."ContentType" AND NEW."Size" = OLD."Size"
                       AND NEW."Sha256" = OLD."Sha256" AND NEW."AddedBy" = OLD."AddedBy" AND NEW."AddedAt" = OLD."AddedAt" AND NEW."ScanStatus" = OLD."ScanStatus" THEN
                        RETURN NEW;
                    END IF;
                    RAISE EXCEPTION 'evidence file % can only be withdrawn, not changed', OLD."Id";
                END $$;
                DROP TRIGGER IF EXISTS manual_run_attachments_no_rewrite ON manual_run_attachments;
                CREATE TRIGGER manual_run_attachments_no_rewrite BEFORE UPDATE ON manual_run_attachments FOR EACH ROW EXECUTE FUNCTION manual_run_attachments_withdraw_only();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS integration_suite_runs_no_rewrite ON integration_suite_runs; DROP FUNCTION IF EXISTS integration_suite_runs_pinned();
                DROP TRIGGER IF EXISTS specification_catalogs_append_only ON specification_catalogs;
                DROP TRIGGER IF EXISTS technical_review_runs_append_only ON technical_review_runs; DROP FUNCTION IF EXISTS birknext_append_only();
                DROP TRIGGER IF EXISTS manual_run_attachments_no_rewrite ON manual_run_attachments; DROP FUNCTION IF EXISTS manual_run_attachments_withdraw_only();
                """);

            migrationBuilder.DropTable(
                name: "specification_catalogs");

            migrationBuilder.DropTable(
                name: "technical_review_runs");

            migrationBuilder.DropIndex(
                name: "IX_integration_suite_runs_RetestOfRunId",
                table: "integration_suite_runs");

            migrationBuilder.DropIndex(
                name: "ux_integration_suite_runs_run",
                table: "integration_suite_runs");

            migrationBuilder.DropColumn(
                name: "OriginalFileName",
                table: "manual_run_attachments");

            migrationBuilder.DropColumn(
                name: "ScanStatus",
                table: "manual_run_attachments");

            migrationBuilder.DropColumn(
                name: "WithdrawReason",
                table: "manual_run_attachments");

            migrationBuilder.DropColumn(
                name: "WithdrawnAt",
                table: "manual_run_attachments");

            migrationBuilder.DropColumn(
                name: "WithdrawnBy",
                table: "manual_run_attachments");

            migrationBuilder.DropColumn(
                name: "DefinitionJson",
                table: "integration_suite_runs");

            migrationBuilder.DropColumn(
                name: "RetestOfRunId",
                table: "integration_suite_runs");

            migrationBuilder.DropColumn(
                name: "RetestStageId",
                table: "integration_suite_runs");
        }
    }
}
