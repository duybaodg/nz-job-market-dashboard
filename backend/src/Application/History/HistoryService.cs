using Application.Analytics;
using Application.Crawling;
using Application.Common;
using Application.Metrics;
using Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Application.History;

public interface IHistoryService
{
    Task<HistoryDto> GetHistoryAsync(int days, CancellationToken cancellationToken);

    Task<HistoryDto> GetHistoryAsync(int days, HistoryFilter filter, CancellationToken cancellationToken);
}

/// <summary>
/// Historical analysis built only from retained <see cref="ListingSnapshot"/>
/// rows. Missing history stays missing and incomplete periods are labelled; the
/// service never reconstructs a past value from today's mutable job rows.
/// </summary>
public sealed class HistoryService(
    IApplicationDbContext dbContext,
    TimeProvider? timeProvider = null,
    CrawlerLifecycleOptions? lifecycleOptions = null) : IHistoryService
{
    public static readonly int[] SupportedPeriods = [7, 15, 30];

    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly CrawlerLifecycleOptions lifecycle = lifecycleOptions ?? CrawlerLifecycleOptions.Default;

    public Task<HistoryDto> GetHistoryAsync(int days, CancellationToken cancellationToken) =>
        GetHistoryAsync(days, HistoryFilter.None, cancellationToken);

    public async Task<HistoryDto> GetHistoryAsync(
        int days,
        HistoryFilter filter,
        CancellationToken cancellationToken)
    {
        filter ??= HistoryFilter.None;
        var period = Math.Clamp(days, 1, 365);
        var now = clock.GetUtcNow().UtcDateTime;
        // Round 15 (H-12): bound the snapshot scan to the requested period plus
        // its equal-length comparison window (and at least the weekly chart's
        // 30-day floor). Never scan the whole snapshots table for a 7-day query.
        var windowDays = Math.Clamp(Math.Max(period * 2, 30), 1, 730);
        var windowStart = NzReportingCalendar.LocalDate(now).AddDays(-windowDays);
        var snapshots = await dbContext.ListingSnapshots
            .AsNoTracking()
            .Where(snapshot => snapshot.SnapshotDate >= windowStart)
            .ToListAsync(cancellationToken);

        var coverage = BuildCoverage(snapshots, now, lifecycle.RetentionDays);
        var coverageDates = snapshots.Select(snapshot => snapshot.SnapshotDate).ToHashSet();
        var orderedDates = coverageDates.OrderBy(date => date).ToList();
        var filterSupported = filter.HistorySupported;
        var predicate = filterSupported ? BuildPredicate(filter) : null;
        // A skill filter cannot be reconstructed from snapshots. Charts then stay
        // global and are labelled as such; the period comparison is withheld.
        var scoped = predicate is null ? snapshots : snapshots.Where(predicate).ToList();

        // Source coverage is a property of the whole collection, never of the
        // filtered cohort, so its annotations always use every snapshot.
        BuildActiveTrend(snapshots, orderedDates, out var coverageChanges);
        var weekly = BuildWeekly(scoped, coverage, now);
        var activeTrend = filterSupported ? BuildActiveTrend(scoped, orderedDates, out _) : [];
        var newListings = BuildNewListings(scoped, coverage, coverageDates, period, now, filterSupported);

        var latest = activeTrend.LastOrDefault();
        ChangeDto activeChange;
        string? comparableFromDate = null;

        if (latest is null || !filterSupported)
        {
            activeChange = ChangeMapper.ToDto(ChangeCalculation.Compute(latest?.ActiveCount ?? 0, null));
        }
        else
        {
            var latestDate = DateOnly.Parse(latest.SnapshotDate);
            var comparable = activeTrend.FirstOrDefault(point => point.SnapshotDate == latestDate.AddDays(-7).ToString("yyyy-MM-dd"));
            comparableFromDate = comparable?.SnapshotDate;
            activeChange = ChangeMapper.ToDto(
                ChangeCalculation.Compute(latest.ActiveCount, comparable?.ActiveCount));
        }

        var filterDto = ToFilterDto(filter);
        return new HistoryDto(
            now,
            coverage,
            latest?.ActiveCount ?? 0,
            latest?.SnapshotDate,
            activeChange,
            comparableFromDate,
            newListings,
            weekly,
            activeTrend,
            coverageChanges,
            filterDto,
            BuildScopeNote(filter, filterSupported),
            coverage.HasHistory
                ? "History is reconstructed from retained daily listing snapshots only."
                : "No daily snapshots have been retained yet, so historical trends are unavailable.");
    }

    private static Func<ListingSnapshot, bool>? BuildPredicate(HistoryFilter filter)
    {
        var region = Clean(filter.Region);
        var roleFamily = Clean(filter.RoleFamily);
        var seniority = Clean(filter.Seniority);

        if (region is null && roleFamily is null && seniority is null)
        {
            return null;
        }

        return snapshot =>
            (region is null
                || string.Equals(snapshot.Region, region, StringComparison.OrdinalIgnoreCase)
                || string.Equals(snapshot.Location, region, StringComparison.OrdinalIgnoreCase))
            && (roleFamily is null
                || string.Equals(snapshot.RoleFamily, roleFamily, StringComparison.OrdinalIgnoreCase))
            && (seniority is null
                || string.Equals(snapshot.Seniority, seniority, StringComparison.OrdinalIgnoreCase));
    }

    private static HistoryFilterDto ToFilterDto(HistoryFilter filter)
    {
        var region = Clean(filter.Region);
        var roleFamily = Clean(filter.RoleFamily);
        var seniority = Clean(filter.Seniority);
        var skill = Clean(filter.Skill);
        var hasFilters = region is not null || roleFamily is not null || seniority is not null || skill is not null;
        var supported = skill is null;

        return new HistoryFilterDto(
            region,
            roleFamily,
            seniority,
            skill,
            hasFilters,
            supported,
            supported
                ? hasFilters
                    ? "Historical charts and comparisons use the same region, role-family and seniority selection as the current cohort."
                    : "No cohort filter is applied; history shows all tracked listings."
                : "Historical snapshots do not retain skill tags, so a skill-filtered historical comparison is unavailable. Charts show all tracked listings.");
    }

    private static string BuildScopeNote(HistoryFilter filter, bool filterSupported) =>
        !filter.HasFilters
            ? "Historical panels show all tracked listings because no cohort filter is selected."
            : filterSupported
                ? "Historical panels follow the selected region, role family and seniority cohort."
                : "Historical panels show all tracked listings because snapshots do not retain the selected skill; no substitute percentage is derived.";

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static SnapshotCoverageDto BuildCoverage(
        IReadOnlyList<ListingSnapshot> snapshots,
        DateTime now,
        int retentionDays)
    {
        if (snapshots.Count == 0)
        {
            return new SnapshotCoverageDto(
                null,
                null,
                0,
                0,
                [],
                false,
                "No daily snapshots retained yet. Collection begins with the next worker poll; no trend is estimated in the meantime.",
                retentionDays);
        }

        var dates = snapshots.Select(snapshot => snapshot.SnapshotDate).Distinct().OrderBy(date => date).ToList();
        var first = dates[0];
        var last = dates[^1];
        var expected = last.DayNumber - first.DayNumber + 1;
        var present = dates.ToHashSet();
        var missing = new List<string>();

        for (var date = first; date <= last; date = date.AddDays(1))
        {
            if (!present.Contains(date))
            {
                missing.Add(date.ToString("yyyy-MM-dd"));
            }
        }

        var today = NzReportingCalendar.LocalDate(now);
        var staleDays = today.DayNumber - last.DayNumber;
        var notes = new List<string>();
        if (missing.Count > 0)
        {
            notes.Add($"{missing.Count} snapshot day(s) missing inside the coverage window");
        }

        if (staleDays > 1)
        {
            notes.Add($"latest snapshot is {staleDays} days old");
        }

        return new SnapshotCoverageDto(
            first.ToString("yyyy-MM-dd"),
            last.ToString("yyyy-MM-dd"),
            dates.Count,
            expected,
            missing,
            true,
            notes.Count == 0
                ? "Daily snapshots are complete for the retained window."
                : char.ToUpperInvariant(notes[0][0]) + string.Join("; ", notes).Substring(1) + ".",
            retentionDays);
    }

    private static IReadOnlyList<WeeklyNewListingDto> BuildWeekly(
        IReadOnlyList<ListingSnapshot> snapshots,
        SnapshotCoverageDto coverage,
        DateTime now)
    {
        if (!coverage.HasHistory || coverage.FirstDate is null)
        {
            return [];
        }

        var first = DateOnly.Parse(coverage.FirstDate);
        var today = NzReportingCalendar.LocalDate(now);
        var coverageStartWeek = StartOfWeek(first);
        var currentWeek = StartOfWeek(today);

        var byWeek = new Dictionary<DateOnly, HashSet<string>>();
        foreach (var snapshot in snapshots)
        {
            var week = StartOfWeek(NzReportingCalendar.LocalDate(snapshot.FirstSeenAt));
            if (week < coverageStartWeek)
            {
                // The first snapshot cannot see listings that appeared and
                // disappeared before collection began, so earlier weeks are omitted.
                continue;
            }

            if (!byWeek.TryGetValue(week, out var hashes))
            {
                hashes = new HashSet<string>(StringComparer.Ordinal);
                byWeek[week] = hashes;
            }

            hashes.Add(snapshot.ContentHash);
        }

        var missingDates = coverage.MissingDates.ToHashSet(StringComparer.Ordinal);
        var results = new List<WeeklyNewListingDto>();

        for (var week = coverageStartWeek; week <= currentWeek; week = week.AddDays(7))
        {
            var weekEnd = week.AddDays(7);
            var count = byWeek.TryGetValue(week, out var hashes) ? hashes.Count : 0;
            var collectionBeganMidWeek = first > coverageStartWeek;
            var isFirstWeek = week == coverageStartWeek;
            var isCurrentWeek = weekEnd > today;
            var missingInWeek = missingDates.Count(date =>
                DateOnly.TryParse(date, out var parsed) && parsed >= week && parsed < weekEnd);
            var isPartial = (isFirstWeek && collectionBeganMidWeek) || isCurrentWeek || missingInWeek > 0;

            string? note = null;
            if (isCurrentWeek && isFirstWeek && collectionBeganMidWeek)
            {
                note = "Collection and week both in progress";
            }
            else if (isCurrentWeek)
            {
                note = "Week in progress";
            }
            else if (isFirstWeek && collectionBeganMidWeek)
            {
                note = "Collection began mid-week; earlier listings may be missing";
            }
            else if (missingInWeek > 0)
            {
                note = $"{missingInWeek} snapshot day(s) missing";
            }

            results.Add(new WeeklyNewListingDto(
                week.ToString("yyyy-MM-dd"),
                weekEnd.ToString("yyyy-MM-dd"),
                count,
                isPartial,
                coverage.FirstDate,
                note));
        }

        return results;
    }

    private static IReadOnlyList<ActiveTrendPointDto> BuildActiveTrend(
        IReadOnlyList<ListingSnapshot> snapshots,
        IReadOnlyList<DateOnly> orderedDates,
        out IReadOnlyList<SourceCoverageChangeDto> coverageChanges)
    {
        var changes = new List<SourceCoverageChangeDto>();
        var points = new List<ActiveTrendPointDto>();
        var byDate = snapshots
            .GroupBy(snapshot => snapshot.SnapshotDate)
            .ToDictionary(group => group.Key, group => group.ToList());
        HashSet<string>? previousSources = null;

        // Iterate every retained date, not only dates with a matching row, so a
        // filtered cohort's zero days are real zeros rather than missing points.
        foreach (var dateOnly in orderedDates)
        {
            var rows = byDate.GetValueOrDefault(dateOnly) ?? [];
            var active = rows.Where(snapshot => snapshot.IsActive).ToList();
            var activeCount = active.Select(snapshot => snapshot.ContentHash).Distinct(StringComparer.Ordinal).Count();
            var sources = active.Select(snapshot => snapshot.Source).ToHashSet(StringComparer.OrdinalIgnoreCase);

            var added = previousSources is null
                ? []
                : sources.Except(previousSources, StringComparer.OrdinalIgnoreCase).OrderBy(source => source).ToList();
            var removed = previousSources is null
                ? []
                : previousSources.Except(sources, StringComparer.OrdinalIgnoreCase).OrderBy(source => source).ToList();
            var changed = added.Count > 0 || removed.Count > 0;
            var date = dateOnly.ToString("yyyy-MM-dd");

            points.Add(new ActiveTrendPointDto(date, activeCount, sources.Count, changed, added, removed));
            if (changed)
            {
                changes.Add(new SourceCoverageChangeDto(date, added, removed));
            }

            previousSources = sources;
        }

        coverageChanges = changes;
        return points;
    }

    private static NewListingPeriodDto BuildNewListings(
        IReadOnlyList<ListingSnapshot> snapshots,
        SnapshotCoverageDto coverage,
        IReadOnlySet<DateOnly> coverageDates,
        int days,
        DateTime now,
        bool historyFilterSupported)
    {
        const string dateBasis = "Crawler first-seen timestamp (discovery), by complete Pacific/Auckland days";
        var interval = NzReportingCalendar.LastCompleteDays(days, now);
        var previous = NzReportingCalendar.PreviousPeriod(interval);

        // A timestamp range is not completeness: every local day in both the
        // current and previous windows must actually have a retained snapshot.
        var currentCovered = historyFilterSupported && CoversCompleteWindow(coverageDates, interval);
        var previousCovered = historyFilterSupported && CoversCompleteWindow(coverageDates, previous);

        var current = historyFilterSupported ? CountDistinctFirstSeen(snapshots, interval) : 0;
        // A comparison is only meaningful when the current period is itself
        // complete; an incomplete numerator cannot be compared with a complete
        // previous period.
        var previousCount = currentCovered && previousCovered
            ? CountDistinctFirstSeen(snapshots, previous)
            : (int?)null;
        var change = ChangeMapper.ToDto(ChangeCalculation.Compute(current, previousCount));

        string? note = null;
        if (!coverage.HasHistory)
        {
            note = "No retained snapshots yet; the current period is not covered.";
        }
        else if (!historyFilterSupported)
        {
            note = "Comparison unavailable: historical snapshots do not retain skill tags, so this cohort cannot be reconstructed. No substitute percentage is shown.";
        }
        else if (!currentCovered)
        {
            note = $"Current period is only partially retained: not every day in {interval.StartLocalDate:yyyy-MM-dd} to {interval.EndLocalDateExclusive:yyyy-MM-dd} (exclusive) has a snapshot.";
        }
        else if (!previousCovered)
        {
            note = $"Comparison unavailable: not every day in the previous period {previous.StartLocalDate:yyyy-MM-dd} to {previous.EndLocalDateExclusive:yyyy-MM-dd} (exclusive) is retained.";
        }

        return new NewListingPeriodDto(
            days,
            MetricIntervalMapper.ToDto(interval, isPartial: !currentCovered),
            change,
            currentCovered,
            dateBasis,
            note);
    }

    private static bool CoversCompleteWindow(IReadOnlySet<DateOnly> coverageDates, ReportingInterval interval)
    {
        for (var date = interval.StartLocalDate; date < interval.EndLocalDateExclusive; date = date.AddDays(1))
        {
            if (!coverageDates.Contains(date))
            {
                return false;
            }
        }

        return true;
    }

    private static int CountDistinctFirstSeen(IReadOnlyList<ListingSnapshot> snapshots, ReportingInterval interval) =>
        snapshots
            .Where(snapshot => interval.Contains(snapshot.FirstSeenAt))
            .Select(snapshot => snapshot.ContentHash)
            .Distinct(StringComparer.Ordinal)
            .Count();

    private static DateOnly StartOfWeek(DateOnly date) =>
        date.AddDays(-(((int)date.DayOfWeek + 6) % 7));
}
