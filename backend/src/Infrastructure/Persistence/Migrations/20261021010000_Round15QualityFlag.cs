using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Persistence.Migrations;

/// <summary>
/// Round 15 (H-11): JobQualityGuard retains rejected source rows instead of
/// hard-deleting them. The flag and reason make the row reviewable; retention
/// (not this repair pass) remains the only feature that removes source rows.
/// Purely additive and reversible.
/// </summary>
[DbContext(typeof(ApplicationDbContext))]
[Migration("20261021010000_Round15QualityFlag")]
public sealed class Round15QualityFlag : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "is_quality_flagged",
            table: "jobs",
            type: "boolean",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<string>(
            name: "quality_flag_reason",
            table: "jobs",
            type: "text",
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "quality_flag_reason", table: "jobs");
        migrationBuilder.DropColumn(name: "is_quality_flagged", table: "jobs");
    }
}
