using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BirkNext.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddParquetRuleSets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "parquet_rule_sets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ContractKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ContractVersion = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    RulesJson = table.Column<string>(type: "text", nullable: false),
                    ApprovedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ApprovedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_parquet_rule_sets", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_parquet_rule_sets_ProjectKey_ContractKey_ContractVersion_Ve~",
                table: "parquet_rule_sets",
                columns: new[] { "ProjectKey", "ContractKey", "ContractVersion", "Version" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "parquet_rule_sets");
        }
    }
}
