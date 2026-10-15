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
    public DbSet<ListingSnapshot> ListingSnapshots => Set<ListingSnapshot>();
    public DbSet<ListingSnapshotRun> ListingSnapshotRuns => Set<ListingSnapshotRun>();
    public DbSet<Employer> Employers => Set<Employer>();
    public DbSet<EmployerAlias> EmployerAliases => Set<EmployerAlias>();
    public DbSet<EmployerAuditEvent> EmployerAuditEvents => Set<EmployerAuditEvent>();
    public DbSet<DuplicateGroup> DuplicateGroups => Set<DuplicateGroup>();
    public DbSet<DuplicateReviewCandidate> DuplicateReviewCandidates => Set<DuplicateReviewCandidate>();
    public DbSet<DeduplicationRun> DeduplicationRuns => Set<DeduplicationRun>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigureJobs(modelBuilder);
        ConfigureJobSkills(modelBuilder);
        ConfigureRawJobPages(modelBuilder);
        ConfigureCrawlRuns(modelBuilder);
        ConfigureJobSources(modelBuilder);
        ConfigureListingSnapshots(modelBuilder);
        ConfigureEmployers(modelBuilder);
        ConfigureDeduplication(modelBuilder);
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
            entity.Property(job => job.SalaryText).HasColumnName("salary_text");
            entity.Property(job => job.SalaryCurrency).HasColumnName("salary_currency").HasDefaultValue("NZD");
            entity.Property(job => job.SalaryPeriod).HasColumnName("salary_period").HasDefaultValue("Unknown");
            entity.Property(job => job.SalaryQuestionable).HasColumnName("salary_questionable").HasDefaultValue(false);
            entity.Property(job => job.SalaryQuestionableReason).HasColumnName("salary_questionable_reason");
            entity.Property(job => job.SalarySource).HasColumnName("salary_source");
            entity.Property(job => job.EmploymentType).HasColumnName("employment_type");
            entity.Property(job => job.EmploymentTypeSource).HasColumnName("employment_type_source");
            entity.Property(job => job.Seniority).HasColumnName("seniority");
            entity.Property(job => job.SenioritySource).HasColumnName("seniority_source");
            entity.Property(job => job.SeniorityConfidence).HasColumnName("seniority_confidence");
            entity.Property(job => job.WorkMode).HasColumnName("work_mode");
            entity.Property(job => job.WorkModeSource).HasColumnName("work_mode_source");
            entity.Property(job => job.WorkModeConfidence).HasColumnName("work_mode_confidence");
            entity.Property(job => job.ExperienceBand).HasColumnName("experience_band");
            entity.Property(job => job.AiEnrichmentModel).HasColumnName("ai_enrichment_model");
            entity.Property(job => job.AiEnrichedAt).HasColumnName("ai_enriched_at");
            entity.Property(job => job.Industry).HasColumnName("industry");
            entity.Property(job => job.DescriptionSummary).HasColumnName("description_summary");
            entity.Property(job => job.DescriptionText).HasColumnName("description_text");
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
            entity.Property(job => job.EmployerCanonicalId).HasColumnName("employer_canonical_id");
            entity.Property(job => job.DuplicateGroupId).HasColumnName("duplicate_group_id");
            entity.HasIndex(job => job.ContentHash).IsUnique();
            entity.HasIndex(job => job.Region);
            entity.HasIndex(job => job.Company);
            entity.HasIndex(job => job.EmployerCanonicalId);
            entity.HasIndex(job => job.DuplicateGroupId);
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
            entity.Property(source => source.Category).HasColumnName("source_category").HasDefaultValue("unknown");
            entity.Property(source => source.Enabled).HasColumnName("enabled");
            entity.Property(source => source.CrawlFrequencyMinutes).HasColumnName("crawl_frequency_minutes");
            entity.Property(source => source.LastCrawledAt).HasColumnName("last_crawled_at");
            entity.Property(source => source.TermsCheckedAt).HasColumnName("terms_checked_at");
            entity.Property(source => source.CreatedAt).HasColumnName("created_at");
            entity.Property(source => source.UpdatedAt).HasColumnName("updated_at");
            entity.HasIndex(source => source.Name).IsUnique();
        });
    }

    private static void ConfigureListingSnapshots(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ListingSnapshot>(entity =>
        {
            entity.ToTable("listing_snapshots");
            entity.HasKey(snapshot => snapshot.Id);
            entity.Property(snapshot => snapshot.Id).HasColumnName("id");
            entity.Property(snapshot => snapshot.SnapshotDate).HasColumnName("snapshot_date");
            entity.Property(snapshot => snapshot.JobId).HasColumnName("job_id");
            entity.Property(snapshot => snapshot.Source).HasColumnName("source").IsRequired();
            entity.Property(snapshot => snapshot.SourceCategory).HasColumnName("source_category").IsRequired().HasDefaultValue("unknown");
            entity.Property(snapshot => snapshot.ContentHash).HasColumnName("content_hash").IsRequired();
            entity.Property(snapshot => snapshot.Title).HasColumnName("title").IsRequired();
            entity.Property(snapshot => snapshot.Company).HasColumnName("company");
            entity.Property(snapshot => snapshot.HiringEmployer).HasColumnName("hiring_employer");
            entity.Property(snapshot => snapshot.AgencyAdvertised).HasColumnName("agency_advertised");
            entity.Property(snapshot => snapshot.Location).HasColumnName("location");
            entity.Property(snapshot => snapshot.Region).HasColumnName("region");
            entity.Property(snapshot => snapshot.WorkMode).HasColumnName("work_mode").IsRequired();
            entity.Property(snapshot => snapshot.Seniority).HasColumnName("seniority").IsRequired();
            entity.Property(snapshot => snapshot.EmploymentType).HasColumnName("employment_type").IsRequired();
            entity.Property(snapshot => snapshot.RoleFamily).HasColumnName("role_family").IsRequired();
            entity.Property(snapshot => snapshot.SalaryMin).HasColumnName("salary_min");
            entity.Property(snapshot => snapshot.SalaryMax).HasColumnName("salary_max");
            entity.Property(snapshot => snapshot.SalaryCurrency).HasColumnName("salary_currency").IsRequired().HasDefaultValue("NZD");
            entity.Property(snapshot => snapshot.SalaryPeriod).HasColumnName("salary_period").IsRequired().HasDefaultValue("Unknown");
            entity.Property(snapshot => snapshot.PostedDate).HasColumnName("posted_date");
            entity.Property(snapshot => snapshot.FirstSeenAt).HasColumnName("first_seen_at");
            entity.Property(snapshot => snapshot.LastSeenAt).HasColumnName("last_seen_at");
            entity.Property(snapshot => snapshot.IsActive).HasColumnName("is_active");
            entity.Property(snapshot => snapshot.CapturedAtUtc).HasColumnName("captured_at_utc");
            entity.HasIndex(snapshot => new { snapshot.SnapshotDate, snapshot.ContentHash }).IsUnique();
            entity.HasIndex(snapshot => snapshot.FirstSeenAt);
        });

        modelBuilder.Entity<ListingSnapshotRun>(entity =>
        {
            entity.ToTable("listing_snapshot_runs");
            entity.HasKey(run => run.Id);
            entity.Property(run => run.Id).HasColumnName("id");
            entity.Property(run => run.SnapshotDate).HasColumnName("snapshot_date");
            entity.Property(run => run.CapturedAtUtc).HasColumnName("captured_at_utc");
            entity.Property(run => run.ListingCount).HasColumnName("listing_count");
            entity.Property(run => run.ActiveCount).HasColumnName("active_count");
            entity.Property(run => run.SourceCount).HasColumnName("source_count");
            entity.Property(run => run.IdentifiableEmployerCount).HasColumnName("identifiable_employer_count");
            entity.Property(run => run.SourceCoverageJson).HasColumnName("source_coverage_json").HasColumnType("jsonb");
            entity.HasIndex(run => run.SnapshotDate).IsUnique();
        });
    }

    private static void ConfigureEmployers(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Employer>(entity =>
        {
            entity.ToTable("employers");
            entity.HasKey(employer => employer.Id);
            entity.Property(employer => employer.Id).HasColumnName("id");
            entity.Property(employer => employer.CanonicalName).HasColumnName("canonical_name").IsRequired();
            entity.Property(employer => employer.NormalizedName).HasColumnName("normalized_name").IsRequired();
            entity.Property(employer => employer.Type).HasColumnName("type").HasConversion<string>().IsRequired();
            entity.Property(employer => employer.CreatedAt).HasColumnName("created_at");
            entity.Property(employer => employer.Notes).HasColumnName("notes");
            entity.HasIndex(employer => employer.NormalizedName).IsUnique();
        });

        modelBuilder.Entity<EmployerAlias>(entity =>
        {
            entity.ToTable("employer_aliases");
            entity.HasKey(alias => alias.Id);
            entity.Property(alias => alias.Id).HasColumnName("id");
            entity.Property(alias => alias.EmployerId).HasColumnName("employer_id");
            entity.Property(alias => alias.Alias).HasColumnName("alias").IsRequired();
            entity.Property(alias => alias.NormalizedAlias).HasColumnName("normalized_alias").IsRequired();
            entity.Property(alias => alias.CreatedAt).HasColumnName("created_at");
            entity.Property(alias => alias.CreatedBy).HasColumnName("created_by");
            entity.HasOne(alias => alias.Employer)
                .WithMany(employer => employer.Aliases)
                .HasForeignKey(alias => alias.EmployerId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(alias => alias.NormalizedAlias).IsUnique();
        });

        modelBuilder.Entity<EmployerAuditEvent>(entity =>
        {
            entity.ToTable("employer_audit_events");
            entity.HasKey(audit => audit.Id);
            entity.Property(audit => audit.Id).HasColumnName("id");
            entity.Property(audit => audit.EmployerId).HasColumnName("employer_id");
            entity.Property(audit => audit.Action).HasColumnName("action").IsRequired();
            entity.Property(audit => audit.Detail).HasColumnName("detail");
            entity.Property(audit => audit.Actor).HasColumnName("actor").IsRequired();
            entity.Property(audit => audit.CreatedAt).HasColumnName("created_at");
            entity.HasIndex(audit => audit.EmployerId);
            entity.HasIndex(audit => audit.CreatedAt);
        });

        // Job -> Employer and Job -> DuplicateGroup are optional references: a
        // job with no reviewed employer or no duplicate stays fully usable, and
        // deleting a canonical/group only detaches the job (source rows survive).
        modelBuilder.Entity<Job>()
            .HasOne<Employer>()
            .WithMany()
            .HasForeignKey(job => job.EmployerCanonicalId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<Job>()
            .HasOne<DuplicateGroup>()
            .WithMany()
            .HasForeignKey(job => job.DuplicateGroupId)
            .OnDelete(DeleteBehavior.SetNull);
    }

    private static void ConfigureDeduplication(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DuplicateGroup>(entity =>
        {
            entity.ToTable("duplicate_groups");
            entity.HasKey(group => group.Id);
            entity.Property(group => group.Id).HasColumnName("id");
            entity.Property(group => group.CanonicalListingId).HasColumnName("canonical_listing_id");
            entity.Property(group => group.MatchMethod).HasColumnName("match_method").IsRequired();
            entity.Property(group => group.MatchVersion).HasColumnName("match_version").IsRequired();
            entity.Property(group => group.Confidence).HasColumnName("confidence").IsRequired();
            entity.Property(group => group.CreatedAt).HasColumnName("created_at");
            entity.Property(group => group.UpdatedAt).HasColumnName("updated_at");
            entity.Property(group => group.MemberCount).HasColumnName("member_count");
            entity.HasIndex(group => group.CanonicalListingId);
        });

        modelBuilder.Entity<DuplicateReviewCandidate>(entity =>
        {
            entity.ToTable("duplicate_review_candidates");
            entity.HasKey(candidate => candidate.Id);
            entity.Property(candidate => candidate.Id).HasColumnName("id");
            entity.Property(candidate => candidate.JobId).HasColumnName("job_id");
            entity.Property(candidate => candidate.CandidateJobId).HasColumnName("candidate_job_id");
            entity.Property(candidate => candidate.Reason).HasColumnName("reason").IsRequired();
            entity.Property(candidate => candidate.Title).HasColumnName("title");
            entity.Property(candidate => candidate.Employer).HasColumnName("employer");
            entity.Property(candidate => candidate.CreatedAt).HasColumnName("created_at");
            entity.Property(candidate => candidate.ResolvedAt).HasColumnName("resolved_at");
            entity.HasIndex(candidate => candidate.JobId);
            entity.HasIndex(candidate => candidate.Reason);
        });

        modelBuilder.Entity<DeduplicationRun>(entity =>
        {
            entity.ToTable("deduplication_runs");
            entity.HasKey(run => run.Id);
            entity.Property(run => run.Id).HasColumnName("id");
            entity.Property(run => run.StartedAt).HasColumnName("started_at");
            entity.Property(run => run.FinishedAt).HasColumnName("finished_at");
            entity.Property(run => run.Status).HasColumnName("status").IsRequired();
            entity.Property(run => run.IsValidated).HasColumnName("is_validated");
            entity.Property(run => run.MatchVersion).HasColumnName("match_version").IsRequired();
            entity.Property(run => run.Trigger).HasColumnName("trigger").IsRequired();
            entity.Property(run => run.ListingsEvaluated).HasColumnName("listings_evaluated");
            entity.Property(run => run.GroupsFound).HasColumnName("groups_found");
            entity.Property(run => run.ListingsMerged).HasColumnName("listings_merged");
            entity.Property(run => run.UniqueVacancies).HasColumnName("unique_vacancies");
            entity.Property(run => run.ReviewCandidateCount).HasColumnName("review_candidate_count");
            entity.Property(run => run.Notes).HasColumnName("notes");
            entity.HasIndex(run => run.StartedAt);
            entity.HasIndex(run => run.IsValidated);
        });
    }

}
