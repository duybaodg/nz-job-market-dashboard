using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Persistence.Migrations;

/// <summary>
/// Round 15 (M-1/H-3): index the retention predicates on the two largest
/// tables, and add the race-safe half of the per-source crawl lock. The partial
/// unique index allows any number of finished runs but at most one
/// <c>status='Running'</c> row per source. The migration is reversible.
/// </summary>
[DbContext(typeof(ApplicationDbContext))]
[Migration("20261021000000_Round15IndexesAndRunLock")]
public sealed class Round15IndexesAndRunLock : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateIndex(
            name: "IX_raw_job_pages_crawled_at",
            table: "raw_job_pages",
            column: "crawled_at");

        migrationBuilder.CreateIndex(
            name: "IX_crawl_runs_started_at",
            table: "crawl_runs",
            column: "started_at");

        migrationBuilder.CreateIndex(
            name: "IX_crawl_runs_source_running",
            table: "crawl_runs",
            column: "source",
            unique: true,
            filter: "\"status\" = 'Running'");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_crawl_runs_source_running",
            table: "crawl_runs");

        migrationBuilder.DropIndex(
            name: "IX_crawl_runs_started_at",
            table: "crawl_runs");

        migrationBuilder.DropIndex(
            name: "IX_raw_job_pages_crawled_at",
            table: "raw_job_pages");
    }
}
