using Application.Common;
using Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Persistence;

public sealed class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
    : DbContext(options), IApplicationDbContext
{
    public DbSet<Job> Jobs => Set<Job>();
    public DbSet<JobSkill> JobSkills => Set<JobSkill>();
    public DbSet<RawJobPage> RawJobPages => Set<RawJobPage>();
    public DbSet<CrawlRun> CrawlRuns => Set<CrawlRun>();
    public DbSet<JobSource> JobSources => Set<JobSource>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigureJobs(modelBuilder);
        ConfigureJobSkills(modelBuilder);
        ConfigureRawJobPages(modelBuilder);
        ConfigureCrawlRuns(modelBuilder);
        ConfigureJobSources(modelBuilder);
        SeedData(modelBuilder);
    }

    private static void ConfigureJobs(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Job>(entity =>
        {
            entity.ToTable("jobs");
            entity.HasKey(job => job.Id);
            entity.Property(job => job.Id).HasColumnName("id");
            entity.Property(job => job.Source).HasColumnName("source").IsRequired();
            entity.Property(job => job.SourceJobId).HasColumnName("source_job_id");
            entity.Property(job => job.Title).HasColumnName("title").IsRequired();
            entity.Property(job => job.Company).HasColumnName("company");
            entity.Property(job => job.Location).HasColumnName("location");
            entity.Property(job => job.Region).HasColumnName("region");
            entity.Property(job => job.SalaryMin).HasColumnName("salary_min");
            entity.Property(job => job.SalaryMax).HasColumnName("salary_max");
            entity.Property(job => job.EmploymentType).HasColumnName("employment_type");
            entity.Property(job => job.Seniority).HasColumnName("seniority");
            entity.Property(job => job.WorkMode).HasColumnName("work_mode");
            entity.Property(job => job.Industry).HasColumnName("industry");
            entity.Property(job => job.DescriptionSummary).HasColumnName("description_summary");
            entity.Property(job => job.Url).HasColumnName("url").IsRequired();
            entity.Property(job => job.ContentHash).HasColumnName("content_hash").IsRequired();
            entity.Property(job => job.PostedDate).HasColumnName("posted_date");
            entity.Property(job => job.ClosingDate).HasColumnName("closing_date");
            entity.Property(job => job.FirstSeenAt).HasColumnName("first_seen_at");
            entity.Property(job => job.LastSeenAt).HasColumnName("last_seen_at");
            entity.Property(job => job.CreatedAt).HasColumnName("created_at");
            entity.Property(job => job.UpdatedAt).HasColumnName("updated_at");
            entity.HasIndex(job => job.ContentHash).IsUnique();
            entity.HasIndex(job => job.Region);
            entity.HasIndex(job => job.Company);
        });
    }

    private static void ConfigureJobSkills(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<JobSkill>(entity =>
        {
            entity.ToTable("job_skills");
            entity.HasKey(skill => skill.Id);
            entity.Property(skill => skill.Id).HasColumnName("id");
            entity.Property(skill => skill.JobId).HasColumnName("job_id");
            entity.Property(skill => skill.SkillName).HasColumnName("skill_name").IsRequired();
            entity.Property(skill => skill.SkillType).HasColumnName("skill_type");
            entity.Property(skill => skill.Confidence).HasColumnName("confidence");
            entity.HasOne(skill => skill.Job)
                .WithMany(job => job.Skills)
                .HasForeignKey(skill => skill.JobId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(skill => skill.SkillName);
        });
    }

    private static void ConfigureRawJobPages(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<RawJobPage>(entity =>
        {
            entity.ToTable("raw_job_pages");
            entity.HasKey(page => page.Id);
            entity.Property(page => page.Id).HasColumnName("id");
            entity.Property(page => page.Source).HasColumnName("source").IsRequired();
            entity.Property(page => page.Url).HasColumnName("url").IsRequired();
            entity.Property(page => page.RawHtml).HasColumnName("raw_html");
            entity.Property(page => page.Markdown).HasColumnName("markdown");
            entity.Property(page => page.RawJson).HasColumnName("raw_json").HasColumnType("jsonb");
            entity.Property(page => page.CrawledAt).HasColumnName("crawled_at");
            entity.Property(page => page.CrawlRunId).HasColumnName("crawl_run_id");
        });
    }

    private static void ConfigureCrawlRuns(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CrawlRun>(entity =>
        {
            entity.ToTable("crawl_runs");
            entity.HasKey(run => run.Id);
            entity.Property(run => run.Id).HasColumnName("id");
            entity.Property(run => run.Source).HasColumnName("source").IsRequired();
            entity.Property(run => run.Status).HasColumnName("status").IsRequired();
            entity.Property(run => run.StartedAt).HasColumnName("started_at");
            entity.Property(run => run.FinishedAt).HasColumnName("finished_at");
            entity.Property(run => run.TotalPages).HasColumnName("total_pages");
            entity.Property(run => run.TotalJobsFound).HasColumnName("total_jobs_found");
            entity.Property(run => run.TotalJobsSaved).HasColumnName("total_jobs_saved");
            entity.Property(run => run.ErrorMessage).HasColumnName("error_message");
        });
    }

    private static void ConfigureJobSources(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<JobSource>(entity =>
        {
            entity.ToTable("job_sources");
            entity.HasKey(source => source.Id);
            entity.Property(source => source.Id).HasColumnName("id");
            entity.Property(source => source.Name).HasColumnName("name").IsRequired();
            entity.Property(source => source.BaseUrl).HasColumnName("base_url");
            entity.Property(source => source.Method).HasColumnName("method").IsRequired();
            entity.Property(source => source.Enabled).HasColumnName("enabled");
            entity.Property(source => source.CrawlFrequencyMinutes).HasColumnName("crawl_frequency_minutes");
            entity.Property(source => source.CreatedAt).HasColumnName("created_at");
            entity.Property(source => source.UpdatedAt).HasColumnName("updated_at");
        });
    }

    private static void SeedData(ModelBuilder modelBuilder)
    {
        var now = new DateTime(2026, 7, 5, 0, 0, 0, DateTimeKind.Utc);

        var job1 = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var job2 = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var job3 = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var job4 = Guid.Parse("44444444-4444-4444-4444-444444444444");
        var job5 = Guid.Parse("55555555-5555-5555-5555-555555555555");
        var job6 = Guid.Parse("66666666-6666-6666-6666-666666666666");

        modelBuilder.Entity<Job>().HasData(
            CreateJob(job1, "Seed", "seed-1", "Junior Software Developer", "Koru Digital", "Wellington", "Wellington", 65000, 78000, "Full-time", "Junior", "Hybrid", "Technology", "React and .NET role for a junior developer.", "https://example.com/jobs/junior-software-developer", "hash-1", now.AddDays(-1), now),
            CreateJob(job2, "Seed", "seed-2", "Senior .NET Engineer", "Harbour Systems", "Auckland", "Auckland", 125000, 150000, "Full-time", "Senior", "Hybrid", "Technology", "Senior backend role building APIs and data services.", "https://example.com/jobs/senior-dotnet-engineer", "hash-2", now.AddDays(-3), now),
            CreateJob(job3, "Seed", "seed-3", "Data Analyst", "Southern Insights", "Christchurch", "Canterbury", 80000, 95000, "Full-time", "Intermediate", "On-site", "Analytics", "Analytics role focused on SQL dashboards and reporting.", "https://example.com/jobs/data-analyst", "hash-3", now.AddDays(-5), now),
            CreateJob(job4, "Seed", "seed-4", "Graduate Cloud Engineer", "Cloud Kiwi", "Hamilton", "Waikato", 62000, 72000, "Full-time", "Graduate", "Remote", "Technology", "Graduate cloud role using Azure and infrastructure automation.", "https://example.com/jobs/graduate-cloud-engineer", "hash-4", now.AddDays(-2), now),
            CreateJob(job5, "Seed", "seed-5", "Frontend Developer", "Tui Labs", "Auckland", "Auckland", 90000, 115000, "Contract", "Intermediate", "Remote", "Technology", "React and TypeScript contract role.", "https://example.com/jobs/frontend-developer", "hash-5", now.AddDays(-8), now),
            CreateJob(job6, "Seed", "seed-6", "BI Developer", "Health Data NZ", "Dunedin", "Otago", 85000, 105000, "Full-time", "Intermediate", "Hybrid", "Healthcare", "Business intelligence role using SQL and Power BI.", "https://example.com/jobs/bi-developer", "hash-6", now.AddDays(-4), now));

        modelBuilder.Entity<JobSkill>().HasData(
            CreateSkill("aaaaaaaa-0001-0000-0000-000000000001", job1, "React", "Framework"),
            CreateSkill("aaaaaaaa-0002-0000-0000-000000000002", job1, ".NET", "Framework"),
            CreateSkill("aaaaaaaa-0003-0000-0000-000000000003", job1, "TypeScript", "Language"),
            CreateSkill("aaaaaaaa-0004-0000-0000-000000000004", job2, ".NET", "Framework"),
            CreateSkill("aaaaaaaa-0005-0000-0000-000000000005", job2, "C#", "Language"),
            CreateSkill("aaaaaaaa-0006-0000-0000-000000000006", job2, "PostgreSQL", "Database"),
            CreateSkill("aaaaaaaa-0007-0000-0000-000000000007", job3, "SQL", "Database"),
            CreateSkill("aaaaaaaa-0008-0000-0000-000000000008", job3, "Power BI", "Tool"),
            CreateSkill("aaaaaaaa-0009-0000-0000-000000000009", job4, "Azure", "Cloud"),
            CreateSkill("aaaaaaaa-0010-0000-0000-000000000010", job4, "Terraform", "Tool"),
            CreateSkill("aaaaaaaa-0011-0000-0000-000000000011", job5, "React", "Framework"),
            CreateSkill("aaaaaaaa-0012-0000-0000-000000000012", job5, "TypeScript", "Language"),
            CreateSkill("aaaaaaaa-0013-0000-0000-000000000013", job6, "SQL", "Database"),
            CreateSkill("aaaaaaaa-0014-0000-0000-000000000014", job6, "Power BI", "Tool"));

        modelBuilder.Entity<JobSource>().HasData(
            new JobSource
            {
                Id = Guid.Parse("bbbbbbbb-0001-0000-0000-000000000001"),
                Name = "Seed",
                BaseUrl = "https://example.com/jobs",
                Method = "Seed",
                Enabled = true,
                CrawlFrequencyMinutes = 1440,
                CreatedAt = now,
                UpdatedAt = now
            });
    }

    private static Job CreateJob(
        Guid id,
        string source,
        string sourceJobId,
        string title,
        string company,
        string location,
        string region,
        decimal salaryMin,
        decimal salaryMax,
        string employmentType,
        string seniority,
        string workMode,
        string industry,
        string summary,
        string url,
        string contentHash,
        DateTime postedDate,
        DateTime now)
    {
        return new Job
        {
            Id = id,
            Source = source,
            SourceJobId = sourceJobId,
            Title = title,
            Company = company,
            Location = location,
            Region = region,
            SalaryMin = salaryMin,
            SalaryMax = salaryMax,
            EmploymentType = employmentType,
            Seniority = seniority,
            WorkMode = workMode,
            Industry = industry,
            DescriptionSummary = summary,
            Url = url,
            ContentHash = contentHash,
            PostedDate = postedDate,
            FirstSeenAt = postedDate,
            LastSeenAt = now,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static JobSkill CreateSkill(string id, Guid jobId, string skillName, string skillType)
    {
        return new JobSkill
        {
            Id = Guid.Parse(id),
            JobId = jobId,
            SkillName = skillName,
            SkillType = skillType,
            Confidence = 1
        };
    }
}
