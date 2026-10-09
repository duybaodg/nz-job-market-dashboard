using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Persistence.Migrations;

[DbContext(typeof(ApplicationDbContext))]
[Migration("20260822000000_AddCrawlSourceScheduling")]
public sealed class AddCrawlSourceScheduling : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTime>(
            name: "last_crawled_at",
            table: "job_sources",
            type: "timestamp with time zone",
            nullable: true);
        migrationBuilder.AddColumn<string>(
            name: "parser",
            table: "job_sources",
            type: "text",
            nullable: false,
            defaultValue: "generic");
        migrationBuilder.AddColumn<DateTime>(
            name: "terms_checked_at",
            table: "job_sources",
            type: "timestamp with time zone",
            nullable: true);
        migrationBuilder.Sql("UPDATE job_sources SET enabled = FALSE WHERE id = 'bbbbbbbb-0001-0000-0000-000000000001';");
        migrationBuilder.CreateIndex(
            name: "IX_job_sources_name",
            table: "job_sources",
            column: "name",
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(name: "IX_job_sources_name", table: "job_sources");
        migrationBuilder.DropColumn(name: "last_crawled_at", table: "job_sources");
        migrationBuilder.DropColumn(name: "parser", table: "job_sources");
        migrationBuilder.DropColumn(name: "terms_checked_at", table: "job_sources");
        migrationBuilder.Sql("UPDATE job_sources SET enabled = TRUE WHERE id = 'bbbbbbbb-0001-0000-0000-000000000001';");
    }
}
