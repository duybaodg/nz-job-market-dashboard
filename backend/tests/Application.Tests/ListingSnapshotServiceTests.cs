using Application.History;
using Domain.Entities;
using FluentAssertions;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Application.Tests;

public sealed class ListingSnapshotServiceTests
{
    // 2026-10-15T00:30Z is 2026-10-15 13:30 NZDT, so the snapshot date is 2026-10-15.
    private static readonly DateTime Now = new(2026, 10, 15, 0, 30, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Capture_writes_listing_rows_and_a_coverage_run_then_is_idempotent()
    {
        await using var dbContext = CreateDbContext();
        AddJob(dbContext, "Xero", "company", "Xero", active: true);
        AddJob(dbContext, "Beyond Recruitment", "recruiter", "Beyond Recruitment", active: false);
        await dbContext.SaveChangesAsync();

        var service = new ListingSnapshotService(dbContext, new TestClock(Now));
        var first = await service.CaptureAsync(CancellationToken.None);

        first.Captured.Should().BeTrue();
        first.SnapshotDate.Should().Be(new DateOnly(2026, 10, 15));
        first.Listings.Should().Be(2);
        first.Active.Should().Be(1);

        var rows = await dbContext.ListingSnapshots.ToListAsync();
        rows.Should().HaveCount(2);
        rows.Single(row => row.Source == "Xero").HiringEmployer.Should().Be("Xero");
        rows.Single(row => row.Source == "Beyond Recruitment").HiringEmployer.Should().BeNull();
        rows.Single(row => row.Source == "Beyond Recruitment").AgencyAdvertised.Should().BeTrue();
        rows.Should().OnlyContain(row => row.RoleFamily == "Software Engineering");

        var run = await dbContext.ListingSnapshotRuns.SingleAsync();
        run.ListingCount.Should().Be(2);
        run.ActiveCount.Should().Be(1);
        run.SourceCount.Should().Be(2);
        run.SourceCoverageJson.Should().Contain("Xero");

        var second = await service.CaptureAsync(CancellationToken.None);
        second.Captured.Should().BeFalse("the same NZ day is only captured once");
        (await dbContext.ListingSnapshots.CountAsync()).Should().Be(2);
    }

    private static void AddJob(ApplicationDbContext dbContext, string source, string category, string? company, bool active)
    {
        dbContext.JobSources.Add(new JobSource
        {
            Id = Guid.NewGuid(),
            Name = source,
            Method = "Scraping",
            Parser = "generic",
            Category = category,
            BaseUrl = $"https://{source.Replace(" ", "").ToLowerInvariant()}.example",
            CreatedAt = Now,
            UpdatedAt = Now
        });
        dbContext.Jobs.Add(new Job
        {
            Id = Guid.NewGuid(),
            Source = source,
            Title = "Software Engineer",
            Company = company,
            Url = $"https://example.com/{Guid.NewGuid()}",
            ContentHash = Guid.NewGuid().ToString(),
            IsActive = active,
            FirstSeenAt = Now.AddDays(-2),
            LastSeenAt = Now,
            CreatedAt = Now.AddDays(-2),
            UpdatedAt = Now
        });
    }

    private static ApplicationDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        return new ApplicationDbContext(options);
    }
}
