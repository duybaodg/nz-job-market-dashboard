using Application.Health;
using Application.Jobs;
using Domain.Entities;
using FluentAssertions;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Application.Tests;

public sealed class HealthServiceTests
{
    private static readonly DateTime Now = new(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task ComputeAsync_aggregates_live_jobs_and_consecutive_failures()
    {
        await using var dbContext = CreateDbContext();
        dbContext.JobSources.Add(Source("Acme", enabled: true, lastCrawledAt: Now.AddHours(-1)));
        dbContext.CrawlRuns.AddRange(
            Run("Acme", "Failed", Now.AddMinutes(-5), "HTTP 403 from WAF"),
            Run("Acme", "Failed", Now.AddHours(-1)),
            Run("Acme", "Failed", Now.AddHours(-2)),
            Run("Acme", "Completed", Now.AddHours(-3), found: 4));
        dbContext.Jobs.AddRange(
            Job("Acme", active: true),
            Job("Acme", active: true),
            Job("Acme", active: true),
            Job("Acme", active: true),
            Job("Acme", active: false),
            Job("Other", active: true));
        await dbContext.SaveChangesAsync();

        var report = await new HealthService(dbContext, new HealthReportCache()).ComputeAsync(Now, CancellationToken.None);

        var acme = report.Sources.Single(source => source.Source == "Acme");
        acme.State.Should().Be(SourceHealthState.Degraded);
        acme.ConsecutiveFailures.Should().Be(3);
        acme.JobsYielded.Should().Be(4);
        acme.LastStatus.Should().Be("Failed");
        acme.LastError.Should().Be("HTTP 403 from WAF");
        acme.Enabled.Should().BeTrue();
        report.Summary.Degraded.Should().Be(1);
        report.Summary.TotalSources.Should().Be(1);
    }

    [Fact]
    public async Task ComputeAsync_classifies_disabled_and_never_run_sources()
    {
        await using var dbContext = CreateDbContext();
        dbContext.JobSources.AddRange(
            Source("Zed", enabled: false, lastCrawledAt: null),
            Source("Beta", enabled: true, lastCrawledAt: null));
        await dbContext.SaveChangesAsync();

        var report = await new HealthService(dbContext, new HealthReportCache()).ComputeAsync(Now, CancellationToken.None);

        report.Sources.Single(source => source.Source == "Zed").State.Should().Be(SourceHealthState.Disabled);
        report.Sources.Single(source => source.Source == "Beta").State.Should().Be(SourceHealthState.NeverRun);
        report.Summary.Disabled.Should().Be(1);
        report.Summary.NeverRun.Should().Be(1);
    }

    [Fact]
    public async Task ComputeAsync_marks_a_source_stale_beyond_three_frequencies()
    {
        await using var dbContext = CreateDbContext();
        dbContext.JobSources.Add(Source("Acme", enabled: true, lastCrawledAt: Now.AddDays(-5), frequencyMinutes: 1440));
        dbContext.CrawlRuns.Add(Run("Acme", "Completed", Now.AddDays(-5), found: 0));
        await dbContext.SaveChangesAsync();

        var report = await new HealthService(dbContext, new HealthReportCache()).ComputeAsync(Now, CancellationToken.None);

        report.Sources.Single().State.Should().Be(SourceHealthState.Stale);
        report.Summary.Stale.Should().Be(1);
    }

    [Fact]
    public async Task ComputeAsync_reports_an_empty_but_healthy_board()
    {
        await using var dbContext = CreateDbContext();
        dbContext.JobSources.Add(Source("Acme", enabled: true, lastCrawledAt: Now.AddHours(-2)));
        dbContext.CrawlRuns.Add(Run("Acme", "Completed", Now.AddHours(-2), found: 0));
        await dbContext.SaveChangesAsync();

        var report = await new HealthService(dbContext, new HealthReportCache()).ComputeAsync(Now, CancellationToken.None);

        var acme = report.Sources.Single();
        acme.State.Should().Be(SourceHealthState.Empty);
        acme.JobsYielded.Should().Be(0);
        report.Summary.Empty.Should().Be(1);
    }

    [Fact]
    public async Task GetHealthAsync_serves_a_cached_report_for_thirty_seconds()
    {
        await using var dbContext = CreateDbContext();
        dbContext.JobSources.Add(Source("Acme", enabled: true, lastCrawledAt: DateTime.UtcNow));
        await dbContext.SaveChangesAsync();
        var cache = new HealthReportCache();
        var service = new HealthService(dbContext, cache);

        var first = await service.GetHealthAsync(CancellationToken.None);
        var second = await service.GetHealthAsync(CancellationToken.None);

        second.Should().BeSameAs(first);

        cache.Clear();
        var third = await service.GetHealthAsync(CancellationToken.None);
        third.Should().NotBeSameAs(first);
    }

    [Fact]
    public void HealthReportCache_expires_at_the_thirty_second_boundary()
    {
        var cache = new HealthReportCache();
        var report = new HealthReportDto(Now, new HealthSummaryDto(0, 0, 0, 0, 0, 0, 0, 0), [], []);
        cache.Set(Now, report, TimeSpan.FromSeconds(30));

        cache.TryGet(Now.AddSeconds(29.999), out var fresh).Should().BeTrue();
        fresh.Should().BeSameAs(report);

        cache.TryGet(Now.AddSeconds(30), out var expired).Should().BeFalse();
        expired.Should().BeNull();

        // An expired entry is dropped, not merely skipped.
        cache.TryGet(Now, out _).Should().BeFalse();
    }

    [Fact]
    public async Task ComputeAsync_moves_registry_removals_to_the_retired_bucket()
    {
        // Regression for the live divergence: rows removed from sites.json were
        // left as ordinary 'disabled' rows, so the health report kept returning
        // them and its totals did not match the current registry.
        await using var dbContext = CreateDbContext();
        dbContext.JobSources.AddRange(
            Source("Current", enabled: true, lastCrawledAt: Now.AddHours(-1)),
            Source("Gone", enabled: false, lastCrawledAt: Now.AddDays(-3), retiredAt: Now.AddDays(-1)));
        dbContext.CrawlRuns.Add(Run("Gone", "Completed", Now.AddDays(-3), found: 2));
        dbContext.Jobs.AddRange(Job("Current", active: true), Job("Gone", active: true));
        await dbContext.SaveChangesAsync();

        var report = await new HealthService(dbContext, new HealthReportCache()).ComputeAsync(Now, CancellationToken.None);

        report.Sources.Select(source => source.Source).Should().NotContain("Gone");
        report.Sources.Should().ContainSingle(source => source.Source == "Current");
        report.RetiredSources.Should().ContainSingle(source => source.Source == "Gone");
        report.RetiredSources.Single().State.Should().Be(SourceHealthState.Retired);
        report.RetiredSources.Single().DegradeReason.Should().Contain("Removed from the source registry");
        report.Summary.Retired.Should().Be(1);
        report.Summary.TotalSources.Should().Be(1);
        report.Sources.Should().HaveCount(report.Summary.TotalSources);
    }

    [Fact]
    public async Task ComputeAsync_jobsYielded_matches_the_active_jobs_feed_per_source()
    {
        // The health tab must not disagree with the live jobs feed. Both read
        // active jobs, so the per-source counts have to be identical.
        await using var dbContext = CreateDbContext();
        dbContext.JobSources.AddRange(
            Source("Acme", enabled: true, lastCrawledAt: Now.AddHours(-1)),
            Source("Beta", enabled: true, lastCrawledAt: Now.AddHours(-1)));
        dbContext.Jobs.AddRange(
            Job("Acme", active: true),
            Job("Acme", active: true),
            Job("Acme", active: false),
            Job("Beta", active: true));
        await dbContext.SaveChangesAsync();

        var report = await new HealthService(dbContext, new HealthReportCache()).ComputeAsync(Now, CancellationToken.None);
        var feed = await new JobService(dbContext).GetJobsAsync(
            new JobListQuery(null, null, null, null, null),
            CancellationToken.None);

        var feedCounts = feed
            .GroupBy(job => job.Source, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

        foreach (var entry in report.Sources)
        {
            entry.JobsYielded.Should().Be(feedCounts.TryGetValue(entry.Source, out var count) ? count : 0, entry.Source);
        }

        report.Sources.Where(entry => entry.JobsYielded > 0).Select(entry => entry.Source)
            .Should().BeEquivalentTo(feedCounts.Keys);
    }

    private static JobSource Source(string name, bool enabled, DateTime? lastCrawledAt, int frequencyMinutes = 1440, DateTime? retiredAt = null) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        BaseUrl = $"https://{name.ToLowerInvariant()}.example/jobs",
        Method = "Scrapling",
        Parser = "generic",
        Enabled = enabled,
        CrawlFrequencyMinutes = frequencyMinutes,
        LastCrawledAt = lastCrawledAt,
        RetiredAt = retiredAt,
        CreatedAt = Now.AddDays(-30),
        UpdatedAt = Now.AddDays(-30)
    };

    private static CrawlRun Run(string source, string status, DateTime startedAt, string? error = null, int found = 0) => new()
    {
        Id = Guid.NewGuid(),
        Source = source,
        Status = status,
        StartedAt = startedAt,
        FinishedAt = startedAt.AddMinutes(1),
        TotalJobsFound = found,
        ErrorMessage = error
    };

    private static Job Job(string source, bool active) => new()
    {
        Id = Guid.NewGuid(),
        Source = source,
        Title = "Software Engineer",
        Url = $"https://{source.ToLowerInvariant()}.example/jobs/{Guid.NewGuid()}",
        ContentHash = Guid.NewGuid().ToString(),
        IsActive = active,
        FirstSeenAt = Now.AddDays(-1),
        LastSeenAt = Now,
        CreatedAt = Now.AddDays(-1),
        UpdatedAt = Now
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
}
