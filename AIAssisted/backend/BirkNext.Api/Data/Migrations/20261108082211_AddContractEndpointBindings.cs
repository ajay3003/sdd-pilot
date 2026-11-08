using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BirkNext.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddContractEndpointBindings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "contract_endpoint_bindings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ContractId = table.Column<Guid>(type: "uuid", nullable: false),
                    EnvironmentId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    BasePath = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    GraphQlPath = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    Authentication = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_contract_endpoint_bindings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_contract_endpoint_bindings_ContractId_EnvironmentId",
                table: "contract_endpoint_bindings",
                columns: new[] { "ContractId", "EnvironmentId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "contract_endpoint_bindings");
        }
    }
}
