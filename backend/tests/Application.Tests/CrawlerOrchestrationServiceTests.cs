using Application.Crawling;
using Application.Deduplication;
using Application.Normalisation;
using FluentAssertions;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

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
        result.Message.Should().Contain("No source adapter");
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

    private sealed class FakeJobSourceAdapter : IJobSourceAdapter
    {
        public string SourceName => "Firecrawl";

        public Task<SourceFetchResult> FetchJobsAsync(
            JobSearchRequest request,
            CancellationToken cancellationToken)
        {
            var rawPage = new RawJobPageInput(
                SourceName,
                request.Url,
                "<html></html>",
                "# Junior React Developer",
                """{"success":true}""",
                DateTime.UtcNow);

            var jobs = new[]
            {
                new RawJobInput(
                    SourceName,
                    null,
                    "Junior React Developer",
                    "Koru Digital",
                    "Wellington",
                    "$65k - $80k",
                    "Full-time hybrid role using React and TypeScript.",
                    "https://example.com/jobs/react",
                    null,
                    null)
            };

            return Task.FromResult(new SourceFetchResult(rawPage, jobs));
        }
    }
}
