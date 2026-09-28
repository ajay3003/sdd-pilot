using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BirkNext.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSecurityClassificationEvidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "security_classification_evidence",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    environment_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    json = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_security_classification_evidence", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_security_classification_environment_kind_created",
                table: "security_classification_evidence",
                columns: new[] { "environment_id", "kind", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "security_classification_evidence");
        }
    }
}
