using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Round9FieldExtractionProvenance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ai_enriched_at",
                table: "jobs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ai_enrichment_model",
                table: "jobs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "employment_type_source",
                table: "jobs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "salary_source",
                table: "jobs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "seniority_confidence",
                table: "jobs",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "seniority_source",
                table: "jobs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "work_mode_confidence",
                table: "jobs",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "work_mode_source",
                table: "jobs",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ai_enriched_at",
                table: "jobs");

            migrationBuilder.DropColumn(
                name: "ai_enrichment_model",
                table: "jobs");

            migrationBuilder.DropColumn(
                name: "employment_type_source",
                table: "jobs");

            migrationBuilder.DropColumn(
                name: "salary_source",
                table: "jobs");

            migrationBuilder.DropColumn(
                name: "seniority_confidence",
                table: "jobs");

            migrationBuilder.DropColumn(
                name: "seniority_source",
                table: "jobs");

            migrationBuilder.DropColumn(
                name: "work_mode_confidence",
                table: "jobs");

            migrationBuilder.DropColumn(
                name: "work_mode_source",
                table: "jobs");
        }
    }
}
