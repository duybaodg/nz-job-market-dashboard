using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddJobLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "job_skills",
                keyColumn: "id",
                keyValue: new Guid("aaaaaaaa-0001-0000-0000-000000000001"));

            migrationBuilder.DeleteData(
                table: "job_skills",
                keyColumn: "id",
                keyValue: new Guid("aaaaaaaa-0002-0000-0000-000000000002"));

            migrationBuilder.DeleteData(
                table: "job_skills",
                keyColumn: "id",
                keyValue: new Guid("aaaaaaaa-0003-0000-0000-000000000003"));

            migrationBuilder.DeleteData(
                table: "job_skills",
                keyColumn: "id",
                keyValue: new Guid("aaaaaaaa-0004-0000-0000-000000000004"));

            migrationBuilder.DeleteData(
                table: "job_skills",
                keyColumn: "id",
                keyValue: new Guid("aaaaaaaa-0005-0000-0000-000000000005"));

            migrationBuilder.DeleteData(
                table: "job_skills",
                keyColumn: "id",
                keyValue: new Guid("aaaaaaaa-0006-0000-0000-000000000006"));

            migrationBuilder.DeleteData(
                table: "job_skills",
                keyColumn: "id",
                keyValue: new Guid("aaaaaaaa-0007-0000-0000-000000000007"));

            migrationBuilder.DeleteData(
                table: "job_skills",
                keyColumn: "id",
                keyValue: new Guid("aaaaaaaa-0008-0000-0000-000000000008"));

            migrationBuilder.DeleteData(
                table: "job_skills",
                keyColumn: "id",
                keyValue: new Guid("aaaaaaaa-0009-0000-0000-000000000009"));

            migrationBuilder.DeleteData(
                table: "job_skills",
                keyColumn: "id",
                keyValue: new Guid("aaaaaaaa-0010-0000-0000-000000000010"));

            migrationBuilder.DeleteData(
                table: "job_skills",
                keyColumn: "id",
                keyValue: new Guid("aaaaaaaa-0011-0000-0000-000000000011"));

            migrationBuilder.DeleteData(
                table: "job_skills",
                keyColumn: "id",
                keyValue: new Guid("aaaaaaaa-0012-0000-0000-000000000012"));

            migrationBuilder.DeleteData(
                table: "job_skills",
                keyColumn: "id",
                keyValue: new Guid("aaaaaaaa-0013-0000-0000-000000000013"));

            migrationBuilder.DeleteData(
                table: "job_skills",
                keyColumn: "id",
                keyValue: new Guid("aaaaaaaa-0014-0000-0000-000000000014"));

            migrationBuilder.DeleteData(
                table: "job_sources",
                keyColumn: "id",
                keyValue: new Guid("bbbbbbbb-0001-0000-0000-000000000001"));

            migrationBuilder.DeleteData(
                table: "jobs",
                keyColumn: "id",
                keyValue: new Guid("11111111-1111-1111-1111-111111111111"));

            migrationBuilder.DeleteData(
                table: "jobs",
                keyColumn: "id",
                keyValue: new Guid("22222222-2222-2222-2222-222222222222"));

            migrationBuilder.DeleteData(
                table: "jobs",
                keyColumn: "id",
                keyValue: new Guid("33333333-3333-3333-3333-333333333333"));

            migrationBuilder.DeleteData(
                table: "jobs",
                keyColumn: "id",
                keyValue: new Guid("44444444-4444-4444-4444-444444444444"));

            migrationBuilder.DeleteData(
                table: "jobs",
                keyColumn: "id",
                keyValue: new Guid("55555555-5555-5555-5555-555555555555"));

            migrationBuilder.DeleteData(
                table: "jobs",
                keyColumn: "id",
                keyValue: new Guid("66666666-6666-6666-6666-666666666666"));

            migrationBuilder.AddColumn<DateTime>(
                name: "expired_at",
                table: "jobs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_active",
                table: "jobs",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<int>(
                name: "missing_crawl_count",
                table: "jobs",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "expired_at",
                table: "jobs");

            migrationBuilder.DropColumn(
                name: "is_active",
                table: "jobs");

            migrationBuilder.DropColumn(
                name: "missing_crawl_count",
                table: "jobs");

            migrationBuilder.InsertData(
                table: "job_sources",
                columns: new[] { "id", "base_url", "crawl_frequency_minutes", "created_at", "enabled", "last_crawled_at", "method", "name", "parser", "terms_checked_at", "updated_at" },
                values: new object[] { new Guid("bbbbbbbb-0001-0000-0000-000000000001"), "https://example.com/jobs", 1440, new DateTime(2026, 7, 5, 0, 0, 0, 0, DateTimeKind.Utc), false, null, "Seed", "Seed", "generic", null, new DateTime(2026, 7, 5, 0, 0, 0, 0, DateTimeKind.Utc) });

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
        }
    }
}
