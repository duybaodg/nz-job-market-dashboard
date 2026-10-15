using Application.History;
using Domain.Entities;
using FluentAssertions;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Application.Tests;

public sealed class HistoryServiceTests
{
    // 2026-10-15T00:30Z is 2026-10-15 13:30 NZDT. The 7 complete-day interval is
    // [2026-10-08, 2026-10-15) NZ and the previous equal period is
    // [2026-10-01, 2026-10-08) NZ.
    private static readonly DateTime Now = new(2026, 10, 15, 0, 30, 0, DateTimeKind.Utc);

    private sealed record Listing(string Hash, string Source, DateTime FirstSeen, DateOnly ActiveFrom);

    private static readonly Listing[] Listings =
    [
        new("x", "Alpha", new DateTime(2026, 9, 28, 0, 0, 0, DateTimeKind.Utc), new DateOnly(2026, 9, 28)),
        new("a", "Alpha", new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), new DateOnly(2026, 10, 1)),
        new("b", "Beta", new DateTime(2026, 10, 8, 0, 0, 0, DateTimeKind.Utc), new DateOnly(2026, 10, 8)),
        new("c", "Gamma", new DateTime(2026, 10, 9, 0, 0, 0, DateTimeKind.Utc), new DateOnly(2026, 10, 9)),
        new("d", "Delta", new DateTime(2026, 10, 14, 0, 0, 0, DateTimeKind.Utc), new DateOnly(2026, 10, 14))
    ];

    [Fact]
    public async Task Missing_history_stays_missing_and_never_fabricates_a_trend()
    {
        await using var dbContext = CreateDbContext();

        var history = await new HistoryService(dbContext, new TestClock(Now)).GetHistoryAsync(7, CancellationToken.None);

        history.Coverage.HasHistory.Should().BeFalse();
        history.WeeklyNewListings.Should().BeEmpty();
        history.ActiveTrend.Should().BeEmpty();
        history.ActiveChange.ComparisonAvailable.Should().BeFalse();
        history.NewListings.Change.ComparisonAvailable.Should().BeFalse();
        history.NewListings.CurrentPeriodCovered.Should().BeFalse();
        history.Note.Should().Contain("No daily snapshots");
    }

    [Fact]
    public async Task Weekly_trend_uses_retained_snapshots_and_labels_incomplete_weeks()
    {
        await using var dbContext = CreateDbContext();
        SeedCompleteWindow(dbContext);
        await dbContext.SaveChangesAsync();

        var history = await new HistoryService(dbContext, new TestClock(Now)).GetHistoryAsync(7, CancellationToken.None);

        history.Coverage.HasHistory.Should().BeTrue();
        history.Coverage.FirstDate.Should().Be("2026-09-28");
        history.Coverage.LastDate.Should().Be("2026-10-15");
        history.Coverage.SnapshotDays.Should().Be(18);
        history.Coverage.ExpectedDays.Should().Be(18);
        history.Coverage.MissingDates.Should().BeEmpty();

        history.WeeklyNewListings.Should().HaveCount(3);
        var firstWeek = history.WeeklyNewListings[0];
        firstWeek.WeekStartLocal.Should().Be("2026-09-28");
        firstWeek.Count.Should().Be(2, "x and a were first seen in that week");
        firstWeek.IsPartial.Should().BeFalse("coverage started on a Monday and every day is retained");

        var secondWeek = history.WeeklyNewListings[1];
        secondWeek.WeekStartLocal.Should().Be("2026-10-05");
        secondWeek.Count.Should().Be(2);
        secondWeek.IsPartial.Should().BeFalse();

        var currentWeek = history.WeeklyNewListings[2];
        currentWeek.WeekStartLocal.Should().Be("2026-10-12");
        currentWeek.Count.Should().Be(1);
        currentWeek.IsPartial.Should().BeTrue("the current week is still in progress");
        currentWeek.Note.Should().Contain("in progress");
    }

    [Fact]
    public async Task Active_change_uses_the_snapshot_exactly_seven_days_earlier()
    {
        await using var dbContext = CreateDbContext();
        SeedCompleteWindow(dbContext);
        await dbContext.SaveChangesAsync();

        var history = await new HistoryService(dbContext, new TestClock(Now)).GetHistoryAsync(7, CancellationToken.None);

        history.LatestSnapshotDate.Should().Be("2026-10-15");
        history.LatestActiveCount.Should().Be(5);
        history.ComparableFromDate.Should().Be("2026-10-08");
        history.ActiveChange.Previous.Should().Be(3);
        history.ActiveChange.AbsoluteChange.Should().Be(2);
        history.ActiveChange.PercentChange.Should().Be(66.7m);
        history.ActiveChange.ComparisonAvailable.Should().BeTrue();
    }

    [Fact]
    public async Task Source_coverage_changes_are_annotated_so_added_sources_are_not_growth()
    {
        await using var dbContext = CreateDbContext();
        SeedCompleteWindow(dbContext);
        await dbContext.SaveChangesAsync();

        var history = await new HistoryService(dbContext, new TestClock(Now)).GetHistoryAsync(7, CancellationToken.None);

        history.CoverageChanges.Should().HaveCount(3);
        history.CoverageChanges[0].SnapshotDate.Should().Be("2026-10-08");
        history.CoverageChanges[0].SourcesAdded.Should().Contain("Beta");
        history.CoverageChanges[1].SourcesAdded.Should().Contain("Gamma");
        history.CoverageChanges[2].SourcesAdded.Should().Contain("Delta");

        var point = history.ActiveTrend.Single(item => item.SnapshotDate == "2026-10-09");
        point.CoverageChanged.Should().BeTrue();
        point.SourcesAdded.Should().Contain("Gamma");
    }

    [Fact]
    public async Task New_listings_compare_equal_duration_periods_when_snapshots_cover_both()
    {
        await using var dbContext = CreateDbContext();
        SeedCompleteWindow(dbContext);
        await dbContext.SaveChangesAsync();

        var history = await new HistoryService(dbContext, new TestClock(Now)).GetHistoryAsync(7, CancellationToken.None);
        var newListings = history.NewListings;

        newListings.Days.Should().Be(7);
        newListings.Interval.StartLocalDate.Should().Be("2026-10-08");
        newListings.Interval.EndLocalDateExclusive.Should().Be("2026-10-15");
        newListings.CurrentPeriodCovered.Should().BeTrue();
        newListings.Change.Current.Should().Be(3, "b, c and d were first seen inside the period");
        newListings.Change.Previous.Should().Be(1, "a was first seen in the previous equal period");
        newListings.Change.AbsoluteChange.Should().Be(2);
        newListings.Change.ComparisonAvailable.Should().BeTrue();
        newListings.Note.Should().BeNull();
    }

    [Fact]
    public async Task New_listings_declare_the_comparison_unavailable_when_history_starts_late()
    {
        await using var dbContext = CreateDbContext();
        // Only the last three days were retained, so the previous period is not covered.
        AddListing(dbContext, new DateOnly(2026, 10, 13), "late", "Alpha", new DateTime(2026, 10, 13, 0, 0, 0, DateTimeKind.Utc));
        await dbContext.SaveChangesAsync();

        var history = await new HistoryService(dbContext, new TestClock(Now)).GetHistoryAsync(7, CancellationToken.None);

        history.Coverage.FirstDate.Should().Be("2026-10-13");
        history.NewListings.CurrentPeriodCovered.Should().BeFalse();
        history.NewListings.Change.ComparisonAvailable.Should().BeFalse();
        history.NewListings.Note.Should().Contain("only partially retained");
    }

    [Fact]
    public async Task Weekly_grouping_uses_local_dates_across_the_dst_spring_forward()
    {
        await using var dbContext = CreateDbContext();
        // Spring-forward is 2026-09-27 02:00 NZST → 03:00 NZDT.
        var now = new DateTime(2026, 9, 29, 0, 30, 0, DateTimeKind.Utc);
        AddListing(dbContext, new DateOnly(2026, 9, 21), "before", "Alpha", new DateTime(2026, 9, 26, 13, 0, 0, DateTimeKind.Utc), new DateOnly(2026, 9, 29));
        AddListing(dbContext, new DateOnly(2026, 9, 26), "after", "Alpha", new DateTime(2026, 9, 26, 23, 0, 0, DateTimeKind.Utc), new DateOnly(2026, 9, 29));
        await dbContext.SaveChangesAsync();

        var history = await new HistoryService(dbContext, new TestClock(now)).GetHistoryAsync(7, CancellationToken.None);

        var transitionWeek = history.WeeklyNewListings.Single(week => week.WeekStartLocal == "2026-09-21");
        transitionWeek.Count.Should().Be(2, "both listings fall in the same NZ week either side of the DST transition");
    }

    private static void SeedCompleteWindow(ApplicationDbContext dbContext)
    {
        Seed(dbContext, new DateOnly(2026, 9, 28), new DateOnly(2026, 10, 15));
        foreach (var listing in Listings)
        {
            AddListing(dbContext, listing.ActiveFrom, listing.Hash, listing.Source, listing.FirstSeen);
        }
    }

    private static void Seed(ApplicationDbContext dbContext, DateOnly firstDay, DateOnly lastDay)
    {
        for (var day = firstDay; day <= lastDay; day = day.AddDays(1))
        {
            dbContext.ListingSnapshotRuns.Add(new ListingSnapshotRun
            {
                Id = Guid.NewGuid(),
                SnapshotDate = day,
                CapturedAtUtc = day.ToDateTime(TimeOnly.FromTimeSpan(TimeSpan.Zero), DateTimeKind.Utc),
                ListingCount = 0,
                ActiveCount = 0,
                SourceCount = 0,
                IdentifiableEmployerCount = 0,
                SourceCoverageJson = "{}"
            });
        }
    }

    private static void AddListing(
        ApplicationDbContext dbContext,
        DateOnly fromDay,
        string hash,
        string source,
        DateTime firstSeen,
        DateOnly? lastDay = null)
    {
        var end = lastDay ?? new DateOnly(2026, 10, 15);
        for (var day = fromDay; day <= end; day = day.AddDays(1))
        {
            dbContext.ListingSnapshots.Add(new ListingSnapshot
            {
                Id = Guid.NewGuid(),
                SnapshotDate = day,
                JobId = Guid.NewGuid(),
                Source = source,
                SourceCategory = "company",
                ContentHash = hash,
                Title = "Software Engineer",
                HiringEmployer = source,
                WorkMode = "Hybrid",
                Seniority = "Senior",
                EmploymentType = "Full-time",
                RoleFamily = "Software Engineering",
                SalaryCurrency = "NZD",
                SalaryPeriod = "Year",
                FirstSeenAt = firstSeen,
                LastSeenAt = firstSeen.AddDays(1),
                IsActive = true,
                CapturedAtUtc = firstSeen
            });
        }
    }

    private static ApplicationDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        return new ApplicationDbContext(options);
    }
}
