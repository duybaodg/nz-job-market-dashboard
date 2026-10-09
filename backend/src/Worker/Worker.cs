using Application.Common;
using Application.Crawling;
using Microsoft.EntityFrameworkCore;

namespace Worker;

public sealed class CrawlWorker(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<CrawlWorker> logger) : BackgroundService
{
    private readonly int delaySeconds = ReadPositiveInt(configuration, "CRAWLER_DELAY_SECONDS", 30);
    private readonly int pollSeconds = ReadPositiveInt(configuration, "CRAWLER_POLL_SECONDS", 60);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await CrawlDueSourcesAsync(stoppingToken);
            await Task.Delay(TimeSpan.FromSeconds(pollSeconds), stoppingToken);
        }
    }

    private async Task CrawlDueSourcesAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
        var crawler = scope.ServiceProvider.GetRequiredService<ICrawlerOrchestrationService>();
        var now = DateTime.UtcNow;
        var sources = await dbContext.JobSources
            .Where(source => source.Enabled && source.TermsCheckedAt != null && source.BaseUrl != null)
            .OrderBy(source => source.Name)
            .ToListAsync(cancellationToken);
        var dueSources = sources.Where(source =>
            source.LastCrawledAt is null ||
            source.LastCrawledAt <= now.AddMinutes(-Math.Max(source.CrawlFrequencyMinutes, 1)));

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
