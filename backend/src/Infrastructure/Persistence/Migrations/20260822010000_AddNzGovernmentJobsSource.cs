using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Persistence.Migrations;

[DbContext(typeof(ApplicationDbContext))]
[Migration("20260822010000_AddNzGovernmentJobsSource")]
public sealed class AddNzGovernmentJobsSource : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        INSERT INTO job_sources
            (id, name, base_url, method, parser, enabled, crawl_frequency_minutes,
             terms_checked_at, created_at, updated_at)
        VALUES
            ('cccccccc-0001-0000-0000-000000000001', 'NZ Government Jobs',
             'https://jobs.govt.nz/jobtools/jncustomsearch.searchResults?in_organid=16563&in_jobDate=All&in_multi01=%22IT%20%26%20computing%22&in_multi01_id=1802&in_orderby=dateinput%20desc',
             'Scrapling', 'govt-nz', FALSE, 1440,
             '2026-08-22T00:00:00Z', '2026-08-22T00:00:00Z', '2026-08-22T00:00:00Z')
        ON CONFLICT (name) DO NOTHING;
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        DELETE FROM job_sources WHERE id = 'cccccccc-0001-0000-0000-000000000001';
        """);
}
