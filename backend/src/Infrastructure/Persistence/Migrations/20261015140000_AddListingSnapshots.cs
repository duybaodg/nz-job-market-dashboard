using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Persistence.Migrations;

/// <summary>
/// Daily listing snapshots for historical analysis. Purely additive: two new
/// tables, no changes to existing job/source records, so existing rows are
/// preserved and <see cref="Down"/> restores the prior schema exactly.
/// </summary>
[DbContext(typeof(ApplicationDbContext))]
[Migration("20261015140000_AddListingSnapshots")]
public sealed class AddListingSnapshots : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "listing_snapshots",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                snapshot_date = table.Column<DateOnly>(type: "date", nullable: false),
                job_id = table.Column<Guid>(type: "uuid", nullable: false),
                source = table.Column<string>(type: "text", nullable: false),
                source_category = table.Column<string>(type: "text", nullable: false, defaultValue: "unknown"),
                content_hash = table.Column<string>(type: "text", nullable: false),
                title = table.Column<string>(type: "text", nullable: false),
                company = table.Column<string>(type: "text", nullable: true),
                hiring_employer = table.Column<string>(type: "text", nullable: true),
                agency_advertised = table.Column<bool>(type: "boolean", nullable: false),
                location = table.Column<string>(type: "text", nullable: true),
                region = table.Column<string>(type: "text", nullable: true),
                work_mode = table.Column<string>(type: "text", nullable: false),
                seniority = table.Column<string>(type: "text", nullable: false),
                employment_type = table.Column<string>(type: "text", nullable: false),
                role_family = table.Column<string>(type: "text", nullable: false),
                salary_min = table.Column<decimal>(type: "numeric", nullable: true),
                salary_max = table.Column<decimal>(type: "numeric", nullable: true),
                salary_currency = table.Column<string>(type: "text", nullable: false, defaultValue: "NZD"),
                salary_period = table.Column<string>(type: "text", nullable: false, defaultValue: "Unknown"),
                posted_date = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                first_seen_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                last_seen_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                is_active = table.Column<bool>(type: "boolean", nullable: false),
                captured_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_listing_snapshots", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "listing_snapshot_runs",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                snapshot_date = table.Column<DateOnly>(type: "date", nullable: false),
                captured_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                listing_count = table.Column<int>(type: "integer", nullable: false),
                active_count = table.Column<int>(type: "integer", nullable: false),
                source_count = table.Column<int>(type: "integer", nullable: false),
                identifiable_employer_count = table.Column<int>(type: "integer", nullable: false),
                source_coverage_json = table.Column<string>(type: "jsonb", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_listing_snapshot_runs", x => x.id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_listing_snapshots_first_seen_at",
            table: "listing_snapshots",
            column: "first_seen_at");

        migrationBuilder.CreateIndex(
            name: "IX_listing_snapshots_snapshot_date_content_hash",
            table: "listing_snapshots",
            columns: ["snapshot_date", "content_hash"],
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_listing_snapshot_runs_snapshot_date",
            table: "listing_snapshot_runs",
            column: "snapshot_date",
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "listing_snapshot_runs");
        migrationBuilder.DropTable(name: "listing_snapshots");
    }
}
