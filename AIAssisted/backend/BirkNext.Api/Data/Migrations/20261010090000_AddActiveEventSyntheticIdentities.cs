using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BirkNext.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddActiveEventSyntheticIdentities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "active_event_synthetic_identities",
                columns: table => new
                {
                    EnvironmentId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Scope = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Value = table.Column<long>(type: "bigint", nullable: false),
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExtensionId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ReservedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_active_event_synthetic_identities", x => new { x.EnvironmentId, x.Scope, x.Value });
                });

            migrationBuilder.CreateIndex(
                name: "IX_active_event_synthetic_identities_RunId",
                table: "active_event_synthetic_identities",
                column: "RunId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "active_event_synthetic_identities");
        }
    }
}
