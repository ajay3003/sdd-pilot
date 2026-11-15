using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BirkNext.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class ManualRunPagingAndSingleInProgress : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_manual_test_runs_project_started",
                table: "manual_test_runs",
                columns: new[] { "ProjectKey", "StartedAt", "Id" });

            // One run in progress per case. A database that already holds two runs in progress for one case (the race this closes) keeps
            // them as recorded: the index is then not created and a notice says so, instead of failing startup or rewriting history.
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM manual_test_runs WHERE "State" = 'InProgress' GROUP BY "ProjectKey", "DraftId", "CaseId" HAVING count(*) > 1) THEN
                        RAISE NOTICE 'ux_manual_test_runs_one_in_progress not created: a case already has more than one run in progress';
                    ELSE
                        CREATE UNIQUE INDEX ux_manual_test_runs_one_in_progress ON manual_test_runs ("ProjectKey", "DraftId", "CaseId") WHERE "State" = 'InProgress';
                    END IF;
                END $$;
                """);

            // A completed or stopped run is final (the service refuses changes with 409 run-final); the database refuses an UPDATE of it too, as it
            // does for test plan snapshots and execution manifests. DELETE stays possible (Local Data Reset).
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION manual_test_runs_final() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF OLD."State" <> 'InProgress' THEN
                        RAISE EXCEPTION 'manual test run % is % and final', OLD."Id", OLD."State";
                    END IF;
                    RETURN NEW;
                END $$;
                DROP TRIGGER IF EXISTS manual_test_runs_no_rewrite ON manual_test_runs;
                CREATE TRIGGER manual_test_runs_no_rewrite BEFORE UPDATE ON manual_test_runs FOR EACH ROW EXECUTE FUNCTION manual_test_runs_final();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_manual_test_runs_project_started",
                table: "manual_test_runs");

            migrationBuilder.Sql("DROP INDEX IF EXISTS ux_manual_test_runs_one_in_progress;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS manual_test_runs_no_rewrite ON manual_test_runs; DROP FUNCTION IF EXISTS manual_test_runs_final();");
        }
    }
}
