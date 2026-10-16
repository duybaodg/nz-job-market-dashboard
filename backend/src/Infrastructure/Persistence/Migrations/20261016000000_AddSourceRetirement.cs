using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Persistence.Migrations;

/// <summary>
/// Distinguishes a source that was deliberately disabled in the registry from a
/// source that was removed from the registry altogether. The startup
/// <c>SourceRegistrySynchronizer</c> stamps <c>retired_at</c> on any row whose
/// name is no longer in <c>crawler/sources/sites.json</c>, which lets the
/// crawler-health report show only the current registry while still keeping the
/// row (and its historical jobs) for provenance.
/// </summary>
[DbContext(typeof(ApplicationDbContext))]
[Migration("20261016000000_AddSourceRetirement")]
public sealed class AddSourceRetirement : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTime>(
            name: "retired_at",
            table: "job_sources",
            type: "timestamp with time zone",
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "retired_at",
            table: "job_sources");
    }
}
