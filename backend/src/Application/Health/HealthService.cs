using Application.Common;
using Microsoft.EntityFrameworkCore;

namespace Application.Health;

public interface IHealthService
{
    Task<HealthReportDto> GetHealthAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Small process-wide cache so repeated dashboard polls do not re-aggregate the
/// crawl history. Kept dependency-free (no IMemoryCache package) and clock-driven
/// so the 30 second boundary is unit-testable.
/// </summary>
public sealed class HealthReportCache
{
    private readonly object gate = new();
    private HealthReportDto? report;
    private DateTime expiresAtUtc;

    public bool TryGet(DateTime now, out HealthReportDto? cached)
    {
        lock (gate)
        {
            if (report is not null && now < expiresAtUtc)
            {
                cached = report;
                return true;
            }

            // Drop an expired entry so a caller with a skewed/smaller clock
            // reading can never resurrect it.
            report = null;
            expiresAtUtc = default;
            cached = null;
            return false;
        }
    }

    public void Set(DateTime now, HealthReportDto value, TimeSpan timeToLive)
    {
        lock (gate)
        {
            report = value;
            expiresAtUtc = now.Add(timeToLive);
        }
    }

    public void Clear()
    {
        lock (gate)
        {
            report = null;
            expiresAtUtc = default;
        }
    }
}

public sealed class HealthService(IApplicationDbContext dbContext, HealthReportCache cache) : IHealthService
{
    public static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(30);

    public async Task<HealthReportDto> GetHealthAsync(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;

        if (cache.TryGet(now, out var cached))
        {
            return cached!;
        }

        var report = await ComputeAsync(now, cancellationToken);
        cache.Set(now, report, CacheDuration);
        return report;
    }

    /// <summary>
    /// Builds the health report from existing tables only. Public so tests can
    /// pin a deterministic clock without waiting on the cache.
    /// </summary>
    public async Task<HealthReportDto> ComputeAsync(DateTime now, CancellationToken cancellationToken)
    {
        var sources = await dbContext.JobSources
            .AsNoTracking()
            .OrderBy(source => source.Name)
            .ToListAsync(cancellationToken);

        var jobCounts = (await dbContext.Jobs
                .AsNoTracking()
                .Where(job => job.IsActive)
                .GroupBy(job => job.Source)
                .Select(group => new { Source = group.Key, Count = group.Count() })
                .ToListAsync(cancellationToken))
            .ToDictionary(row => row.Source, row => row.Count, StringComparer.OrdinalIgnoreCase);

        var runs = await dbContext.CrawlRuns
            .AsNoTracking()
            .OrderByDescending(run => run.StartedAt)
            .Select(run => new { run.Source, run.Status, run.TotalJobsFound, run.StartedAt, run.ErrorMessage })
            .ToListAsync(cancellationToken);

        var runsBySource = runs
            .GroupBy(run => run.Source, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<CrawlRunSnapshot>)group
                    .Select(run => new CrawlRunSnapshot(run.Status, run.TotalJobsFound, run.StartedAt, run.ErrorMessage))
                    .ToList(),
                StringComparer.OrdinalIgnoreCase);

        var entries = new List<SourceHealthDto>(sources.Count);

        foreach (var source in sources)
        {
            runsBySource.TryGetValue(source.Name, out var sourceRuns);
            sourceRuns ??= [];
            var jobsYielded = jobCounts.TryGetValue(source.Name, out var count) ? count : 0;
            var evaluation = SourceHealthEvaluator.Evaluate(
                new SourceHealthInput(
                    source.Enabled,
                    source.LastCrawledAt,
                    source.CrawlFrequencyMinutes,
                    jobsYielded,
                    sourceRuns),
                now);

            entries.Add(new SourceHealthDto(
                source.Name,
                source.Enabled,
                source.Parser,
                source.Method,
                source.LastCrawledAt ?? sourceRuns.FirstOrDefault()?.StartedAt,
                evaluation.LastStatus,
                evaluation.LastError,
                evaluation.ConsecutiveFailures,
                evaluation.ConsecutiveZeroYield,
                jobsYielded,
                evaluation.State,
                evaluation.DegradeReason,
                source.CrawlFrequencyMinutes));
        }

        var summary = new HealthSummaryDto(
            entries.Count(entry => entry.State == SourceHealthState.Healthy),
            entries.Count(entry => entry.State == SourceHealthState.Degraded),
            entries.Count(entry => entry.State == SourceHealthState.Stale),
            entries.Count(entry => entry.State == SourceHealthState.Empty),
            entries.Count(entry => entry.State == SourceHealthState.Disabled),
            entries.Count(entry => entry.State == SourceHealthState.NeverRun),
            entries.Count);

        return new HealthReportDto(now, summary, entries);
    }
}
