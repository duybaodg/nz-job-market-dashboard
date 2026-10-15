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
    IListingDeduplicationService? deduplicationService = null) : IAnalyticsService
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public async Task<OverviewDto> GetOverviewAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var active = await LoadActiveListingsAsync(cancellationToken);
        var categories = await LoadSourceCategoriesAsync(cancellationToken);
        var dateObservations = active
            .Select(job => new ListingDateObservation(job.PostedDate, job.FirstSeenAt))
            .ToList();

        var interval = NzReportingCalendar.LastCompleteDays(7, now);
        var previousInterval = NzReportingCalendar.PreviousPeriod(interval);

        var newlyDiscovered = ListingMetrics.Count(dateObservations, interval, DateBasis.Discovered);
        var newlyPosted = ListingMetrics.Count(dateObservations, interval, DateBasis.Posted);

        var discoveredChange = BuildChange(
            newlyDiscovered.Count,
            HistoryAvailable(previousInterval, now)
                ? ListingMetrics.Count(dateObservations, previousInterval, DateBasis.Discovered).Count
                : null);
        var postedChange = BuildChange(
            newlyPosted.Count,
            HistoryAvailable(previousInterval, now)
                ? ListingMetrics.Count(dateObservations, previousInterval, DateBasis.Posted).Count
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
            newlyPosted.WithPostedDate,
            newlyPosted.MissingPostedDate,
            discoveredChange,
            postedChange,
            ToSalaryDto(salary),
            employerSummary,
            dataStatus,
            active
                .Where(job => !string.IsNullOrWhiteSpace(job.Region))
                .GroupBy(job => job.Region!)
                .OrderByDescending(group => group.Count())
                .Select(group => group.Key)
                .FirstOrDefault(),
            await dbContext.JobSkills
                .Where(skill => skill.Job != null && skill.Job.IsActive)
                .GroupBy(skill => skill.SkillName)
                .OrderByDescending(group => group.Count())
                .Select(group => group.Key)
                .FirstOrDefaultAsync(cancellationToken),
            ToIntervalDto(interval, isPartial: false),
            deduplication.UniqueVacancies,
            deduplication);
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
            .Select(group => new JobsByRegionDto(group.Key, group.Count()))
            .ToListAsync(cancellationToken);
    }

    public async Task<FreshnessDto> GetFreshnessAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var active = await LoadActiveListingsAsync(cancellationToken);
        var dateObservations = active
            .Select(job => new ListingDateObservation(job.PostedDate, job.FirstSeenAt))
            .ToList();
        var windows = new List<FreshnessWindowDto>();

        foreach (var days in FreshnessWindows.All)
        {
            var interval = NzReportingCalendar.LastCompleteDays(days, now);
            var previousInterval = NzReportingCalendar.PreviousPeriod(interval);
            var comparisonAvailable = HistoryAvailable(previousInterval, now);

            var posted = ListingMetrics.Count(dateObservations, interval, DateBasis.Posted);
            var discovered = ListingMetrics.Count(dateObservations, interval, DateBasis.Discovered);
            var previousPosted = comparisonAvailable
                ? ListingMetrics.Count(dateObservations, previousInterval, DateBasis.Posted).Count
                : 0;
            var previousDiscovered = comparisonAvailable
                ? ListingMetrics.Count(dateObservations, previousInterval, DateBasis.Discovered).Count
                : 0;

            windows.Add(new FreshnessWindowDto(
                days,
                ToIntervalDto(interval, isPartial: false),
                posted.Count,
                previousPosted,
                discovered.Count,
                previousDiscovered,
                posted.WithPostedDate,
                posted.MissingPostedDate,
                comparisonAvailable));
        }

        var deduplication = await LoadDeduplicationAsync(active.Count, cancellationToken);
        return new FreshnessDto(
            active.Count,
            now,
            await BuildDataStatusAsync(now, cancellationToken),
            windows,
            deduplication.UniqueVacancies,
            deduplication);
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
    private static bool HistoryAvailable(ReportingInterval previousInterval, DateTime now) =>
        previousInterval.StartUtc >= now.AddDays(-CrawlerLifecycleOptions.DefaultRetentionDays);

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

        var sources = await dbContext.JobSources
            .AsNoTracking()
            .Select(source => new SourceRunObservation(source.Name, source.Enabled, source.LastCrawledAt, null))
            .ToListAsync(cancellationToken);

        var status = DataStatusEvaluator.Evaluate(
            now,
            lastSuccessful,
            newestSeen,
            sources,
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
        Enum.TryParse<SalaryPayPeriod>(value, ignoreCase: true, out var period)
            ? period
            : SalaryPayPeriod.Year;

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
