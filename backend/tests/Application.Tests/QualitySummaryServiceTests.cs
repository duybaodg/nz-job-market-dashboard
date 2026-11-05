using Application.Analytics;
using Application.Metrics;
using Domain.Entities;
using FluentAssertions;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Application.Tests;

public sealed class QualitySummaryServiceTests
{
    private static readonly DateTime Now = new(2026, 11, 5, 0, 30, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Every_public_measure_carries_a_denominator_and_classification_is_unavailable_without_labels()
    {
        await using var dbContext = CreateDbContext();
        AddSource(dbContext, "Xero");
        AddSource(dbContext, "Beyond Recruitment");
        var dataJob = AddJob(dbContext, "Xero", "Data Engineer", employerId: null);
        AddJob(dbContext, "Beyond Recruitment", "Support Analyst", employerId: null);
        dbContext.JobSkills.Add(new JobSkill
        {
            Id = Guid.NewGuid(),
            JobId = dataJob,
            SkillName = "SQL",
            SkillType = "ProgrammingLanguage"
        });
        dbContext.CrawlRuns.AddRange(
            new CrawlRun
            {
                Id = Guid.NewGuid(),
                Source = "Xero",
                Status = "Completed",
                StartedAt = Now.AddHours(-2),
                FinishedAt = Now.AddHours(-1)
            },
            new CrawlRun
            {
                Id = Guid.NewGuid(),
                Source = "Beyond Recruitment",
                Status = "Completed",
                StartedAt = Now.AddHours(-2),
                FinishedAt = Now.AddHours(-1)
            });
        dbContext.ListingSnapshotRuns.Add(new ListingSnapshotRun
        {
            Id = Guid.NewGuid(),
            SnapshotDate = new DateOnly(2026, 11, 5),
            CapturedAtUtc = Now.AddHours(-3),
            ListingCount = 2,
            ActiveCount = 2,
            SourceCount = 2,
            SourceCoverageJson = "{}"
        });
        await dbContext.SaveChangesAsync();

        var analytics = new AnalyticsService(dbContext, new TestClock(Now));
        var service = new QualitySummaryService(dbContext, analytics, new TestClock(Now));
        var summary = await service.GetQualitySummaryAsync(CancellationToken.None);

        summary.ActiveListings.Should().Be(2);
        summary.Measures.Select(measure => measure.Key).Should().Equal(
            "source-refresh-coverage",
            "comparable-salary-share",
            "unknown-role-share",
            "skill-extraction-coverage",
            "employer-resolution",
            "deduplication-status",
            "snapshot-completeness");
        summary.Measures.Should().OnlyContain(measure => measure.Denominator != null);
        summary.Measures.Should().OnlyContain(measure => measure.CapturedAtUtc != default);
        summary.Measures.Single(measure => measure.Key == "skill-extraction-coverage")
            .Numerator.Should().Be(1);
        summary.Measures.Single(measure => measure.Key == "snapshot-completeness")
            .DisplayValue.Should().Contain("1 of 1");
        summary.Classification.Available.Should().BeFalse("no labelled sample is bundled in this fixture");
        summary.Classification.AccuracyPercent.Should().BeNull();
        summary.Classification.RuleVersion.Should().Be("role-family-v1");
    }

    private static JobSource AddSource(ApplicationDbContext dbContext, string name)
    {
        var source = new JobSource
        {
            Id = Guid.NewGuid(),
            Name = name,
            BaseUrl = $"https://{name.Replace(" ", "").ToLowerInvariant()}.example",
            Method = "Scraping",
            Parser = "generic",
            Category = "company",
            Enabled = true,
            CreatedAt = Now.AddDays(-10),
            UpdatedAt = Now.AddDays(-10)
        };
        dbContext.JobSources.Add(source);
        return source;
    }

    private static Guid AddJob(ApplicationDbContext dbContext, string source, string title, Guid? employerId)
    {
        var id = Guid.NewGuid();
        dbContext.Jobs.Add(new Job
        {
            Id = id,
            Source = source,
            Title = title,
            Company = source,
            Url = $"https://example.com/{id}",
            ContentHash = id.ToString(),
            IsActive = true,
            EmploymentType = "Permanent",
            SalaryMin = 100000,
            SalaryMax = 120000,
            SalaryCurrency = "NZD",
            SalaryPeriod = "Year",
            FirstSeenAt = Now.AddDays(-2),
            LastSeenAt = Now,
            CreatedAt = Now.AddDays(-2),
            UpdatedAt = Now,
            EmployerCanonicalId = employerId
        });
        return id;
    }

    private static ApplicationDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ApplicationDbContext(options);
    }
}
