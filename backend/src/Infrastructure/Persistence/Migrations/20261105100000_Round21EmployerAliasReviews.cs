using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Persistence.Migrations;

/// <summary>
/// Round 21 P3: auditable human decisions for unmatched advertised employer
/// names. Grouping happens in application code and never writes identity; only
/// an explicit decision row (and, for an explicit match, an alias) changes
/// resolution. Reversible: <see cref="Down"/> drops only this table.
/// </summary>
[DbContext(typeof(ApplicationDbContext))]
[Migration("20261105100000_Round21EmployerAliasReviews")]
public sealed class Round21EmployerAliasReviews : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "employer_alias_reviews",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                normalized_name = table.Column<string>(type: "text", nullable: false),
                advertised_name = table.Column<string>(type: "text", nullable: false),
                decision = table.Column<string>(type: "text", nullable: false),
                canonical_employer_id = table.Column<Guid>(type: "uuid", nullable: true),
                group_key = table.Column<string>(type: "text", nullable: false),
                reason = table.Column<string>(type: "text", nullable: true),
                reviewer = table.Column<string>(type: "text", nullable: false),
                created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                reviewed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_employer_alias_reviews", x => x.id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_employer_alias_reviews_group_key",
            table: "employer_alias_reviews",
            column: "group_key");

        migrationBuilder.CreateIndex(
            name: "IX_employer_alias_reviews_normalized_name",
            table: "employer_alias_reviews",
            column: "normalized_name",
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "employer_alias_reviews");
    }
}
