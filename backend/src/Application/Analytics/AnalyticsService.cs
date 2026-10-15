using Application.Common;
using Application.Crawling;
using Application.Metrics;
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
    TimeProvider? timeProvider = null) : IAnalyticsService
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public async Task<OverviewDto> GetOverviewAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var active = await LoadActiveListingsAsync(cancellationToken);
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
        var listingsWithEmployer = active.Count(job => !string.IsNullOrWhiteSpace(job.Company));
        var identifiable = active
            .Where(job => !string.IsNullOrWhiteSpace(job.Company))
            .Select(job => job.Company!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        var employerCoverage = active.Count == 0
            ? 0m
            : Math.Round((decimal)listingsWithEmployer / active.Count * 100m, 1, MidpointRounding.AwayFromZero);

        var dataStatus = await BuildDataStatusAsync(now, cancellationToken);

        return new OverviewDto(
            now,
            active.Count,
            discoveredChange,
            postedChange,
            ToSalaryDto(salary),
            new EmployerSummaryDto(identifiable, listingsWithEmployer, active.Count - listingsWithEmployer, employerCoverage),
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
            ToIntervalDto(interval, isPartial: false));
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

        return new FreshnessDto(
            active.Count,
            now,
            await BuildDataStatusAsync(now, cancellationToken),
            windows);
    }

    private static ChangeDto BuildChange(int current, int? previous)
    {
        var change = ChangeCalculation.Compute(current, previous);
        return new ChangeDto(
            change.Current,
            change.Previous,
            change.AbsoluteChange,
            change.PercentChange,
            change.ComparisonAvailable,
            change.NoPriorBaseline,
            change.Note);
    }

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
                job.Region))
            .ToListAsync(cancellationToken);
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
        new(
            interval.StartUtc,
            interval.EndUtc,
            interval.StartLocalDate.ToString("yyyy-MM-dd"),
            interval.EndLocalDateExclusive.ToString("yyyy-MM-dd"),
            (int)Math.Round(interval.Duration.TotalDays),
            interval.Label,
            isPartial);

    private static SalaryPayPeriod ParsePayPeriod(string? value) =>
        Enum.TryParse<SalaryPayPeriod>(value, ignoreCase: true, out var period)
            ? period
            : SalaryPayPeriod.Year;

    private sealed record ActiveListing(
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
        string? Region);
}
