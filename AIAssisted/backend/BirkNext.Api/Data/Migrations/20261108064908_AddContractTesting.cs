using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BirkNext.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddContractTesting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DefinitionRef",
                table: "execution_calls",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "api_contract_versions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ContractId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    Sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Source = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    SourceLabel = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Format = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    DocumentText = table.Column<string>(type: "text", nullable: false),
                    ContractJson = table.Column<string>(type: "text", nullable: false),
                    OperationCount = table.Column<int>(type: "integer", nullable: false),
                    ImportedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_api_contract_versions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "api_contracts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Protocol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_api_contracts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "contract_tests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ContractId = table.Column<Guid>(type: "uuid", nullable: false),
                    Operation = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    DocumentJson = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_contract_tests", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_execution_calls_DefinitionRef",
                table: "execution_calls",
                column: "DefinitionRef");

            migrationBuilder.CreateIndex(
                name: "IX_api_contract_versions_ContractId_Version",
                table: "api_contract_versions",
                columns: new[] { "ContractId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_api_contracts_Key",
                table: "api_contracts",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_contract_tests_ContractId_Operation",
                table: "contract_tests",
                columns: new[] { "ContractId", "Operation" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "api_contract_versions");

            migrationBuilder.DropTable(
                name: "api_contracts");

            migrationBuilder.DropTable(
                name: "contract_tests");

            migrationBuilder.DropIndex(
                name: "IX_execution_calls_DefinitionRef",
                table: "execution_calls");

            migrationBuilder.DropColumn(
                name: "DefinitionRef",
                table: "execution_calls");
        }
    }
}
