using Application.Common;
using Application.Crawling;
using Application.History;
using Application.Jobs;
using Microsoft.EntityFrameworkCore;

namespace Worker;

public sealed class CrawlWorker(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<CrawlWorker> logger) : BackgroundService
{
    private readonly int delaySeconds = ReadPositiveInt(configuration, "CRAWLER_DELAY_SECONDS", 30);
    private readonly int pollSeconds = ReadPositiveInt(configuration, "CRAWLER_POLL_SECONDS", 60);
    private static readonly TimeSpan PurgeInterval = TimeSpan.FromHours(1);
    private DateTime? lastPurgeAt;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            // Round 15 (H-2): a single transient database/network error must not
            // escape ExecuteAsync and stop the host (BackgroundServiceException
            // behavior is StopHost by default). OperationCanceledException on
            // shutdown still propagates.
            try
            {
                await PurgeExpiredDataAsync(stoppingToken);
                await CaptureListingSnapshotAsync(stoppingToken);
                await CrawlDueSourcesAsync(stoppingToken);
                await RunAiEnrichmentAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Worker cycle failed; continuing after the poll interval.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(pollSeconds), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// Optional Round 9 AI enrichment: a bounded batch per cycle, and a no-op
    /// unless AI_EXTRACTION_ENABLED is true and a key is configured.
    /// </summary>
    private async Task RunAiEnrichmentAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var enrichment = scope.ServiceProvider.GetRequiredService<IAiFieldEnrichmentService>();
            if (!enrichment.IsEnabled)
            {
                return;
            }

            var result = await enrichment.RunBatchAsync(0, cancellationToken);
            if (result.Attempted > 0)
            {
                logger.LogInformation(
                    "AI enrichment: {Attempted} attempted, {Updated} updated, {Failed} failed.",
                    result.Attempted,
                    result.Updated,
                    result.Failed);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "AI field enrichment failed");
        }
    }

    /// <summary>
    /// Writes today's listing snapshot (idempotent per Pacific/Auckland day), so
    /// historical trends are backed by retained evidence rather than today's
    /// mutable job rows.
    /// </summary>
    private async Task CaptureListingSnapshotAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var snapshots = scope.ServiceProvider.GetRequiredService<IListingSnapshotService>();
            var result = await snapshots.CaptureAsync(cancellationToken);
            if (result.Captured)
            {
                logger.LogInformation(
                    "Listing snapshot captured for {Date}: {Listings} listings, {Active} active.",
                    result.SnapshotDate,
                    result.Listings,
                    result.Active);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Listing snapshot capture failed");
        }
    }

    /// <summary>
    /// Runs the retention purge hourly (not on every poll). The purge is
    /// idempotent, so a missed hour simply catches up on the next cycle.
    /// </summary>
    private async Task PurgeExpiredDataAsync(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        if (lastPurgeAt is { } last && now - last < PurgeInterval)
        {
            return;
        }

        try
        {
            using var scope = scopeFactory.CreateScope();
            var retention = scope.ServiceProvider.GetRequiredService<ICrawlDataRetentionService>();
            await retention.PurgeAsync(cancellationToken);
            lastPurgeAt = now;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A retention failure must not stop crawling.
            logger.LogError(exception, "Retention purge failed");
        }
    }

    private async Task CrawlDueSourcesAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
        var crawler = scope.ServiceProvider.GetRequiredService<ICrawlerOrchestrationService>();
        var priority = scope.ServiceProvider.GetRequiredService<ISourcePriorityProvider>();
        var now = DateTime.UtcNow;
        var sources = await dbContext.JobSources
            .Where(source => source.Enabled && source.TermsCheckedAt != null && source.BaseUrl != null)
            .ToListAsync(cancellationToken);
        // Sources marked "last" in the registry (SEEK, Trade Me) are crawled after
        // everything else, then alphabetically within each priority band.
        var dueSources = SourceScheduler.OrderForCrawl(
            sources.Where(source =>
                source.LastCrawledAt is null ||
                source.LastCrawledAt <= now.AddMinutes(-Math.Max(source.CrawlFrequencyMinutes, 1))),
            priority);

        foreach (var source in dueSources)
        {
            try
            {
                var result = await crawler.StartCrawlRunAsync(
                    new StartCrawlRunRequest(source.Name, source.BaseUrl!),
                    cancellationToken);
                logger.LogInformation(
                    "Crawl {Status} for {Source}: {Message}",
                    result.CrawlRun.Status,
                    source.Name,
                    result.Message);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "Unhandled crawl failure for {Source}", source.Name);
            }

            source.LastCrawledAt = DateTime.UtcNow;
            source.UpdatedAt = source.LastCrawledAt.Value;
            await dbContext.SaveChangesAsync(cancellationToken);
            await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken);
        }
    }

    private static int ReadPositiveInt(IConfiguration configuration, string key, int fallback) =>
        int.TryParse(configuration[key], out var value) ? Math.Max(value, 1) : fallback;
}
