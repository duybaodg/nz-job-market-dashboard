using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Persistence.Migrations;

[DbContext(typeof(ApplicationDbContext))]
[Migration("20260826050000_AddDigitalGarageSource")]
public sealed class AddDigitalGarageSource : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        INSERT INTO job_sources
            (id, name, base_url, method, parser, enabled, crawl_frequency_minutes,
             terms_checked_at, created_at, updated_at)
        VALUES
            ('bbbbbbbb-0010-0000-0000-000000000010', 'Digital Garage',
             'https://digitalgarage.co.nz/jobs',
             'Scrapling', 'digital-garage', TRUE, 1440,
             '2026-08-23T00:00:00Z', NOW(), NOW())
        ON CONFLICT (name) DO NOTHING;
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        DELETE FROM job_sources WHERE id = 'bbbbbbbb-0010-0000-0000-000000000010';
        """);
}