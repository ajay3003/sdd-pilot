using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BirkNext.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSystemGuideRevisions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "system_guide_revisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GuideKey = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    State = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ContentFingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ContentJson = table.Column<string>(type: "text", nullable: false),
                    SourceBasisJson = table.Column<string>(type: "text", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ChangeSummary = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    SubmittedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    SubmittedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ApprovedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ApprovedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    PublishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    SupersededByRevision = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_system_guide_revisions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_system_guide_revisions_GuideKey_Revision",
                table: "system_guide_revisions",
                columns: new[] { "GuideKey", "Revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_system_guide_revisions_open",
                table: "system_guide_revisions",
                column: "GuideKey",
                unique: true,
                filter: "\"State\" IN ('Draft', 'InReview')");

            migrationBuilder.CreateIndex(
                name: "IX_system_guide_revisions_published",
                table: "system_guide_revisions",
                column: "GuideKey",
                unique: true,
                filter: "\"State\" = 'Published'");

            // A revision beyond Draft is immutable content: the database refuses to change its content, source basis or identity; a Superseded
            // revision is final, and a Published one can only become Superseded. DELETE stays possible (Local Data Reset).
            if (migrationBuilder.ActiveProvider == "Npgsql.EntityFrameworkCore.PostgreSQL")
            {
                migrationBuilder.Sql("""
                    CREATE OR REPLACE FUNCTION system_guide_revisions_guard() RETURNS trigger LANGUAGE plpgsql AS $$
                    BEGIN
                        IF NEW."GuideKey" IS DISTINCT FROM OLD."GuideKey" OR NEW."Revision" IS DISTINCT FROM OLD."Revision" THEN
                            RAISE EXCEPTION 'guide revision identity is immutable (% r%)', OLD."GuideKey", OLD."Revision";
                        END IF;
                        IF OLD."State" <> 'Draft' AND (NEW."ContentJson" IS DISTINCT FROM OLD."ContentJson"
                            OR NEW."ContentFingerprint" IS DISTINCT FROM OLD."ContentFingerprint" OR NEW."SourceBasisJson" IS DISTINCT FROM OLD."SourceBasisJson") THEN
                            RAISE EXCEPTION 'guide revision % r% is immutable beyond Draft', OLD."GuideKey", OLD."Revision";
                        END IF;
                        IF OLD."State" = 'Superseded' THEN
                            RAISE EXCEPTION 'superseded guide revision % r% is final', OLD."GuideKey", OLD."Revision";
                        END IF;
                        IF OLD."State" = 'Published' AND NEW."State" NOT IN ('Published', 'Superseded') THEN
                            RAISE EXCEPTION 'published guide revision % r% can only be superseded', OLD."GuideKey", OLD."Revision";
                        END IF;
                        RETURN NEW;
                    END;
                    $$;
                    CREATE TRIGGER system_guide_revisions_no_rewrite BEFORE UPDATE ON system_guide_revisions
                        FOR EACH ROW EXECUTE FUNCTION system_guide_revisions_guard();
                    """);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider == "Npgsql.EntityFrameworkCore.PostgreSQL")
                migrationBuilder.Sql("DROP TRIGGER IF EXISTS system_guide_revisions_no_rewrite ON system_guide_revisions; DROP FUNCTION IF EXISTS system_guide_revisions_guard();");
            migrationBuilder.DropTable(
                name: "system_guide_revisions");
        }
    }
}
