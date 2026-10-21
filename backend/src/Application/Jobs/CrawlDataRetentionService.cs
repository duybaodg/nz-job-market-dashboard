using System.Linq.Expressions;
using Application.Common;
using Application.Crawling;
using Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Application.Jobs;

public sealed record CrawlDataRetentionResult(int JobsDeleted, int CrawlRunsDeleted, int RawPagesDeleted)
{
    public static CrawlDataRetentionResult Empty { get; } = new(0, 0, 0);
}

/// <summary>
/// Hard-deletes crawl data older than the configured retention window
/// (<c>CRAWLER_RETENTION_DAYS</c>, default 20 days):
/// <list type="bullet">
/// <item>job rows whose <c>last_seen_at</c> (falling back to <c>created_at</c>
/// when <c>last_seen_at</c> was never written) is older than the window;</item>
/// <item><c>crawl_runs</c> history older than the window, together with the
/// <c>raw_job_pages</c> that hang off those runs (the FK has no cascade, so the
/// pages must go first).</item>
/// </list>
/// Each delete is a bounded set-based <c>DELETE</c> (batches of 1000 ids), so a
/// long-overdue purge cannot lock the largest table for one huge transaction.
/// A run over an empty window removes nothing and repeating the purge is a
/// no-op.
/// </summary>
public interface ICrawlDataRetentionService
{
    Task<CrawlDataRetentionResult> PurgeAsync(CancellationToken cancellationToken);
}

public sealed class CrawlDataRetentionService(
    IApplicationDbContext dbContext,
    CrawlerLifecycleOptions lifecycleOptions,
    ILogger<CrawlDataRetentionService> logger) : ICrawlDataRetentionService
{
    /// <summary>
    /// Cutoff for the retention window. Rows whose reference timestamp is
    /// <em>strictly older</em> than this instant are deleted; a row that is
    /// exactly <paramref name="retentionDays"/> old is kept. A non-positive
    /// window is clamped to one day so a misconfiguration cannot wipe live data.
    /// </summary>
    public static DateTime Cutoff(DateTime nowUtc, int retentionDays) =>
        nowUtc.AddDays(-Math.Max(retentionDays, 1));

    /// <summary>
    /// In-memory equivalent of <see cref="ExpiredJob"/> (which is what the
    /// database <c>DELETE</c> uses). Kept as the readable statement of the
    /// boundary rule; unit tests pin both so they cannot drift.
    /// </summary>
    public static bool IsExpired(DateTime lastSeenAt, DateTime createdAt, DateTime nowUtc, int retentionDays)
    {
        var reference = lastSeenAt == default ? createdAt : lastSeenAt;
        return reference < Cutoff(nowUtc, retentionDays);
    }

    /// <summary>Translatable predicate for the job retention <c>DELETE</c>.</summary>
    public static Expression<Func<Job, bool>> ExpiredJob(DateTime cutoff) =>
        job => (job.LastSeenAt != default(DateTime) && job.LastSeenAt < cutoff)
            || (job.LastSeenAt == default(DateTime) && job.CreatedAt < cutoff);

    /// <summary>Translatable predicate for the crawl-run retention <c>DELETE</c>.</summary>
    public static Expression<Func<CrawlRun, bool>> ExpiredCrawlRun(DateTime cutoff) =>
        run => run.StartedAt < cutoff;

    /// <summary>
    /// Open or resolved duplicate-review candidates that point at any of the
    /// jobs about to be purged (both sides of the pair, including null).
    /// </summary>
    public static Expression<Func<DuplicateReviewCandidate, bool>> ReviewsReferencing(
        IReadOnlyCollection<Guid> jobIds) =>
        candidate => jobIds.Contains(candidate.JobId)
            || (candidate.CandidateJobId != null && jobIds.Contains(candidate.CandidateJobId.Value));

    /// <summary>Bound each DELETE so a large backlog is purged in short transactions.</summary>
    public const int BatchSize = 1000;

    public async Task<CrawlDataRetentionResult> PurgeAsync(CancellationToken cancellationToken)
    {
        var cutoff = Cutoff(DateTime.UtcNow, lifecycleOptions.RetentionDays);

        // raw_job_pages is a child of crawl_runs with no cascade delete, so its
        // rows must be removed before the runs they belong to. Old orphaned
        // pages (never attached to a run) are removed by the same predicate.
        var rawPagesDeleted = 0;
        while (true)
        {
            var pageIds = await dbContext.RawJobPages
                .Where(page => page.CrawledAt < cutoff
                    || dbContext.CrawlRuns.Any(run => run.Id == page.CrawlRunId && run.StartedAt < cutoff))
                .OrderBy(page => page.Id)
                .Select(page => page.Id)
                .Take(BatchSize)
                .ToListAsync(cancellationToken);
            if (pageIds.Count == 0)
            {
                break;
            }

            rawPagesDeleted += await dbContext.RawJobPages
                .Where(page => pageIds.Contains(page.Id))
                .ExecuteDeleteAsync(cancellationToken);
        }

        var crawlRunsDeleted = 0;
        while (true)
        {
            var runIds = await dbContext.CrawlRuns
                .Where(ExpiredCrawlRun(cutoff))
                .OrderBy(run => run.Id)
                .Select(run => run.Id)
                .Take(BatchSize)
                .ToListAsync(cancellationToken);
            if (runIds.Count == 0)
            {
                break;
            }

            crawlRunsDeleted += await dbContext.CrawlRuns
                .Where(run => runIds.Contains(run.Id))
                .ExecuteDeleteAsync(cancellationToken);
        }

        var jobsDeleted = 0;
        while (true)
        {
            var jobIds = await dbContext.Jobs
                .Where(ExpiredJob(cutoff))
                .OrderBy(job => job.Id)
                .Select(job => job.Id)
                .Take(BatchSize)
                .ToListAsync(cancellationToken);
            if (jobIds.Count == 0)
            {
                break;
            }

            // Round 15 (L-8): remove duplicate-review rows that reference a
            // purged job in the same pass, so the admin report never shows dead
            // references (there is no FK/cascade on this table).
            await dbContext.DuplicateReviewCandidates
                .Where(ReviewsReferencing(jobIds))
                .ExecuteDeleteAsync(cancellationToken);

            jobsDeleted += await dbContext.Jobs
                .Where(job => jobIds.Contains(job.Id))
                .ExecuteDeleteAsync(cancellationToken);
        }

        logger.LogInformation(
            "Retention purged {JobsDeleted} jobs, {CrawlRunsDeleted} crawl runs, {RawPagesDeleted} raw pages "
            + "older than {RetentionDays} days.",
            jobsDeleted,
            crawlRunsDeleted,
            rawPagesDeleted,
            lifecycleOptions.RetentionDays);

        return new CrawlDataRetentionResult(jobsDeleted, crawlRunsDeleted, rawPagesDeleted);
    }
}
