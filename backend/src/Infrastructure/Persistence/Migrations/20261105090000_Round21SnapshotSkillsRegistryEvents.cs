using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Persistence.Migrations;

/// <summary>
/// Round 21 P0: additive snapshot capture and a dated registry-event log.
///
/// <para>
/// <c>listing_snapshots</c> gains <c>skills_captured</c> and <c>skills_json</c>.
/// Existing rows keep <c>skills_captured = false</c>, which means "unknown",
/// not "no skills"; nothing is backfilled. The snapshot writer only sets these
/// columns on future captures.
/// </para>
/// <para>
/// <c>registry_events</c> is append-only. The migration seeds exactly one
/// truthful baseline event per current source, dated 2026-11-05. Changes before
/// that date stay unknown. <see cref="Down"/> removes only the two new columns
/// and the new table, so the prior schema and all pre-existing rows survive.
/// </para>
/// </summary>
[DbContext(typeof(ApplicationDbContext))]
[Migration("20261105090000_Round21SnapshotSkillsRegistryEvents")]
public sealed class Round21SnapshotSkillsRegistryEvents : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "skills_captured",
            table: "listing_snapshots",
            type: "boolean",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<string>(
            name: "skills_json",
            table: "listing_snapshots",
            type: "jsonb",
            nullable: false,
            defaultValue: "[]");

        migrationBuilder.CreateTable(
            name: "registry_events",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                source_name = table.Column<string>(type: "text", nullable: false),
                event_type = table.Column<string>(type: "text", nullable: false),
                enabled = table.Column<bool>(type: "boolean", nullable: false),
                retired_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                event_date = table.Column<DateOnly>(type: "date", nullable: false),
                recorded_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                reason = table.Column<string>(type: "text", nullable: false),
                actor = table.Column<string>(type: "text", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_registry_events", x => x.id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_registry_events_event_date",
            table: "registry_events",
            column: "event_date");

        migrationBuilder.CreateIndex(
            name: "IX_registry_events_source_name_event_date",
            table: "registry_events",
            columns: ["source_name", "event_date"]);

        // Truthful baseline only. This records the state the registry actually
        // held when the migration ran; it does not invent a history.
        migrationBuilder.Sql(
            """
            INSERT INTO registry_events
                (id, source_name, event_type, enabled, retired_at, event_date, recorded_at_utc, reason, actor)
            SELECT
                gen_random_uuid(),
                name,
                'state-as-of',
                enabled,
                retired_at,
                DATE '2026-11-05',
                now(),
                'Baseline: registry state as of 2026-11-05, recorded from the current job_sources rows. Changes before this date are unknown and are not reconstructed.',
                'system:migration'
            FROM job_sources;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "registry_events");
        migrationBuilder.DropColumn(name: "skills_json", table: "listing_snapshots");
        migrationBuilder.DropColumn(name: "skills_captured", table: "listing_snapshots");
    }
}
