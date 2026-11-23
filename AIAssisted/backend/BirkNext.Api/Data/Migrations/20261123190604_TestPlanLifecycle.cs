using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BirkNext.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class TestPlanLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Lifecycle",
                table: "test_plan_drafts",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Active");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LifecycleChangedAt",
                table: "test_plan_drafts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LifecycleChangedBy",
                table: "test_plan_drafts",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LifecycleReason",
                table: "test_plan_drafts",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Lifecycle",
                table: "test_plan_drafts");

            migrationBuilder.DropColumn(
                name: "LifecycleChangedAt",
                table: "test_plan_drafts");

            migrationBuilder.DropColumn(
                name: "LifecycleChangedBy",
                table: "test_plan_drafts");

            migrationBuilder.DropColumn(
                name: "LifecycleReason",
                table: "test_plan_drafts");
        }
    }
}
