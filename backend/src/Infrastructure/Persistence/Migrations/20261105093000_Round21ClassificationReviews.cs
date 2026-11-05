using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Persistence.Migrations;

/// <summary>
/// Round 21 P2: append/update-only classification review decisions. The table
/// has no foreign key to <c>jobs</c> because reviews must survive the 20-day job
/// retention purge as evidence. Reversible: <see cref="Down"/> drops only this
/// table.
/// </summary>
[DbContext(typeof(ApplicationDbContext))]
[Migration("20261105093000_Round21ClassificationReviews")]
public sealed class Round21ClassificationReviews : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "classification_reviews",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                job_id = table.Column<Guid>(type: "uuid", nullable: false),
                source = table.Column<string>(type: "text", nullable: false),
                title = table.Column<string>(type: "text", nullable: false),
                predicted_role_family = table.Column<string>(type: "text", nullable: false),
                rule_version = table.Column<string>(type: "text", nullable: false),
                label = table.Column<string>(type: "text", nullable: false),
                reviewed_role_family = table.Column<string>(type: "text", nullable: true),
                predicted_seniority = table.Column<string>(type: "text", nullable: false),
                reviewed_seniority = table.Column<string>(type: "text", nullable: true),
                seniority_flag = table.Column<string>(type: "text", nullable: false),
                seniority_note = table.Column<string>(type: "text", nullable: true),
                notes = table.Column<string>(type: "text", nullable: true),
                reviewer = table.Column<string>(type: "text", nullable: false),
                created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                reviewed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_classification_reviews", x => x.id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_classification_reviews_job_id_rule_version",
            table: "classification_reviews",
            columns: ["job_id", "rule_version"],
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_classification_reviews_label",
            table: "classification_reviews",
            column: "label");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "classification_reviews");
    }
}
