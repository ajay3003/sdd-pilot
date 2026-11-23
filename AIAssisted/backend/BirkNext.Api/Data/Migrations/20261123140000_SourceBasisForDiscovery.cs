using BirkNext.Api.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BirkNext.Api.Data.Migrations;

/// <summary>
/// Source-driven test planning (2026-11-23): the imported specification's parsed evidence per catalog revision (discovery reads it instead of a
/// browser copy), and the source basis of each stored discovery result (application, current vs historical, basis fingerprint) so the current
/// result is restored and kept independently of historical inspections. Additive and nullable: older rows keep loading unchanged.
/// </summary>
public partial class SourceBasisForDiscovery : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>("EvidenceJson", "specification_catalogs", type: "jsonb", nullable: true);
        migrationBuilder.AddColumn<string>("ApplicationKey", "scenario_discovery_results", type: "character varying(300)", maxLength: 300, nullable: true);
        migrationBuilder.AddColumn<string>("BasisKind", "scenario_discovery_results", type: "character varying(20)", maxLength: 20, nullable: true);
        migrationBuilder.AddColumn<string>("BasisFingerprint", "scenario_discovery_results", type: "character varying(64)", maxLength: 64, nullable: true);
        migrationBuilder.CreateIndex("IX_scenario_discovery_results_ApplicationKey_DiscoveredAt", "scenario_discovery_results", new[] { "ApplicationKey", "DiscoveredAt" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex("IX_scenario_discovery_results_ApplicationKey_DiscoveredAt", "scenario_discovery_results");
        migrationBuilder.DropColumn("BasisFingerprint", "scenario_discovery_results");
        migrationBuilder.DropColumn("BasisKind", "scenario_discovery_results");
        migrationBuilder.DropColumn("ApplicationKey", "scenario_discovery_results");
        migrationBuilder.DropColumn("EvidenceJson", "specification_catalogs");
    }
}
