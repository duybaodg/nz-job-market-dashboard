using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Persistence.Migrations;

/// <summary>
/// Applies the robots.txt review to the seeded source registry. Sites whose
/// robots.txt disallows automated crawling are disabled until a human reviews
/// their terms of service, so the worker does not generate a failing crawl run
/// on every cycle. Regenerated from the registry's <c>robotsAllows</c> metadata.
/// </summary>
[DbContext(typeof(ApplicationDbContext))]
[Migration("20261011010000_ApplyRobotsReview")]
public sealed class ApplyRobotsReview : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        UPDATE job_sources SET enabled = FALSE, updated_at = NOW()
        WHERE name IN ('Health NZ Careers', 'Ryman Healthcare');
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        UPDATE job_sources SET enabled = TRUE, updated_at = NOW()
        WHERE name IN ('Health NZ Careers', 'Ryman Healthcare');
        """);
}
