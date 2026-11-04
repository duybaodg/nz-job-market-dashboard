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
        BuildActiveTrend(snapshots, orderedDates, out var rawCoverageChanges);
        var runObservations = await LoadRunObservationsAsync(orderedDates, cancellationToken);
        var sourceRegistry = await LoadSourceRegistryAsync(cancellationToken);
        var coverageChanges = ClassifyCoverageChanges(rawCoverageChanges, snapshots, runObservations, sourceRegistry);
        var constantSourceComparison = BuildConstantSourceComparison(snapshots, orderedDates, runObservations);
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
            constantSourceComparison,
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

    private async Task<IReadOnlyList<RunObservation>> LoadRunObservationsAsync(
        IReadOnlyList<DateOnly> snapshotDates,
        CancellationToken cancellationToken)
    {
        if (snapshotDates.Count == 0)
        {
            return [];
        }

        // Include a month before the first snapshot so a returning source can be
        // recognised as a recovery rather than a brand-new source.
        var fromUtc = NzReportingCalendar.StartOfLocalDayUtc(snapshotDates[0].AddDays(-30));
        var runs = await dbContext.CrawlRuns
            .AsNoTracking()
            .Where(run => (run.FinishedAt ?? run.StartedAt) >= fromUtc)
            .Select(run => new
            {
                run.Source,
                run.Status,
                Timestamp = run.FinishedAt ?? run.StartedAt
            })
            .ToListAsync(cancellationToken);

        return runs
            .Select(run => new RunObservation(
                run.Source,
                NzReportingCalendar.LocalDate(run.Timestamp),
                run.Status))
            .ToList();
    }

    private async Task<IReadOnlyList<SourceRegistryRow>> LoadSourceRegistryAsync(CancellationToken cancellationToken)
    {
        var sources = await dbContext.JobSources
            .AsNoTracking()
            .Select(source => new { source.Name, source.Enabled, source.RetiredAt, source.CreatedAt })
            .ToListAsync(cancellationToken);

        return sources
            .Select(source => new SourceRegistryRow(source.Name, source.Enabled, source.RetiredAt, source.CreatedAt))
            .ToList();
    }

    private static IReadOnlyList<SourceCoverageChangeDto> ClassifyCoverageChanges(
        IReadOnlyList<SourceCoverageChangeDto> changes,
        IReadOnlyList<ListingSnapshot> snapshots,
        IReadOnlyList<RunObservation> runs,
        IReadOnlyList<SourceRegistryRow> sourceRegistry)
    {
        var registry = sourceRegistry
            .GroupBy(source => source.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var runsBySourceAndDate = runs
            .GroupBy(run => (run.Source, run.LocalDate))
            .ToDictionary(
                group => group.Key,
                group => group.Select(run => run.Status).ToList(),
                new SourceDateComparer());

        return changes
            .Select(change =>
            {
                var date = DateOnly.Parse(change.SnapshotDate);
                var events = new List<SourceCoverageEventDto>();

                foreach (var source in change.SourcesAdded)
                {
                    // A source that was observed before this date is recovering
                    // from an outage; one never observed before is genuinely new
                    // to the retained record.
                    var hadPriorObservation =
                        snapshots.Any(snapshot =>
                            snapshot.SnapshotDate < date
                            && string.Equals(snapshot.Source, source, StringComparison.OrdinalIgnoreCase))
                        || runs.Any(run =>
                            run.LocalDate < date
                            && string.Equals(run.Source, source, StringComparison.OrdinalIgnoreCase));

                    if (hadPriorObservation)
                    {
                        events.Add(new SourceCoverageEventDto(
                            source,
                            "added",
                            "Recovery",
                            "This source had earlier retained observations and was absent on the previous snapshot. The reappearance is a collection recovery, not evidence of newly posted roles."));
                        continue;
                    }

                    if (registry.TryGetValue(source, out var registered)
                        && IsRegistryCreationOnOrNear(registered.CreatedAt, date))
                    {
                        events.Add(new SourceCoverageEventDto(
                            source,
                            "added",
                            "Configuration added",
                            $"The current source registry row was created around {NzReportingCalendar.LocalDate(registered.CreatedAt):yyyy-MM-dd}. Historical enabled state is not retained, so this is inferred from the current registry."));
                        continue;
                    }

                    events.Add(new SourceCoverageEventDto(
                        source,
                        "added",
                        "First observation",
                        "No earlier retained snapshot or crawl run exists for this source. The first appearance may reflect a new configuration or newly reachable listings."));
                }

                foreach (var source in change.SourcesRemoved)
                {
                    var statuses = runsBySourceAndDate.GetValueOrDefault((source, date)) ?? [];
                    registry.TryGetValue(source, out var registered);

                    if (registered is { RetiredAt: not null } || registered is { Enabled: false })
                    {
                        events.Add(new SourceCoverageEventDto(
                            source,
                            "removed",
                            "Registry change",
                            "The source is currently disabled or retired in the registry. Its absence reflects configuration state, not zero demand or confirmed vacancy closures."));
                        continue;
                    }

                    if (statuses.Any(status => string.Equals(status, "Failed", StringComparison.OrdinalIgnoreCase)))
                    {
                        events.Add(new SourceCoverageEventDto(
                            source,
                            "removed",
                            "Refresh failure",
                            "A failed crawl run was recorded on this date. The missing rows are treated as an observation gap, never as a zero-source count or a closure."));
                        continue;
                    }

                    if (statuses.Any(status => string.Equals(status, "Completed", StringComparison.OrdinalIgnoreCase)))
                    {
                        events.Add(new SourceCoverageEventDto(
                            source,
                            "removed",
                            "No observations after a successful refresh",
                            "The source completed a refresh but contributed no snapshot rows on this date. That is zero yield, not proof that every vacancy closed."));
                        continue;
                    }

                    events.Add(new SourceCoverageEventDto(
                        source,
                        "removed",
                        "Not observed (no successful run)",
                        "No completed crawl run is recorded for this date, so the absence is an observation gap rather than measured zero demand."));
                }

                return change with { Events = events };
            })
            .ToList();
    }

    private static bool IsRegistryCreationOnOrNear(DateTime createdAtUtc, DateOnly snapshotDate)
    {
        var created = NzReportingCalendar.LocalDate(createdAtUtc);
        return created >= snapshotDate.AddDays(-1) && created <= snapshotDate.AddDays(1);
    }

    private static ConstantSourceComparisonDto BuildConstantSourceComparison(
        IReadOnlyList<ListingSnapshot> snapshots,
        IReadOnlyList<DateOnly> orderedDates,
        IReadOnlyList<RunObservation> runs)
    {
        const string eligibilityRule =
            "Sources with a valid observation (a snapshot row or a completed crawl run) on every Pacific/Auckland day in the comparison window, counted on both sides from that same source set.";

        if (orderedDates.Count == 0)
        {
            return UnavailableConstantComparison(
                "No retained daily snapshots are available, so a constant-source comparison cannot be computed.",
                eligibilityRule);
        }

        var toDate = orderedDates[^1];
        var fromDate = toDate.AddDays(-7);
        if (orderedDates.Count < 8 || !orderedDates.Contains(fromDate))
        {
            return UnavailableConstantComparison(
                $"At least eight consecutive snapshot days are required for a seven-day comparison; the retained window is {orderedDates[0]:yyyy-MM-dd} to {toDate:yyyy-MM-dd}.",
                eligibilityRule);
        }

        var requiredDates = Enumerable.Range(0, 8).Select(offset => fromDate.AddDays(offset)).ToList();
        var missingDates = requiredDates.Where(date => !orderedDates.Contains(date)).ToList();
        if (missingDates.Count > 0)
        {
            return UnavailableConstantComparison(
                $"Snapshot day(s) missing inside the comparison window: {string.Join(", ", missingDates.Select(date => date.ToString("yyyy-MM-dd")))}.",
                eligibilityRule);
        }

        var completedRuns = runs
            .Where(run => string.Equals(run.Status, "Completed", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var observedSources = snapshots
            .Select(snapshot => snapshot.Source)
            .Concat(completedRuns.Select(run => run.Source))
            .Where(source => !string.IsNullOrWhiteSpace(source))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(source => source, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var observations = new HashSet<(string Source, DateOnly Date)>(new SourceDateComparer());
        foreach (var snapshot in snapshots)
        {
            observations.Add((snapshot.Source, snapshot.SnapshotDate));
        }

        foreach (var run in completedRuns)
        {
            observations.Add((run.Source, run.LocalDate));
        }

        var eligible = observedSources
            .Where(source => requiredDates.All(date => observations.Contains((source, date))))
            .OrderBy(source => source, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (eligible.Count == 0)
        {
            return UnavailableConstantComparison(
                $"No source has a valid observation on every day from {fromDate:yyyy-MM-dd} to {toDate:yyyy-MM-dd}; a like-for-like comparison would be fabricated.",
                eligibilityRule);
        }

        var eligibleSet = eligible.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rawFrom = CountActive(snapshots, fromDate, null);
        var rawTo = CountActive(snapshots, toDate, null);
        var from = CountActive(snapshots, fromDate, eligibleSet);
        var to = CountActive(snapshots, toDate, eligibleSet);
        var change = ChangeMapper.ToDto(ChangeCalculation.Compute(to, from));
        var rawChange = ChangeMapper.ToDto(ChangeCalculation.Compute(rawTo, rawFrom));
        var residual = rawChange.AbsoluteChange is { } rawAbsolute && change.AbsoluteChange is { } constantAbsolute
            ? rawAbsolute - constantAbsolute
            : (int?)null;

        return new ConstantSourceComparisonDto(
            true,
            null,
            fromDate.ToString("yyyy-MM-dd"),
            toDate.ToString("yyyy-MM-dd"),
            observedSources.Count,
            eligible.Count,
            from,
            to,
            change,
            rawFrom,
            rawTo,
            rawChange,
            residual,
            eligibilityRule,
            eligible);
    }

    private static int CountActive(
        IReadOnlyList<ListingSnapshot> snapshots,
        DateOnly date,
        IReadOnlySet<string>? sources) =>
        snapshots
            .Where(snapshot => snapshot.SnapshotDate == date
                && snapshot.IsActive
                && (sources is null || sources.Contains(snapshot.Source)))
            .Select(snapshot => snapshot.ContentHash)
            .Distinct(StringComparer.Ordinal)
            .Count();

    private static ConstantSourceComparisonDto UnavailableConstantComparison(string reason, string rule) =>
        new(
            false,
            reason,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            rule,
            []);

    private sealed record RunObservation(string Source, DateOnly LocalDate, string Status);

    private sealed record SourceRegistryRow(string Name, bool Enabled, DateTime? RetiredAt, DateTime CreatedAt);

    private sealed class SourceDateComparer : IEqualityComparer<(string Source, DateOnly Date)>
    {
        public bool Equals((string Source, DateOnly Date) x, (string Source, DateOnly Date) y) =>
            string.Equals(x.Source, y.Source, StringComparison.OrdinalIgnoreCase) && x.Date == y.Date;

        public int GetHashCode((string Source, DateOnly Date) obj) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Source), obj.Date);
    }



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
                changes.Add(new SourceCoverageChangeDto(date, added, removed, []));
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
