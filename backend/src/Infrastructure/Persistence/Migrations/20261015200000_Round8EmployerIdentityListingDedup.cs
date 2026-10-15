using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Round8EmployerIdentityListingDedup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "description_text",
                table: "jobs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "duplicate_group_id",
                table: "jobs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "employer_canonical_id",
                table: "jobs",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "deduplication_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    started_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    finished_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    is_validated = table.Column<bool>(type: "boolean", nullable: false),
                    match_version = table.Column<string>(type: "text", nullable: false),
                    trigger = table.Column<string>(type: "text", nullable: false),
                    listings_evaluated = table.Column<int>(type: "integer", nullable: false),
                    groups_found = table.Column<int>(type: "integer", nullable: false),
                    listings_merged = table.Column<int>(type: "integer", nullable: false),
                    unique_vacancies = table.Column<int>(type: "integer", nullable: false),
                    review_candidate_count = table.Column<int>(type: "integer", nullable: false),
                    notes = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_deduplication_runs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "duplicate_groups",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    canonical_listing_id = table.Column<Guid>(type: "uuid", nullable: false),
                    match_method = table.Column<string>(type: "text", nullable: false),
                    match_version = table.Column<string>(type: "text", nullable: false),
                    confidence = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    member_count = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_duplicate_groups", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "duplicate_review_candidates",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    candidate_job_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reason = table.Column<string>(type: "text", nullable: false),
                    title = table.Column<string>(type: "text", nullable: true),
                    employer = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    resolved_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_duplicate_review_candidates", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "employer_audit_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    employer_id = table.Column<Guid>(type: "uuid", nullable: true),
                    action = table.Column<string>(type: "text", nullable: false),
                    detail = table.Column<string>(type: "text", nullable: true),
                    actor = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_employer_audit_events", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "employers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    canonical_name = table.Column<string>(type: "text", nullable: false),
                    normalized_name = table.Column<string>(type: "text", nullable: false),
                    type = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    notes = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_employers", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "employer_aliases",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    employer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    alias = table.Column<string>(type: "text", nullable: false),
                    normalized_alias = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_employer_aliases", x => x.id);
                    table.ForeignKey(
                        name: "FK_employer_aliases_employers_employer_id",
                        column: x => x.employer_id,
                        principalTable: "employers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_jobs_duplicate_group_id",
                table: "jobs",
                column: "duplicate_group_id");

            migrationBuilder.CreateIndex(
                name: "IX_jobs_employer_canonical_id",
                table: "jobs",
                column: "employer_canonical_id");

            migrationBuilder.CreateIndex(
                name: "IX_deduplication_runs_is_validated",
                table: "deduplication_runs",
                column: "is_validated");

            migrationBuilder.CreateIndex(
                name: "IX_deduplication_runs_started_at",
                table: "deduplication_runs",
                column: "started_at");

            migrationBuilder.CreateIndex(
                name: "IX_duplicate_groups_canonical_listing_id",
                table: "duplicate_groups",
                column: "canonical_listing_id");

            migrationBuilder.CreateIndex(
                name: "IX_duplicate_review_candidates_job_id",
                table: "duplicate_review_candidates",
                column: "job_id");

            migrationBuilder.CreateIndex(
                name: "IX_duplicate_review_candidates_reason",
                table: "duplicate_review_candidates",
                column: "reason");

            migrationBuilder.CreateIndex(
                name: "IX_employer_aliases_employer_id",
                table: "employer_aliases",
                column: "employer_id");

            migrationBuilder.CreateIndex(
                name: "IX_employer_aliases_normalized_alias",
                table: "employer_aliases",
                column: "normalized_alias",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_employer_audit_events_created_at",
                table: "employer_audit_events",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "IX_employer_audit_events_employer_id",
                table: "employer_audit_events",
                column: "employer_id");

            migrationBuilder.CreateIndex(
                name: "IX_employers_normalized_name",
                table: "employers",
                column: "normalized_name",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_jobs_duplicate_groups_duplicate_group_id",
                table: "jobs",
                column: "duplicate_group_id",
                principalTable: "duplicate_groups",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_jobs_employers_employer_canonical_id",
                table: "jobs",
                column: "employer_canonical_id",
                principalTable: "employers",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            // Reviewed curated aliases that seed the canonical employer model.
            // Everything else is bootstrapped once from the distinct source
            // company values by the idempotent repair pass and reviewed through
            // the admin alias workflow; no fuzzy merge ever runs here.
            var seededAt = new DateTime(2026, 10, 15, 0, 0, 0, DateTimeKind.Utc);
            migrationBuilder.InsertData(
                table: "employers",
                columns: ["id", "canonical_name", "normalized_name", "type", "created_at", "notes"],
                values: new object[,]
                {
                    { new Guid("a1000000-0000-0000-0000-000000000001"), "Fisher & Paykel", "fisher & paykel", "DirectEmployer", seededAt, "Seeded canonical employer" },
                    { new Guid("a1000000-0000-0000-0000-000000000002"), "ANZ Bank New Zealand", "anz bank new zealand", "DirectEmployer", seededAt, "Seeded canonical employer" }
                });
            migrationBuilder.InsertData(
                table: "employer_aliases",
                columns: ["id", "employer_id", "alias", "normalized_alias", "created_at", "created_by"],
                values: new object[,]
                {
                    { new Guid("a2000000-0000-0000-0000-000000000001"), new Guid("a1000000-0000-0000-0000-000000000001"), "F&P", "f&p", seededAt, "system:seed" },
                    { new Guid("a2000000-0000-0000-0000-000000000002"), new Guid("a1000000-0000-0000-0000-000000000002"), "ANZ", "anz", seededAt, "system:seed" }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The seeded canonical employers/aliases live in the tables dropped
            // below, so dropping the tables reverts the seed as well.
            migrationBuilder.DropForeignKey(
                name: "FK_jobs_duplicate_groups_duplicate_group_id",
                table: "jobs");

            migrationBuilder.DropForeignKey(
                name: "FK_jobs_employers_employer_canonical_id",
                table: "jobs");

            migrationBuilder.DropTable(
                name: "deduplication_runs");

            migrationBuilder.DropTable(
                name: "duplicate_groups");

            migrationBuilder.DropTable(
                name: "duplicate_review_candidates");

            migrationBuilder.DropTable(
                name: "employer_aliases");

            migrationBuilder.DropTable(
                name: "employer_audit_events");

            migrationBuilder.DropTable(
                name: "employers");

            migrationBuilder.DropIndex(
                name: "IX_jobs_duplicate_group_id",
                table: "jobs");

            migrationBuilder.DropIndex(
                name: "IX_jobs_employer_canonical_id",
                table: "jobs");

            migrationBuilder.DropColumn(
                name: "description_text",
                table: "jobs");

            migrationBuilder.DropColumn(
                name: "duplicate_group_id",
                table: "jobs");

            migrationBuilder.DropColumn(
                name: "employer_canonical_id",
                table: "jobs");
        }
    }
}
