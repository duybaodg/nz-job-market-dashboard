using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Persistence.Migrations;

[DbContext(typeof(ApplicationDbContext))]
[Migration("20260826080000_AddLinkedInSource")]
public sealed class AddLinkedInSource : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        INSERT INTO job_sources
            (id, name, base_url, method, parser, enabled, crawl_frequency_minutes,
             terms_checked_at, created_at, updated_at)
        VALUES
            ('bbbbbbbb-0013-0000-0000-000000000013', 'LinkedIn',
             'https://www.linkedin.com/jobs/',
             'Scrapling', 'linkedin', TRUE, 1440,
             NOW(), NOW(), NOW())
        ON CONFLICT (name) DO NOTHING;
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        DELETE FROM job_sources WHERE id = 'bbbbbbbb-0013-0000-0000-000000000013';
        """);
}