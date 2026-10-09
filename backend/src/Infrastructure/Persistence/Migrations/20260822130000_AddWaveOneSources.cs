using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Persistence.Migrations;

[DbContext(typeof(ApplicationDbContext))]
[Migration("20260822130000_AddWaveOneSources")]
public sealed class AddWaveOneSources : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            INSERT INTO job_sources
                (id, name, base_url, method, parser, enabled, crawl_frequency_minutes,
                 terms_checked_at, created_at, updated_at)
            VALUES
                ('bbbbbbbb-0003-0000-0000-000000000003', 'Absolute IT',
                 'https://absoluteit.co.nz/it-jobs/', 'Scrapling', 'absolute-it', FALSE, 1440,
                 '2026-08-22T00:00:00Z', NOW(), NOW()),
                ('bbbbbbbb-0004-0000-0000-000000000004', 'Talent Army',
                 'https://talent.army/job-board', 'Scrapling', 'talent-army', FALSE, 1440,
                 '2026-08-22T00:00:00Z', NOW(), NOW())
            ON CONFLICT (name) DO NOTHING;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DELETE FROM job_sources
            WHERE id IN (
                'bbbbbbbb-0003-0000-0000-000000000003',
                'bbbbbbbb-0004-0000-0000-000000000004'
            );
            """);
    }
}
