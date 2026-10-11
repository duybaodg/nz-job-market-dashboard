using Application.Jobs;
using Domain.Entities;
using FluentAssertions;

namespace Application.Tests;

public sealed class CrawlDataRetentionServiceTests
{
    private static readonly DateTime Now = new(2026, 10, 12, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Cutoff_subtracts_the_retention_window()
    {
        CrawlDataRetentionService.Cutoff(Now, 20).Should().Be(Now.AddDays(-20));
    }

    [Fact]
    public void Cutoff_clamps_non_positive_windows_to_one_day()
    {
        CrawlDataRetentionService.Cutoff(Now, 0).Should().Be(Now.AddDays(-1));
        CrawlDataRetentionService.Cutoff(Now, -5).Should().Be(Now.AddDays(-1));
    }

    // Convention: strictly older than the window is deleted, so a row that is
    // exactly 20 days old is KEPT.
    [Theory]
    [InlineData(19.5, false)]
    [InlineData(19.99999, false)]
    [InlineData(20.0, false)]
    [InlineData(20.00001, true)]
    [InlineData(30.0, true)]
    public void IsExpired_uses_a_strict_boundary(double ageInDays, bool expected)
    {
        var lastSeenAt = Now.AddDays(-ageInDays);

        CrawlDataRetentionService.IsExpired(lastSeenAt, lastSeenAt, Now, 20).Should().Be(expected);
    }

    [Fact]
    public void IsExpired_falls_back_to_created_at_when_last_seen_was_never_written()
    {
        CrawlDataRetentionService.IsExpired(default, Now.AddDays(-30), Now, 20).Should().BeTrue();
        CrawlDataRetentionService.IsExpired(default, Now.AddDays(-20), Now, 20).Should().BeFalse();
        CrawlDataRetentionService.IsExpired(default, Now.AddDays(-1), Now, 20).Should().BeFalse();
    }

    [Fact]
    public void IsExpired_prefers_last_seen_at_over_created_at_when_set()
    {
        CrawlDataRetentionService.IsExpired(Now.AddDays(-1), Now.AddDays(-100), Now, 20).Should().BeFalse();
        CrawlDataRetentionService.IsExpired(Now.AddDays(-30), Now.AddDays(-1), Now, 20).Should().BeTrue();
    }

    [Fact]
    public void ExpiredJob_expression_matches_the_date_math_used_by_the_delete()
    {
        var cutoff = CrawlDataRetentionService.Cutoff(Now, 20);
        var predicate = CrawlDataRetentionService.ExpiredJob(cutoff).Compile();

        predicate(CreateJob(Now.AddDays(-20))).Should().BeFalse();
        predicate(CreateJob(Now.AddDays(-20).AddSeconds(-1))).Should().BeTrue();
        predicate(CreateJob(Now.AddDays(-1))).Should().BeFalse();
        predicate(CreateJob(default, Now.AddDays(-30))).Should().BeTrue();
        predicate(CreateJob(default, Now.AddDays(-1))).Should().BeFalse();
    }

    [Fact]
    public void ExpiredCrawlRun_expression_uses_started_at_and_the_same_boundary()
    {
        var cutoff = CrawlDataRetentionService.Cutoff(Now, 20);
        var predicate = CrawlDataRetentionService.ExpiredCrawlRun(cutoff).Compile();

        predicate(new CrawlRun { StartedAt = Now.AddDays(-20) }).Should().BeFalse();
        predicate(new CrawlRun { StartedAt = Now.AddDays(-20).AddSeconds(-1) }).Should().BeTrue();
        predicate(new CrawlRun { StartedAt = Now.AddDays(-1) }).Should().BeFalse();
    }

    private static Job CreateJob(DateTime lastSeenAt, DateTime? createdAt = null) => new()
    {
        Id = Guid.NewGuid(),
        Source = "Acme",
        Title = "Senior Data Engineer",
        Url = "https://acme.example/jobs/1",
        ContentHash = Guid.NewGuid().ToString(),
        IsActive = true,
        FirstSeenAt = createdAt ?? lastSeenAt,
        LastSeenAt = lastSeenAt,
        CreatedAt = createdAt ?? lastSeenAt,
        UpdatedAt = createdAt ?? lastSeenAt
    };
}
