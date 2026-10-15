using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Persistence.Migrations;

[DbContext(typeof(ApplicationDbContext))]
[Migration("20261015120000_AddSalaryProvenance")]
public sealed class AddSalaryProvenance : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "salary_text",
            table: "jobs",
            type: "text",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "salary_currency",
            table: "jobs",
            type: "text",
            nullable: false,
            defaultValue: "NZD");

        migrationBuilder.AddColumn<string>(
            name: "salary_period",
            table: "jobs",
            type: "text",
            nullable: false,
            defaultValue: "Unknown");

        migrationBuilder.AddColumn<bool>(
            name: "salary_questionable",
            table: "jobs",
            type: "boolean",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<string>(
            name: "salary_questionable_reason",
            table: "jobs",
            type: "text",
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "salary_text", table: "jobs");
        migrationBuilder.DropColumn(name: "salary_currency", table: "jobs");
        migrationBuilder.DropColumn(name: "salary_period", table: "jobs");
        migrationBuilder.DropColumn(name: "salary_questionable", table: "jobs");
        migrationBuilder.DropColumn(name: "salary_questionable_reason", table: "jobs");
    }
}
