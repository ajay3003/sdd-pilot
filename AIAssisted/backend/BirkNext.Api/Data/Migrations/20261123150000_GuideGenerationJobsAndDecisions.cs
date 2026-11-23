using BirkNext.Api.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BirkNext.Api.Data.Migrations;

/// <summary>
/// Durable System Understanding generation (2026-11-23): one job per guide, source snapshot and generator version (survives restarts, keeps its
/// diagnostics, can be retried), and reviewer decisions / supporting evidence / staleness on source candidates. Additive; older rows load unchanged.
/// </summary>
public partial class GuideGenerationJobsAndDecisions : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable("system_guide_generation_jobs", table => new
        {
            Id = table.Column<Guid>(type: "uuid", nullable: false),
            GuideKey = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
            ApplicationKey = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
            SnapshotId = table.Column<Guid>(type: "uuid", nullable: false),
            SnapshotLabel = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
            Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
            Attempts = table.Column<int>(type: "integer", nullable: false),
            LastError = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
            GeneratorVersion = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
            CandidateId = table.Column<Guid>(type: "uuid", nullable: true),
            CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
            UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
        }, constraints: table => table.PrimaryKey("PK_system_guide_generation_jobs", x => x.Id));
        migrationBuilder.CreateIndex("IX_system_guide_generation_jobs_GuideKey_SnapshotId_GeneratorV~", "system_guide_generation_jobs",
            new[] { "GuideKey", "SnapshotId", "GeneratorVersion" }, unique: true);
        migrationBuilder.CreateIndex("IX_system_guide_generation_jobs_Status_CreatedAt", "system_guide_generation_jobs", new[] { "Status", "CreatedAt" });

        migrationBuilder.AddColumn<string>("DecisionsJson", "system_guide_candidates", type: "text", nullable: true);
        migrationBuilder.AddColumn<string>("SupportingJson", "system_guide_candidates", type: "text", nullable: true);
        migrationBuilder.AddColumn<Guid>("SupersededBy", "system_guide_candidates", type: "uuid", nullable: true);
        migrationBuilder.AddColumn<string>("SourceLabel", "system_guide_candidates", type: "character varying(300)", maxLength: 300, nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn("SourceLabel", "system_guide_candidates");
        migrationBuilder.DropColumn("SupersededBy", "system_guide_candidates");
        migrationBuilder.DropColumn("SupportingJson", "system_guide_candidates");
        migrationBuilder.DropColumn("DecisionsJson", "system_guide_candidates");
        migrationBuilder.DropTable("system_guide_generation_jobs");
    }
}
