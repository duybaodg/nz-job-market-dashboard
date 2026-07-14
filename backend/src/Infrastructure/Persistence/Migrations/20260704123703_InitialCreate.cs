using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "crawl_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    source = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    started_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    finished_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    total_pages = table.Column<int>(type: "integer", nullable: false),
                    total_jobs_found = table.Column<int>(type: "integer", nullable: false),
                    total_jobs_saved = table.Column<int>(type: "integer", nullable: false),
                    error_message = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_crawl_runs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "job_sources",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    base_url = table.Column<string>(type: "text", nullable: true),
                    method = table.Column<string>(type: "text", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    crawl_frequency_minutes = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_job_sources", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "jobs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    source = table.Column<string>(type: "text", nullable: false),
                    source_job_id = table.Column<string>(type: "text", nullable: true),
                    title = table.Column<string>(type: "text", nullable: false),
                    company = table.Column<string>(type: "text", nullable: true),
                    location = table.Column<string>(type: "text", nullable: true),
                    region = table.Column<string>(type: "text", nullable: true),
                    salary_min = table.Column<decimal>(type: "numeric", nullable: true),
                    salary_max = table.Column<decimal>(type: "numeric", nullable: true),
                    employment_type = table.Column<string>(type: "text", nullable: false),
                    seniority = table.Column<string>(type: "text", nullable: false),
                    work_mode = table.Column<string>(type: "text", nullable: false),
                    industry = table.Column<string>(type: "text", nullable: true),
                    description_summary = table.Column<string>(type: "text", nullable: true),
                    url = table.Column<string>(type: "text", nullable: false),
                    content_hash = table.Column<string>(type: "text", nullable: false),
                    posted_date = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    closing_date = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    first_seen_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_jobs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "raw_job_pages",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    source = table.Column<string>(type: "text", nullable: false),
                    url = table.Column<string>(type: "text", nullable: false),
                    raw_html = table.Column<string>(type: "text", nullable: true),
                    markdown = table.Column<string>(type: "text", nullable: true),
                    raw_json = table.Column<string>(type: "jsonb", nullable: true),
                    crawled_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    crawl_run_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_raw_job_pages", x => x.id);
                    table.ForeignKey(
                        name: "FK_raw_job_pages_crawl_runs_crawl_run_id",
                        column: x => x.crawl_run_id,
                        principalTable: "crawl_runs",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "job_skills",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    skill_name = table.Column<string>(type: "text", nullable: false),
                    skill_type = table.Column<string>(type: "text", nullable: true),
                    confidence = table.Column<decimal>(type: "numeric", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_job_skills", x => x.id);
                    table.ForeignKey(
                        name: "FK_job_skills_jobs_job_id",
                        column: x => x.job_id,
                        principalTable: "jobs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "job_sources",
                columns: new[] { "id", "base_url", "crawl_frequency_minutes", "created_at", "enabled", "method", "name", "updated_at" },
                values: new object[] { new Guid("bbbbbbbb-0001-0000-0000-000000000001"), "https://example.com/jobs", 1440, new DateTime(2026, 7, 5, 0, 0, 0, 0, DateTimeKind.Utc), true, "Seed", "Seed", new DateTime(2026, 7, 5, 0, 0, 0, 0, DateTimeKind.Utc) });

            migrationBuilder.InsertData(
                table: "jobs",
                columns: new[] { "id", "closing_date", "company", "content_hash", "created_at", "description_summary", "employment_type", "first_seen_at", "industry", "last_seen_at", "location", "posted_date", "region", "salary_max", "salary_min", "seniority", "source", "source_job_id", "title", "updated_at", "url", "work_mode" },
                values: new object[,]
                {
                    { new Guid("11111111-1111-1111-1111-111111111111"), null, "Koru Digital", "hash-1", new DateTime(2026, 7, 5, 0, 0, 0, 0, DateTimeKind.Utc), "React and .NET role for a junior developer.", "Full-time", new DateTime(2026, 7, 4, 0, 0, 0, 0, DateTimeKind.Utc), "Technology", new DateTime(2026, 7, 5, 0, 0, 0, 0, DateTimeKind.Utc), "Wellington", new DateTime(2026, 7, 4, 0, 0, 0, 0, DateTimeKind.Utc), "Wellington", 78000m, 65000m, "Junior", "Seed", "seed-1", "Junior Software Developer", new DateTime(2026, 7, 5, 0, 0, 0, 0, DateTimeKind.Utc), "https://example.com/jobs/junior-software-developer", "Hybrid" },
                    { new Guid("22222222-2222-2222-2222-222222222222"), null, "Harbour Systems", "hash-2", new DateTime(2026, 7, 5, 0, 0, 0, 0, DateTimeKind.Utc), "Senior backend role building APIs and data services.", "Full-time", new DateTime(2026, 7, 2, 0, 0, 0, 0, DateTimeKind.Utc), "Technology", new DateTime(2026, 7, 5, 0, 0, 0, 0, DateTimeKind.Utc), "Auckland", new DateTime(2026, 7, 2, 0, 0, 0, 0, DateTimeKind.Utc), "Auckland", 150000m, 125000m, "Senior", "Seed", "seed-2", "Senior .NET Engineer", new DateTime(2026, 7, 5, 0, 0, 0, 0, DateTimeKind.Utc), "https://example.com/jobs/senior-dotnet-engineer", "Hybrid" },
                    { new Guid("33333333-3333-3333-3333-333333333333"), null, "Southern Insights", "hash-3", new DateTime(2026, 7, 5, 0, 0, 0, 0, DateTimeKind.Utc), "Analytics role focused on SQL dashboards and reporting.", "Full-time", new DateTime(2026, 6, 30, 0, 0, 0, 0, DateTimeKind.Utc), "Analytics", new DateTime(2026, 7, 5, 0, 0, 0, 0, DateTimeKind.Utc), "Christchurch", new DateTime(2026, 6, 30, 0, 0, 0, 0, DateTimeKind.Utc), "Canterbury", 95000m, 80000m, "Intermediate", "Seed", "seed-3", "Data Analyst", new DateTime(2026, 7, 5, 0, 0, 0, 0, DateTimeKind.Utc), "https://example.com/jobs/data-analyst", "On-site" },
                    { new Guid("44444444-4444-4444-4444-444444444444"), null, "Cloud Kiwi", "hash-4", new DateTime(2026, 7, 5, 0, 0, 0, 0, DateTimeKind.Utc), "Graduate cloud role using Azure and infrastructure automation.", "Full-time", new DateTime(2026, 7, 3, 0, 0, 0, 0, DateTimeKind.Utc), "Technology", new DateTime(2026, 7, 5, 0, 0, 0, 0, DateTimeKind.Utc), "Hamilton", new DateTime(2026, 7, 3, 0, 0, 0, 0, DateTimeKind.Utc), "Waikato", 72000m, 62000m, "Graduate", "Seed", "seed-4", "Graduate Cloud Engineer", new DateTime(2026, 7, 5, 0, 0, 0, 0, DateTimeKind.Utc), "https://example.com/jobs/graduate-cloud-engineer", "Remote" },
                    { new Guid("55555555-5555-5555-5555-555555555555"), null, "Tui Labs", "hash-5", new DateTime(2026, 7, 5, 0, 0, 0, 0, DateTimeKind.Utc), "React and TypeScript contract role.", "Contract", new DateTime(2026, 6, 27, 0, 0, 0, 0, DateTimeKind.Utc), "Technology", new DateTime(2026, 7, 5, 0, 0, 0, 0, DateTimeKind.Utc), "Auckland", new DateTime(2026, 6, 27, 0, 0, 0, 0, DateTimeKind.Utc), "Auckland", 115000m, 90000m, "Intermediate", "Seed", "seed-5", "Frontend Developer", new DateTime(2026, 7, 5, 0, 0, 0, 0, DateTimeKind.Utc), "https://example.com/jobs/frontend-developer", "Remote" },
                    { new Guid("66666666-6666-6666-6666-666666666666"), null, "Health Data NZ", "hash-6", new DateTime(2026, 7, 5, 0, 0, 0, 0, DateTimeKind.Utc), "Business intelligence role using SQL and Power BI.", "Full-time", new DateTime(2026, 7, 1, 0, 0, 0, 0, DateTimeKind.Utc), "Healthcare", new DateTime(2026, 7, 5, 0, 0, 0, 0, DateTimeKind.Utc), "Dunedin", new DateTime(2026, 7, 1, 0, 0, 0, 0, DateTimeKind.Utc), "Otago", 105000m, 85000m, "Intermediate", "Seed", "seed-6", "BI Developer", new DateTime(2026, 7, 5, 0, 0, 0, 0, DateTimeKind.Utc), "https://example.com/jobs/bi-developer", "Hybrid" }
                });

            migrationBuilder.InsertData(
                table: "job_skills",
                columns: new[] { "id", "confidence", "job_id", "skill_name", "skill_type" },
                values: new object[,]
                {
                    { new Guid("aaaaaaaa-0001-0000-0000-000000000001"), 1m, new Guid("11111111-1111-1111-1111-111111111111"), "React", "Framework" },
                    { new Guid("aaaaaaaa-0002-0000-0000-000000000002"), 1m, new Guid("11111111-1111-1111-1111-111111111111"), ".NET", "Framework" },
                    { new Guid("aaaaaaaa-0003-0000-0000-000000000003"), 1m, new Guid("11111111-1111-1111-1111-111111111111"), "TypeScript", "Language" },
                    { new Guid("aaaaaaaa-0004-0000-0000-000000000004"), 1m, new Guid("22222222-2222-2222-2222-222222222222"), ".NET", "Framework" },
                    { new Guid("aaaaaaaa-0005-0000-0000-000000000005"), 1m, new Guid("22222222-2222-2222-2222-222222222222"), "C#", "Language" },
                    { new Guid("aaaaaaaa-0006-0000-0000-000000000006"), 1m, new Guid("22222222-2222-2222-2222-222222222222"), "PostgreSQL", "Database" },
                    { new Guid("aaaaaaaa-0007-0000-0000-000000000007"), 1m, new Guid("33333333-3333-3333-3333-333333333333"), "SQL", "Database" },
                    { new Guid("aaaaaaaa-0008-0000-0000-000000000008"), 1m, new Guid("33333333-3333-3333-3333-333333333333"), "Power BI", "Tool" },
                    { new Guid("aaaaaaaa-0009-0000-0000-000000000009"), 1m, new Guid("44444444-4444-4444-4444-444444444444"), "Azure", "Cloud" },
                    { new Guid("aaaaaaaa-0010-0000-0000-000000000010"), 1m, new Guid("44444444-4444-4444-4444-444444444444"), "Terraform", "Tool" },
                    { new Guid("aaaaaaaa-0011-0000-0000-000000000011"), 1m, new Guid("55555555-5555-5555-5555-555555555555"), "React", "Framework" },
                    { new Guid("aaaaaaaa-0012-0000-0000-000000000012"), 1m, new Guid("55555555-5555-5555-5555-555555555555"), "TypeScript", "Language" },
                    { new Guid("aaaaaaaa-0013-0000-0000-000000000013"), 1m, new Guid("66666666-6666-6666-6666-666666666666"), "SQL", "Database" },
                    { new Guid("aaaaaaaa-0014-0000-0000-000000000014"), 1m, new Guid("66666666-6666-6666-6666-666666666666"), "Power BI", "Tool" }
                });

            migrationBuilder.CreateIndex(
                name: "IX_job_skills_job_id",
                table: "job_skills",
                column: "job_id");

            migrationBuilder.CreateIndex(
                name: "IX_job_skills_skill_name",
                table: "job_skills",
                column: "skill_name");

            migrationBuilder.CreateIndex(
                name: "IX_jobs_company",
                table: "jobs",
                column: "company");

            migrationBuilder.CreateIndex(
                name: "IX_jobs_content_hash",
                table: "jobs",
                column: "content_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_jobs_region",
                table: "jobs",
                column: "region");

            migrationBuilder.CreateIndex(
                name: "IX_raw_job_pages_crawl_run_id",
                table: "raw_job_pages",
                column: "crawl_run_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "job_skills");

            migrationBuilder.DropTable(
                name: "job_sources");

            migrationBuilder.DropTable(
                name: "raw_job_pages");

            migrationBuilder.DropTable(
                name: "jobs");

            migrationBuilder.DropTable(
                name: "crawl_runs");
        }
    }
}
