namespace Worker;

public sealed class CrawlWorker : BackgroundService
{
    private readonly ILogger<CrawlWorker> logger;

    public CrawlWorker(ILogger<CrawlWorker> logger)
    {
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("Crawler worker placeholder running at: {Time}", DateTimeOffset.UtcNow);
            }

            await Task.Delay(TimeSpan.FromHours(24), stoppingToken);
        }
    }
}
