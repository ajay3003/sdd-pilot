using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BirkNext.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSourceRepositoryAssociations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "source_repository_associations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    repository_key_a = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    repository_a = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    repository_key_b = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    repository_b = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    declared_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    declared_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_source_repository_associations", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_source_repository_associations_keys",
                table: "source_repository_associations",
                columns: new[] { "repository_key_a", "repository_key_b" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "source_repository_associations");
        }
    }
}
