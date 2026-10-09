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
            entity.Property(job => job.ExperienceBand).HasColumnName("experience_band");
            entity.Property(job => job.Industry).HasColumnName("industry");
            entity.Property(job => job.DescriptionSummary).HasColumnName("description_summary");
            entity.Property(job => job.Url).HasColumnName("url").IsRequired();
            entity.Property(job => job.ContentHash).HasColumnName("content_hash").IsRequired();
            entity.Property(job => job.PostedDate).HasColumnName("posted_date");
            entity.Property(job => job.ClosingDate).HasColumnName("closing_date");
            entity.Property(job => job.IsActive).HasColumnName("is_active");
            entity.Property(job => job.ExpiredAt).HasColumnName("expired_at");
            entity.Property(job => job.MissingCrawlCount).HasColumnName("missing_crawl_count");
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
            entity.Property(source => source.Parser).HasColumnName("parser").IsRequired();
            entity.Property(source => source.Enabled).HasColumnName("enabled");
            entity.Property(source => source.CrawlFrequencyMinutes).HasColumnName("crawl_frequency_minutes");
            entity.Property(source => source.LastCrawledAt).HasColumnName("last_crawled_at");
            entity.Property(source => source.TermsCheckedAt).HasColumnName("terms_checked_at");
            entity.Property(source => source.CreatedAt).HasColumnName("created_at");
            entity.Property(source => source.UpdatedAt).HasColumnName("updated_at");
            entity.HasIndex(source => source.Name).IsUnique();
        });
    }

}
