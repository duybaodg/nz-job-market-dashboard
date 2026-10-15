using Application.Analytics;
using Application.Metrics;
using Domain.Entities;
using FluentAssertions;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Application.Tests;

public sealed class AnalyticsServiceTests
{
    // 2026-10-15T00:30Z is 2026-10-15 13:30 NZDT. The 7 complete-day window is
    // [2026-10-07T11:00Z, 2026-10-14T11:00Z) and the previous window is
    // [2026-09-30T11:00Z, 2026-10-07T11:00Z).
    private static readonly DateTime Now = new(2026, 10, 15, 0, 30, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Overview_separates_posted_from_discovered_and_labels_the_interval()
    {
        await using var dbContext = CreateDbContext();
        AddJob(dbContext, "A", posted: new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc), firstSeen: new DateTime(2026, 10, 13, 0, 0, 0, DateTimeKind.Utc));
        AddJob(dbContext, "B", posted: new DateTime(2026, 10, 10, 0, 0, 0, DateTimeKind.Utc), firstSeen: new DateTime(2026, 10, 10, 0, 0, 0, DateTimeKind.Utc));
        AddJob(dbContext, "C", posted: null, firstSeen: new DateTime(2026, 10, 13, 0, 0, 0, DateTimeKind.Utc));
        AddJob(dbContext, "D", posted: new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc), firstSeen: new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc));
        AddJob(dbContext, "F", posted: null, firstSeen: new DateTime(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc));
        AddJob(dbContext, "E", active: false, posted: new DateTime(2026, 10, 12, 0, 0, 0, DateTimeKind.Utc), firstSeen: new DateTime(2026, 10, 12, 0, 0, 0, DateTimeKind.Utc));
        await dbContext.SaveChangesAsync();

        var overview = await new AnalyticsService(dbContext, new FixedTimeProvider(Now)).GetOverviewAsync(CancellationToken.None);

        overview.ActiveListings.Should().Be(5, "the inactive listing is outside the active cohort");
        overview.NewlyDiscovered.Current.Should().Be(3, "A, B and C were first seen inside the window");
        overview.NewlyPosted.Current.Should().Be(1, "only B has a posted date inside the window");
        overview.NewlyDiscovered.PercentChange.Should().Be(200m, "one listing was discovered in the previous window");
        overview.NewlyPosted.NoPriorBaseline.Should().BeTrue("no comparable posted date exists in the previous window");
        overview.Interval.StartLocalDate.Should().Be("2026-10-08");
        overview.Interval.EndLocalDateExclusive.Should().Be("2026-10-15");
        overview.Interval.Label.Should().NotBeNullOrWhiteSpace();
        overview.Interval.IsPartial.Should().BeFalse();
    }

    [Fact]
    public async Task Overview_reports_salary_sample_coverage_and_exclusions()
    {
        await using var dbContext = CreateDbContext();
        AddJob(dbContext, "A", salaryMin: 120_000m, salaryMax: 140_000m, salaryText: "$120,000 - $140,000", employmentType: "Full-time");
        AddJob(dbContext, "B", salaryMin: 150_000m, salaryMax: 170_000m, salaryText: "$150,000 - $170,000", employmentType: "Full-time");
        AddJob(dbContext, "C", salaryMin: 85m, salaryText: "$85 per hour", salaryPeriod: "Hour", employmentType: "Contract");
        AddJob(dbContext, "D"); // no salary
        await dbContext.SaveChangesAsync();

        var overview = await new AnalyticsService(dbContext, new FixedTimeProvider(Now)).GetOverviewAsync(CancellationToken.None);

        overview.Salary.EligibleCount.Should().Be(2);
        overview.Salary.CohortCount.Should().Be(4);
        overview.Salary.CoveragePercent.Should().Be(50m);
        overview.Salary.MedianAnnualNzd.Should().BeNull("fewer than five comparable observations suppresses the median");
        overview.Salary.Suppressed.Should().BeTrue();
        overview.Salary.Exclusions["non-annual"].Should().Be(1);
        overview.Salary.Exclusions["missing"].Should().Be(1);
        overview.Salary.Currency.Should().Be("NZD");
        overview.Salary.PayPeriod.Should().Be("Year");
    }

    [Fact]
    public async Task Overview_retains_the_last_dataset_and_reports_unavailable_when_every_crawl_failed()
    {
        await using var dbContext = CreateDbContext();
        AddJob(dbContext, "A");
        AddJob(dbContext, "B");
        dbContext.CrawlRuns.Add(new CrawlRun
        {
            Id = Guid.NewGuid(),
            Source = "Acme",
            Status = "Failed",
            StartedAt = Now.AddHours(-1),
            FinishedAt = Now.AddHours(-1),
            ErrorMessage = "HTTP 403"
        });
        dbContext.JobSources.Add(new JobSource
        {
            Id = Guid.NewGuid(),
            Name = "Acme",
            Method = "scrapling",
            Parser = "generic",
            Enabled = true,
            BaseUrl = "https://acme.example",
            CreatedAt = Now.AddDays(-1),
            UpdatedAt = Now.AddDays(-1)
        });
        await dbContext.SaveChangesAsync();

        var overview = await new AnalyticsService(dbContext, new FixedTimeProvider(Now)).GetOverviewAsync(CancellationToken.None);

        overview.ActiveListings.Should().Be(2, "a failed refresh must not masquerade as zero jobs");
        overview.DataStatus.State.Should().Be(nameof(DataFreshnessState.Unavailable));
        overview.DataStatus.Message.Should().Contain("zero-demand");
    }

    [Fact]
    public async Task Freshness_exposes_both_date_bases_and_declares_missing_history()
    {
        await using var dbContext = CreateDbContext();
        AddJob(dbContext, "A", posted: new DateTime(2026, 10, 13, 0, 0, 0, DateTimeKind.Utc), firstSeen: new DateTime(2026, 10, 13, 0, 0, 0, DateTimeKind.Utc));
        AddJob(dbContext, "B", posted: null, firstSeen: new DateTime(2026, 10, 12, 0, 0, 0, DateTimeKind.Utc));
        await dbContext.SaveChangesAsync();

        var freshness = await new AnalyticsService(dbContext, new FixedTimeProvider(Now)).GetFreshnessAsync(CancellationToken.None);

        freshness.TotalActiveJobs.Should().Be(2);
        var seven = freshness.Windows.Single(window => window.Days == 7);
        seven.NewlyPosted.Should().Be(1);
        seven.NewlyDiscovered.Should().Be(2);
        seven.PostedDateKnown.Should().Be(1);
        seven.PostedDateMissing.Should().Be(1);
        seven.ComparisonAvailable.Should().BeTrue();

        var fifteen = freshness.Windows.Single(window => window.Days == 15);
        fifteen.ComparisonAvailable.Should().BeFalse("the prior 15-day period predates the retention window");
    }

    [Fact]
    public async Task Overview_marks_a_partial_dataset_when_a_source_has_not_refreshed()
    {
        await using var dbContext = CreateDbContext();
        AddJob(dbContext, "A");
        dbContext.CrawlRuns.Add(new CrawlRun
        {
            Id = Guid.NewGuid(),
            Source = "Acme",
            Status = "Completed",
            StartedAt = Now.AddHours(-2),
            FinishedAt = Now.AddHours(-2)
        });
        dbContext.JobSources.AddRange(
            new JobSource { Id = Guid.NewGuid(), Name = "Acme", Method = "scraping", Parser = "generic", Enabled = true, BaseUrl = "https://acme.example", LastCrawledAt = Now.AddHours(-2), CreatedAt = Now, UpdatedAt = Now },
            new JobSource { Id = Guid.NewGuid(), Name = "Beta", Method = "scraping", Parser = "generic", Enabled = true, BaseUrl = "https://beta.example", LastCrawledAt = Now.AddDays(-4), CreatedAt = Now, UpdatedAt = Now });
        await dbContext.SaveChangesAsync();

        var overview = await new AnalyticsService(dbContext, new FixedTimeProvider(Now)).GetOverviewAsync(CancellationToken.None);

        overview.DataStatus.State.Should().Be(nameof(DataFreshnessState.Partial));
        overview.DataStatus.EnabledSources.Should().Be(2);
        overview.DataStatus.EnabledSourcesWithRecentSuccess.Should().Be(1);
    }

    [Fact]
    public async Task Overview_separates_direct_employers_from_agency_only_listings()
    {
        await using var dbContext = CreateDbContext();
        AddJob(dbContext, "Xero", company: "Xero");
        AddJob(dbContext, "Beyond Recruitment", company: "Beyond Recruitment");
        AddJob(dbContext, "Tribe Recruitment", company: "Tau Mai");
        AddJob(dbContext, "SEEK NZ", company: null);
        dbContext.JobSources.AddRange(
            new JobSource { Id = Guid.NewGuid(), Name = "Xero", Method = "scraping", Parser = "jsonld", Category = "company", BaseUrl = "https://xero.example", CreatedAt = Now, UpdatedAt = Now },
            new JobSource { Id = Guid.NewGuid(), Name = "Beyond Recruitment", Method = "scraping", Parser = "generic", Category = "recruiter", BaseUrl = "https://beyond.example", CreatedAt = Now, UpdatedAt = Now },
            new JobSource { Id = Guid.NewGuid(), Name = "Tribe Recruitment", Method = "scraping", Parser = "generic", Category = "recruiter", BaseUrl = "https://tribe.example", CreatedAt = Now, UpdatedAt = Now },
            new JobSource { Id = Guid.NewGuid(), Name = "SEEK NZ", Method = "scraping", Parser = "seek", Category = "job-board", BaseUrl = "https://seek.example", CreatedAt = Now, UpdatedAt = Now });
        await dbContext.SaveChangesAsync();

        var overview = await new AnalyticsService(dbContext, new FixedTimeProvider(Now)).GetOverviewAsync(CancellationToken.None);

        overview.Employers.DirectEmployerListings.Should().Be(1);
        overview.Employers.AgencyAdvertisedListings.Should().Be(2);
        overview.Employers.BoardAdvertisedListings.Should().Be(1);
        overview.Employers.AgencyOnlyListings.Should().Be(1, "Beyond Recruitment advertises its own brand");
        overview.Employers.ListingsWithEmployer.Should().Be(2, "Xero and the named Tau Mai client");
        overview.Employers.ListingsWithoutEmployer.Should().Be(2);
        overview.Employers.IdentifiableEmployers.Should().Be(2);
    }

    private static void AddJob(
        ApplicationDbContext dbContext,
        string source,
        bool active = true,
        DateTime? posted = null,
        DateTime? firstSeen = null,
        decimal? salaryMin = null,
        decimal? salaryMax = null,
        string? salaryText = null,
        string salaryPeriod = "Year",
        string employmentType = "Full-time",
        string? company = null)
    {
        var seen = firstSeen ?? new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        dbContext.Jobs.Add(new Job
        {
            Id = Guid.NewGuid(),
            Source = source,
            Title = "Software Engineer",
            Company = company,
            Url = $"https://{source.ToLowerInvariant()}.example/jobs/{Guid.NewGuid()}",
            ContentHash = Guid.NewGuid().ToString(),
            IsActive = active,
            PostedDate = posted,
            FirstSeenAt = seen,
            LastSeenAt = Now,
            CreatedAt = seen,
            UpdatedAt = Now,
            SalaryMin = salaryMin,
            SalaryMax = salaryMax,
            SalaryText = salaryText,
            SalaryCurrency = "NZD",
            SalaryPeriod = salaryPeriod,
            EmploymentType = employmentType
        });
    }

    private static ApplicationDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        var dbContext = new ApplicationDbContext(options);
        dbContext.Database.EnsureCreated();
        return dbContext;
    }

    private sealed class FixedTimeProvider(DateTime nowUtc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() =>
            new(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc));
    }
}
