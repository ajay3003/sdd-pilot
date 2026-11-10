using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BirkNext.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddContractProjectIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "IdentityState",
                table: "api_contracts",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProjectKey",
                table: "api_contracts",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceProjectKey",
                table: "api_contract_versions",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "contract_review_decisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    ContractId = table.Column<Guid>(type: "uuid", nullable: true),
                    ProjectKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    SubjectKey = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    SnapshotId = table.Column<Guid>(type: "uuid", nullable: true),
                    Detail = table.Column<string>(type: "text", nullable: false),
                    DecidedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    DecidedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_contract_review_decisions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_api_contracts_ProjectKey",
                table: "api_contracts",
                column: "ProjectKey");

            migrationBuilder.CreateIndex(
                name: "IX_api_contract_versions_SourceProjectKey_SourcePath",
                table: "api_contract_versions",
                columns: new[] { "SourceProjectKey", "SourcePath" });

            migrationBuilder.CreateIndex(
                name: "IX_contract_review_decisions_ContractId",
                table: "contract_review_decisions",
                column: "ContractId");

            migrationBuilder.CreateIndex(
                name: "IX_contract_review_decisions_Kind_SubjectKey",
                table: "contract_review_decisions",
                columns: new[] { "Kind", "SubjectKey" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "contract_review_decisions");

            migrationBuilder.DropIndex(
                name: "IX_api_contracts_ProjectKey",
                table: "api_contracts");

            migrationBuilder.DropIndex(
                name: "IX_api_contract_versions_SourceProjectKey_SourcePath",
                table: "api_contract_versions");

            migrationBuilder.DropColumn(
                name: "IdentityState",
                table: "api_contracts");

            migrationBuilder.DropColumn(
                name: "ProjectKey",
                table: "api_contracts");

            migrationBuilder.DropColumn(
                name: "SourceProjectKey",
                table: "api_contract_versions");
        }
    }
}
