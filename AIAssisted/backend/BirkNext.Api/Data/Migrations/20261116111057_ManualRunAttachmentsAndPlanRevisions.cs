using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BirkNext.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class ManualRunAttachmentsAndPlanRevisions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "manual_run_attachments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    FileName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    ContentType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Size = table.Column<long>(type: "bigint", nullable: false),
                    Sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Content = table.Column<byte[]>(type: "bytea", nullable: false),
                    AddedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    AddedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_manual_run_attachments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "test_plan_draft_revisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DraftId = table.Column<Guid>(type: "uuid", nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    PlanName = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    ContentJson = table.Column<string>(type: "text", nullable: false),
                    Reason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    SavedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    SavedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_test_plan_draft_revisions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_manual_run_attachments_ProjectKey_RunId",
                table: "manual_run_attachments",
                columns: new[] { "ProjectKey", "RunId" });

            migrationBuilder.CreateIndex(
                name: "ux_test_plan_draft_revisions_draft_revision",
                table: "test_plan_draft_revisions",
                columns: new[] { "DraftId", "Revision" },
                unique: true);

            // Follow-up after a run (defect status with history, files attached after completion) is the one change a completed or stopped run
            // accepts; the database still refuses anything else: state, outcome, completion, tester, start and steps must stay, every original
            // evidence item must remain, the number of defects must stay and the revision must advance by one.
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION manual_test_runs_final() RETURNS trigger LANGUAGE plpgsql AS $$
                DECLARE o jsonb; n jsonb;
                BEGIN
                    IF OLD."State" = 'InProgress' THEN RETURN NEW; END IF;
                    o := OLD."ContentJson"::jsonb; n := NEW."ContentJson"::jsonb;
                    IF NEW."State" = OLD."State" AND NEW."Outcome" = OLD."Outcome" AND NEW."CompletedAt" IS NOT DISTINCT FROM OLD."CompletedAt"
                       AND NEW."Tester" = OLD."Tester" AND NEW."StartedAt" = OLD."StartedAt" AND NEW."Revision" = OLD."Revision" + 1
                       AND (n -> 'steps') IS NOT DISTINCT FROM (o -> 'steps') AND n ->> 'outcome' IS NOT DISTINCT FROM o ->> 'outcome' AND n ->> 'state' IS NOT DISTINCT FROM o ->> 'state'
                       AND COALESCE(n -> 'evidence', '[]'::jsonb) @> COALESCE(o -> 'evidence', '[]'::jsonb)
                       AND jsonb_array_length(COALESCE(n -> 'defects', '[]'::jsonb)) = jsonb_array_length(COALESCE(o -> 'defects', '[]'::jsonb)) THEN
                        RETURN NEW;
                    END IF;
                    RAISE EXCEPTION 'manual test run % is % and final', OLD."Id", OLD."State";
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "manual_run_attachments");

            migrationBuilder.DropTable(
                name: "test_plan_draft_revisions");

            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION manual_test_runs_final() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF OLD."State" <> 'InProgress' THEN
                        RAISE EXCEPTION 'manual test run % is % and final', OLD."Id", OLD."State";
                    END IF;
                    RETURN NEW;
                END $$;
                """);
        }
    }
}
