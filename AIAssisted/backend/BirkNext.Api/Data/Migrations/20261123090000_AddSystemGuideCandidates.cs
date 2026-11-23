using BirkNext.Api.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BirkNext.Api.Data.Migrations;

public partial class AddSystemGuideCandidates : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable("system_guide_candidates", table => new
        {
            Id = table.Column<Guid>(type: "uuid", nullable: false),
            GuideKey = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
            SourceSnapshotId = table.Column<Guid>(type: "uuid", nullable: false),
            SourceFingerprint = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
            InputFingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
            ContentFingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
            ContentJson = table.Column<string>(type: "text", nullable: false),
            ChangesJson = table.Column<string>(type: "text", nullable: false),
            EvidenceJson = table.Column<string>(type: "text", nullable: false),
            Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
            CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
            GeneratorVersion = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false)
        }, constraints: table => table.PrimaryKey("PK_system_guide_candidates", x => x.Id));

        migrationBuilder.CreateIndex("IX_system_guide_candidates_GuideKey_InputFingerprint", "system_guide_candidates", new[] { "GuideKey", "InputFingerprint" }, unique: true);
        migrationBuilder.CreateIndex("IX_system_guide_candidates_GuideKey_CreatedAt", "system_guide_candidates", new[] { "GuideKey", "CreatedAt" });
    }

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropTable("system_guide_candidates");
}
