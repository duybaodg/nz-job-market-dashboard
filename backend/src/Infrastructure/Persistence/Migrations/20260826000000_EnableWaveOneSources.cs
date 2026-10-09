using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Persistence.Migrations;

[DbContext(typeof(ApplicationDbContext))]
[Migration("20260826000000_EnableWaveOneSources")]
public sealed class EnableWaveOneSources : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        UPDATE job_sources SET enabled = TRUE, updated_at = NOW()
        WHERE name IN ('Absolute IT', 'Talent Army');
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        UPDATE job_sources SET enabled = FALSE, updated_at = NOW()
        WHERE name IN ('Absolute IT', 'Talent Army');
        """);
}