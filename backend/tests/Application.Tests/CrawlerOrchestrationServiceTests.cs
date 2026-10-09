using Application.Crawling;
using Application.Deduplication;
using Application.Normalisation;
using FluentAssertions;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Domain.Entities;

namespace Application.Tests;

public sealed class CrawlerOrchestrationServiceTests
{
    [Fact]
    public async Task StartCrawlRunAsync_stores_raw_page_and_saves_new_jobs()
    {
        await using var dbContext = CreateDbContext();
        var adapter = new FakeJobSourceAdapter();
        var service = new CrawlerOrchestrationService(
            dbContext,
            [adapter],
            new DeduplicationService(),
            new NormalisationService());

        var result = await service.StartCrawlRunAsync(
            new StartCrawlRunRequest("Firecrawl", "https://example.com/search"),
            CancellationToken.None);

        result.Started.Should().BeTrue();
        result.CrawlRun.Status.Should().Be("Completed");
        result.CrawlRun.TotalPages.Should().Be(1);
        result.CrawlRun.TotalJobsFound.Should().Be(1);
        result.CrawlRun.TotalJobsSaved.Should().Be(1);

        dbContext.RawJobPages.Should().ContainSingle(page => page.Url == "https://example.com/search");
        dbContext.Jobs.Should().ContainSingle(job => job.Title == "Junior React Developer");
        dbContext.JobSkills.Should().Contain(skill => skill.SkillName == "React");
    }

    [Fact]
    public async Task StartCrawlRunAsync_returns_failure_without_registered_adapter()
    {
        await using var dbContext = CreateDbContext();
        var service = new CrawlerOrchestrationService(
            dbContext,
            [],
            new DeduplicationService(),
            new NormalisationService());

        var result = await service.StartCrawlRunAsync(
            new StartCrawlRunRequest("Missing", "https://example.com/search"),
            CancellationToken.None);

        result.Started.Should().BeFalse();
        result.CrawlRun.Status.Should().Be("Failed");
        result.Message.Should().Contain("No crawl adapter");
    }

    [Fact]
    public async Task StartCrawlRunAsync_prefers_explicit_work_mode_metadata()
    {
        await using var dbContext = CreateDbContext();
        var job = new RawJobInput(
            "Halter", "1", "Software Engineer", "Halter", "Auckland", null,
            "Employment type: Full-time. Work mode: On-site. Requires 6+ years of experience with Python and AWS. Support remote field devices.",
            "https://example.com/jobs/1", null, null);
        var service = new CrawlerOrchestrationService(
            dbContext, [new FakeJobSourceAdapter([job])], new DeduplicationService(), new NormalisationService());

        await service.StartCrawlRunAsync(new("Firecrawl", "https://example.com/search"), CancellationToken.None);

        dbContext.Jobs.Single().WorkMode.Should().Be("On-site");
        dbContext.Jobs.Single().EmploymentType.Should().Be("Full-time");
        dbContext.Jobs.Single().ExperienceBand.Should().Be("More than 5 years");
        dbContext.Jobs.Single().Skills.Should().Contain(skill => skill.SkillName == "Python" && skill.SkillType == "ProgrammingLanguage");
    }

    [Fact]
    public async Task StartCrawlRunAsync_refreshes_existing_job_insights()
    {
        await using var dbContext = CreateDbContext();
        var existing = CreateJob("https://example.com/jobs/refresh");
        existing.Skills.Add(new JobSkill { Id = Guid.NewGuid(), SkillName = "React", SkillType = "Keyword" });
        dbContext.Jobs.Add(existing);
        await dbContext.SaveChangesAsync();
        var raw = new RawJobInput("Firecrawl", null, "Test Engineer", "Example", "Wellington Central", null,
            "Requires 4 years of experience using Python.", existing.Url, null, null);
        var service = new CrawlerOrchestrationService(
            dbContext, [new FakeJobSourceAdapter([raw])], new DeduplicationService(), new NormalisationService());

        await service.StartCrawlRunAsync(new("Firecrawl", "https://example.com/search"), CancellationToken.None);

        dbContext.Jobs.Single().ExperienceBand.Should().Be("2-5 years");
        dbContext.JobSkills.Should().ContainSingle(skill => skill.SkillName == "Python" && skill.SkillType == "ProgrammingLanguage");
    }

    [Fact]
    public async Task StartCrawlRunAsync_expires_closed_jobs_and_jobs_missing_twice()
    {
        await using var dbContext = CreateDbContext();
        dbContext.Jobs.AddRange(
            CreateJob("https://example.com/jobs/missing"),
            CreateJob("https://example.com/jobs/closed", DateTime.UtcNow.AddDays(-1)));
        await dbContext.SaveChangesAsync();
        var service = new CrawlerOrchestrationService(
            dbContext,
            [new FakeJobSourceAdapter([])],
            new DeduplicationService(),
            new NormalisationService());

        await service.StartCrawlRunAsync(new("Firecrawl", "https://example.com/search"), CancellationToken.None);

        dbContext.Jobs.Single(job => job.Url.EndsWith("missing")).IsActive.Should().BeTrue();
        dbContext.Jobs.Single(job => job.Url.EndsWith("missing")).MissingCrawlCount.Should().Be(1);
        dbContext.Jobs.Single(job => job.Url.EndsWith("closed")).IsActive.Should().BeFalse();

        await service.StartCrawlRunAsync(new("Firecrawl", "https://example.com/search"), CancellationToken.None);

        dbContext.Jobs.Single(job => job.Url.EndsWith("missing")).IsActive.Should().BeFalse();
    }

    private static Job CreateJob(string url, DateTime? closingDate = null) => new()
    {
        Id = Guid.NewGuid(),
        Source = "Firecrawl",
        Title = "Test Engineer",
        Url = url,
        ContentHash = Guid.NewGuid().ToString(),
        ClosingDate = closingDate,
        IsActive = true,
        FirstSeenAt = DateTime.UtcNow,
        LastSeenAt = DateTime.UtcNow,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    private static ApplicationDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        var dbContext = new ApplicationDbContext(options);
        dbContext.Database.EnsureCreated();
        return dbContext;
    }

    private sealed class FakeJobSourceAdapter(IReadOnlyList<RawJobInput>? resultJobs = null) : IJobSourceAdapter
    {
        public string Method => "Firecrawl";

        public Task<SourceFetchResult> FetchJobsAsync(
            JobSearchRequest request,
            CancellationToken cancellationToken)
        {
            var rawPage = new RawJobPageInput(
                request.Source,
                request.Url,
                "<html></html>",
                "# Junior React Developer",
                """{"success":true}""",
                DateTime.UtcNow);

            var jobs = resultJobs ??
            [
                new RawJobInput(
                    request.Source,
                    null,
                    "Junior React Developer",
                    "Koru Digital",
                    "Wellington",
                    "$65k - $80k",
                    "Full-time hybrid role using React and TypeScript.",
                    "https://example.com/jobs/react",
                    null,
                    null)
            ];

            return Task.FromResult(new SourceFetchResult([rawPage], jobs, true));
        }
    }
}
