using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BirkNext.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddContractVersionSourceProvenance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SourcePath",
                table: "api_contract_versions",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SourceSnapshotId",
                table: "api_contract_versions",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_api_contract_versions_SourceSnapshotId_SourcePath",
                table: "api_contract_versions",
                columns: new[] { "SourceSnapshotId", "SourcePath" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_api_contract_versions_SourceSnapshotId_SourcePath",
                table: "api_contract_versions");

            migrationBuilder.DropColumn(
                name: "SourcePath",
                table: "api_contract_versions");

            migrationBuilder.DropColumn(
                name: "SourceSnapshotId",
                table: "api_contract_versions");
        }
    }
}
