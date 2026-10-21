using Application.Common;
using Application.Crawling;
using Application.Deduplication;
using Application.Employers;
using Application.Metrics;
using Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Application.Analytics;

public interface IAnalyticsService
{
    Task<OverviewDto> GetOverviewAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<SkillDemandDto>> GetTopSkillsAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<JobsByRegionDto>> GetJobsByRegionAsync(CancellationToken cancellationToken);
    Task<FreshnessDto> GetFreshnessAsync(CancellationToken cancellationToken);
}

/// <summary>The freshness windows surfaced by the dashboard, in complete NZ days.</summary>
public static class FreshnessWindows
{
    public static readonly int[] All = [2, 7, 15];
}

public sealed class AnalyticsService(
    IApplicationDbContext dbContext,
    TimeProvider? timeProvider = null,
    IListingDeduplicationService? deduplicationService = null,
    CrawlerLifecycleOptions? lifecycleOptions = null) : IAnalyticsService
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly CrawlerLifecycleOptions lifecycle = lifecycleOptions ?? CrawlerLifecycleOptions.Default;

    /// <summary>
    /// Explicit scope label for discovery/posted counts. The cohort includes
    /// retained listings that have since expired, so churn cannot look like
    /// growth by dropping old rows from the previous period.
    /// </summary>
    private string DiscoveryBasis =>
        $"Listings first seen or posted inside the window, including rows that have since expired while still inside the "
        + $"{lifecycle.RetentionDays}-day retention window. Not a census of every vacancy ever advertised.";

    public async Task<OverviewDto> GetOverviewAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var active = await LoadActiveListingsAsync(cancellationToken);
        var categories = await LoadSourceCategoriesAsync(cancellationToken);
        // Round 15 (H-10/M-4): discovery/posted counts use the retained cohort
        // (active + recently expired), not just today's active rows. The
        // posted-date coverage fields still describe the active cohort so
        // known + missing always equals ActiveListings.
        var discoveryObservations = await LoadDiscoveryObservationsAsync(now, cancellationToken);
        var activeObservations = active
            .Select(job => new ListingDateObservation(job.PostedDate, job.FirstSeenAt))
            .ToList();

        var interval = NzReportingCalendar.LastCompleteDays(7, now);
        var previousInterval = NzReportingCalendar.PreviousPeriod(interval);

        var newlyDiscovered = ListingMetrics.Count(discoveryObservations, interval, DateBasis.Discovered);
        var newlyPosted = ListingMetrics.Count(discoveryObservations, interval, DateBasis.Posted);
        var activePostedCoverage = ListingMetrics.Count(activeObservations, interval, DateBasis.Posted);

        var discoveredChange = BuildChange(
            newlyDiscovered.Count,
            HistoryAvailable(previousInterval, now)
                ? ListingMetrics.Count(discoveryObservations, previousInterval, DateBasis.Discovered).Count
                : null);
        var postedChange = BuildChange(
            newlyPosted.Count,
            HistoryAvailable(previousInterval, now)
                ? ListingMetrics.Count(discoveryObservations, previousInterval, DateBasis.Posted).Count
                : null);

        var salary = SalaryMetrics.Compute(active.Select(job => new SalaryObservation(
            job.SalaryMin,
            job.SalaryMax,
            job.SalaryText,
            string.IsNullOrWhiteSpace(job.SalaryCurrency) ? "NZD" : job.SalaryCurrency!,
            ParsePayPeriod(job.SalaryPeriod),
            job.EmploymentType,
            job.SalaryQuestionable)));
        var employerRows = active
            .Select(job => new EmployerRow(
                EmployerClassifier.Classify(job.Source, CategoryFor(categories, job.Source), job.Company),
                job.EmployerCanonicalId))
            .ToList();
        var employerTypes = await LoadEmployerTypesAsync(cancellationToken);
        var employerSummary = BuildEmployerSummary(employerRows, active.Count, employerTypes);

        var dataStatus = await BuildDataStatusAsync(now, cancellationToken);
        var deduplication = await LoadDeduplicationAsync(active.Count, cancellationToken);

        return new OverviewDto(
            now,
            active.Count,
            activePostedCoverage.WithPostedDate,
            activePostedCoverage.MissingPostedDate,
            discoveredChange,
            postedChange,
            ToSalaryDto(salary),
            employerSummary,
            dataStatus,
            active
                .Where(job => !string.IsNullOrWhiteSpace(job.Region))
                .GroupBy(job => job.Region!)
                .OrderByDescending(group => group.Count())
                .ThenBy(group => group.Key)
                .Select(group => group.Key)
                .FirstOrDefault(),
            await dbContext.JobSkills
                .Where(skill => skill.Job != null && skill.Job.IsActive)
                .GroupBy(skill => skill.SkillName)
                .OrderByDescending(group => group.Count())
                .ThenBy(group => group.Key)
                .Select(group => group.Key)
                .FirstOrDefaultAsync(cancellationToken),
            ToIntervalDto(interval, isPartial: false),
            deduplication.UniqueVacancies,
            deduplication,
            DiscoveryBasis);
    }

    public async Task<IReadOnlyList<SkillDemandDto>> GetTopSkillsAsync(CancellationToken cancellationToken)
    {
        return await dbContext.JobSkills
            .AsNoTracking()
            .Where(skill => skill.Job != null && skill.Job.IsActive)
            .GroupBy(skill => skill.SkillName)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key)
            .Take(TopSkillsLimit)
            .Select(group => new SkillDemandDto(group.Key, group.Select(skill => skill.JobId).Distinct().Count()))
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// The Round 7 taxonomy produces a much richer mix than the original
    /// programming-language list, so the dashboard surfaces the top 15.
    /// </summary>
    public const int TopSkillsLimit = 15;

    public async Task<IReadOnlyList<JobsByRegionDto>> GetJobsByRegionAsync(CancellationToken cancellationToken)
    {
        return await dbContext.Jobs
            .AsNoTracking()
            .Where(job => job.IsActive && job.Region != null)
            .GroupBy(job => job.Region!)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key)
            .Select(group => new JobsByRegionDto(group.Key, group.Count()))
            .ToListAsync(cancellationToken);
    }

    public async Task<FreshnessDto> GetFreshnessAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var active = await LoadActiveListingsAsync(cancellationToken);
        var discoveryObservations = await LoadDiscoveryObservationsAsync(now, cancellationToken);
        var activeObservations = active
            .Select(job => new ListingDateObservation(job.PostedDate, job.FirstSeenAt))
            .ToList();
        var windows = new List<FreshnessWindowDto>();

        foreach (var days in FreshnessWindows.All)
        {
            var interval = NzReportingCalendar.LastCompleteDays(days, now);
            var previousInterval = NzReportingCalendar.PreviousPeriod(interval);
            var comparisonAvailable = HistoryAvailable(previousInterval, now);

            var posted = ListingMetrics.Count(discoveryObservations, interval, DateBasis.Posted);
            var discovered = ListingMetrics.Count(discoveryObservations, interval, DateBasis.Discovered);
            var activePostedCoverage = ListingMetrics.Count(activeObservations, interval, DateBasis.Posted);
            var previousPosted = comparisonAvailable
                ? ListingMetrics.Count(discoveryObservations, previousInterval, DateBasis.Posted).Count
                : 0;
            var previousDiscovered = comparisonAvailable
                ? ListingMetrics.Count(discoveryObservations, previousInterval, DateBasis.Discovered).Count
                : 0;

            windows.Add(new FreshnessWindowDto(
                days,
                ToIntervalDto(interval, isPartial: false),
                posted.Count,
                previousPosted,
                discovered.Count,
                previousDiscovered,
                activePostedCoverage.WithPostedDate,
                activePostedCoverage.MissingPostedDate,
                comparisonAvailable));
        }

        var deduplication = await LoadDeduplicationAsync(active.Count, cancellationToken);
        return new FreshnessDto(
            active.Count,
            now,
            await BuildDataStatusAsync(now, cancellationToken),
            windows,
            deduplication.UniqueVacancies,
            deduplication,
            DiscoveryBasis);
    }

    private static ChangeDto BuildChange(int current, int? previous) =>
        ChangeMapper.ToDto(ChangeCalculation.Compute(current, previous));

    private static SalarySummaryDto ToSalaryDto(SalarySummary salary) =>
        new(
            salary.MedianAnnualNzd,
            salary.EligibleCount,
            salary.CohortCount,
            salary.CoveragePercent,
            salary.QuestionableCount,
            salary.Suppressed,
            salary.SmallSample,
            salary.Deduplicated,
            salary.Exclusions,
            salary.Currency,
            salary.PayPeriod,
            salary.Basis);

    /// <summary>
    /// Current active rows can only reconstruct a prior period while that period
    /// falls inside the crawl retention window. Older periods are declared
    /// unavailable rather than guessed.
    /// </summary>
    private bool HistoryAvailable(ReportingInterval previousInterval, DateTime now) =>
        previousInterval.StartUtc >= now.AddDays(-Math.Max(lifecycle.RetentionDays, 1));

    private async Task<List<ListingDateObservation>> LoadDiscoveryObservationsAsync(
        DateTime now,
        CancellationToken cancellationToken)
    {
        var cutoff = now.AddDays(-Math.Max(lifecycle.RetentionDays, 1));
        return await dbContext.Jobs
            .AsNoTracking()
            .Where(job => job.IsActive || job.FirstSeenAt >= cutoff || job.LastSeenAt >= cutoff)
            .Select(job => new ListingDateObservation(job.PostedDate, job.FirstSeenAt))
            .ToListAsync(cancellationToken);
    }

    private async Task<List<ActiveListing>> LoadActiveListingsAsync(CancellationToken cancellationToken)
    {
        return await dbContext.Jobs
            .AsNoTracking()
            .Where(job => job.IsActive)
            .Select(job => new ActiveListing(
                job.Source,
                job.PostedDate,
                job.FirstSeenAt,
                job.LastSeenAt,
                job.SalaryMin,
                job.SalaryMax,
                job.SalaryText,
                job.SalaryCurrency,
                job.SalaryPeriod,
                job.SalaryQuestionable,
                job.EmploymentType,
                job.Company,
                job.Region,
                job.EmployerCanonicalId))
            .ToListAsync(cancellationToken);
    }

    private async Task<IReadOnlyDictionary<string, string>> LoadSourceCategoriesAsync(CancellationToken cancellationToken)
    {
        var categories = await dbContext.JobSources
            .AsNoTracking()
            .Select(source => new { source.Name, source.Category })
            .ToListAsync(cancellationToken);

        return categories
            .GroupBy(source => source.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => string.IsNullOrWhiteSpace(group.First().Category) ? "unknown" : group.First().Category,
                StringComparer.OrdinalIgnoreCase);
    }

    private static string? CategoryFor(IReadOnlyDictionary<string, string> categories, string source) =>
        categories.TryGetValue(source, out var category) ? category : null;

    private static EmployerSummaryDto BuildEmployerSummary(
        IReadOnlyList<EmployerRow> rows,
        int activeCount,
        IReadOnlyDictionary<Guid, EmployerType> employerTypes)
    {
        var listingsWithEmployer = rows.Count(row => row.Classification.Disclosure == EmployerDisclosure.Disclosed);
        var identifiable = rows
            .Where(row => row.Classification.Disclosure == EmployerDisclosure.Disclosed)
            .Select(row => row.CanonicalId is { } canonicalId
                ? $"id:{canonicalId}"
                : $"name:{EmployerNameNormaliser.Normalise(row.Classification.HiringEmployer)}")
            .Where(key => key != "name:")
            .Distinct(StringComparer.Ordinal)
            .Count();
        var canonicalIds = rows
            .Where(row => row.CanonicalId != null)
            .Select(row => row.CanonicalId!.Value)
            .Distinct()
            .ToList();
        var unmatchedNames = rows
            .Where(row => row.Classification.Disclosure == EmployerDisclosure.Disclosed
                && row.CanonicalId is null
                && !string.IsNullOrWhiteSpace(row.Classification.HiringEmployer))
            .Select(row => EmployerNameNormaliser.Normalise(row.Classification.HiringEmployer))
            .Distinct(StringComparer.Ordinal)
            .Count();
        var coveragePercent = activeCount == 0
            ? 0m
            : Math.Round((decimal)listingsWithEmployer / activeCount * 100m, 1, MidpointRounding.AwayFromZero);

        return new EmployerSummaryDto(
            identifiable,
            listingsWithEmployer,
            activeCount - listingsWithEmployer,
            coveragePercent,
            rows.Count(row => row.Classification.Disclosure == EmployerDisclosure.AgencyOnly),
            rows.Count(row => row.Classification.AdvertiserType == AdvertiserType.DirectEmployer),
            rows.Count(row => row.Classification.AdvertiserType == AdvertiserType.Agency),
            rows.Count(row => row.Classification.AdvertiserType == AdvertiserType.Board),
            canonicalIds.Count,
            canonicalIds.Count(id => employerTypes.GetValueOrDefault(id) == EmployerType.Agency),
            canonicalIds.Count(id => employerTypes.GetValueOrDefault(id) == EmployerType.DirectEmployer),
            unmatchedNames);
    }

    private async Task<DeduplicationSummaryDto> LoadDeduplicationAsync(int activeListings, CancellationToken cancellationToken)
    {
        if (deduplicationService is not null)
        {
            return await deduplicationService.GetSummaryAsync(cancellationToken);
        }

        return new DeduplicationSummaryDto(
            false,
            null,
            DeduplicationMatchMethods.MatchVersion,
            activeListings,
            null,
            0,
            0,
            0,
            DeduplicationEngine.MaxGroupSize,
            null);
    }

    private async Task<IReadOnlyDictionary<Guid, EmployerType>> LoadEmployerTypesAsync(CancellationToken cancellationToken)
    {
        var employers = await dbContext.Employers
            .AsNoTracking()
            .Select(employer => new { employer.Id, employer.Type })
            .ToListAsync(cancellationToken);
        return employers.ToDictionary(employer => employer.Id, employer => employer.Type);
    }

    private async Task<DataStatusDto> BuildDataStatusAsync(DateTime now, CancellationToken cancellationToken)
    {
        var lastSuccessful = await dbContext.CrawlRuns
            .AsNoTracking()
            .Where(run => run.Status == "Completed" && run.FinishedAt != null)
            .OrderByDescending(run => run.FinishedAt)
            .Select(run => run.FinishedAt)
            .FirstOrDefaultAsync(cancellationToken);

        var newestSeen = await dbContext.Jobs
            .AsNoTracking()
            .Where(job => job.IsActive)
            .OrderByDescending(job => job.LastSeenAt)
            .Select(job => (DateTime?)job.LastSeenAt)
            .FirstOrDefaultAsync(cancellationToken);

        // Round 15 (H-4): LastCrawledAt is stamped for failed attempts too, so it
        // must never be passed as "last successful". Use the latest Completed run
        // per source; a source whose only recent runs failed is not refreshed.
        var lastSuccessBySource = await dbContext.CrawlRuns
            .AsNoTracking()
            .Where(run => run.Status == "Completed" && run.FinishedAt != null)
            .GroupBy(run => run.Source)
            .Select(group => new { Source = group.Key, Last = group.Max(run => run.FinishedAt) })
            .ToDictionaryAsync(item => item.Source, item => item.Last, cancellationToken);

        var sources = await dbContext.JobSources
            .AsNoTracking()
            .Select(source => new { source.Name, source.Enabled })
            .ToListAsync(cancellationToken);

        var observations = sources
            .Select(source => new SourceRunObservation(
                source.Name,
                source.Enabled,
                lastSuccessBySource.GetValueOrDefault(source.Name),
                null))
            .ToList();

        var status = DataStatusEvaluator.Evaluate(
            now,
            lastSuccessful,
            newestSeen,
            observations,
            DataStatusEvaluator.DefaultStaleAfter);

        return new DataStatusDto(
            status.State.ToString(),
            status.LastSuccessfulUpdateUtc,
            status.NewestListingSeenUtc,
            status.EnabledSources,
            status.EnabledSourcesWithRecentSuccess,
            status.Message);
    }

    private static MetricIntervalDto ToIntervalDto(ReportingInterval interval, bool isPartial) =>
        MetricIntervalMapper.ToDto(interval, isPartial);

    private static SalaryPayPeriod ParsePayPeriod(string? value) =>
        // An unrecognised or absent pay period is NOT annual. Treating it as Year
        // would let an unknown-period amount into the annual median while the
        // frontend requires an explicit Year, so the two views would disagree.
        Enum.TryParse<SalaryPayPeriod>(value, ignoreCase: true, out var period)
            ? period
            : SalaryPayPeriod.Unknown;

    private sealed record ActiveListing(
        string Source,
        DateTime? PostedDate,
        DateTime FirstSeenAt,
        DateTime LastSeenAt,
        decimal? SalaryMin,
        decimal? SalaryMax,
        string? SalaryText,
        string? SalaryCurrency,
        string? SalaryPeriod,
        bool SalaryQuestionable,
        string EmploymentType,
        string? Company,
        string? Region,
        Guid? EmployerCanonicalId);

    private sealed record EmployerRow(EmployerClassification Classification, Guid? CanonicalId);
}
