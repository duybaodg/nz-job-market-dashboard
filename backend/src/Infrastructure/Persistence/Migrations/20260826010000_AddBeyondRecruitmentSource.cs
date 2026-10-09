using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Persistence.Migrations;

[DbContext(typeof(ApplicationDbContext))]
[Migration("20260826010000_AddBeyondRecruitmentSource")]
public sealed class AddBeyondRecruitmentSource : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        INSERT INTO job_sources
            (id, name, base_url, method, parser, enabled, crawl_frequency_minutes,
             terms_checked_at, created_at, updated_at)
        VALUES
            ('bbbbbbbb-0006-0000-0000-000000000006', 'Beyond Recruitment',
             'https://www.beyondrecruitment.co.nz/jobs/it-transformation-and-digital',
             'Scrapling', 'beyond-recruitment', TRUE, 1440,
             '2026-08-23T00:00:00Z', NOW(), NOW())
        ON CONFLICT (name) DO NOTHING;
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        DELETE FROM job_sources WHERE id = 'bbbbbbbb-0006-0000-0000-000000000006';
        """);
}