using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BirkNext.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkspaceTransfer : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "imported_historical_records",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    OriginalId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    OriginInstanceId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    OriginalProject = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    OriginalEnvironmentId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    EnvironmentId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    OriginalTimestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ImportedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DocumentJson = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_imported_historical_records", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "workspace_transfer_operations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    State = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Actor = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Scope = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    Summary = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    PackageSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    PlanFingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    DestinationFingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    PlanJson = table.Column<string>(type: "text", nullable: true),
                    ResultJson = table.Column<string>(type: "text", nullable: true),
                    Error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    StagedPackage = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workspace_transfer_operations", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_imported_historical_records_OperationId",
                table: "imported_historical_records",
                column: "OperationId");

            migrationBuilder.CreateIndex(
                name: "IX_imported_historical_records_OriginInstanceId_Kind_OriginalId",
                table: "imported_historical_records",
                columns: new[] { "OriginInstanceId", "Kind", "OriginalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_workspace_transfer_operations_CreatedAt",
                table: "workspace_transfer_operations",
                column: "CreatedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "imported_historical_records");

            migrationBuilder.DropTable(
                name: "workspace_transfer_operations");
        }
    }
}
