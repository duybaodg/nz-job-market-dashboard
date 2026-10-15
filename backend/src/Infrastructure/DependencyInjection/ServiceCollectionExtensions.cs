using Application.Common;
using Application.Crawling;
using Application.Jobs;
using Infrastructure.Ai;
using Infrastructure.Crawling;
using Infrastructure.Firecrawl;
using Infrastructure.Persistence;
using Infrastructure.Scrapling;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Infrastructure.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddInfrastructureServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration["DATABASE_URL"]
            ?? configuration.GetConnectionString("DefaultConnection")
            ?? "Host=localhost;Port=55432;Database=nz_job_market;Username=postgres;Password=postgres";

        services.AddDbContext<ApplicationDbContext>(options =>
            options.UseNpgsql(connectionString));

        services.AddScoped<IApplicationDbContext>(provider =>
            provider.GetRequiredService<ApplicationDbContext>());
        services.Configure<FirecrawlOptions>(options =>
        {
            options.ApiKey = configuration["FIRECRAWL_API_KEY"] ?? string.Empty;
            options.BaseUrl = configuration["FIRECRAWL_BASE_URL"] ?? "https://api.firecrawl.dev/v2";
            options.TimeoutSeconds = int.TryParse(configuration["FIRECRAWL_TIMEOUT_SECONDS"], out var timeoutSeconds)
                ? timeoutSeconds
                : 60;
        });
        services.AddHttpClient<IFirecrawlClient, FirecrawlClient>((provider, client) =>
        {
            var options = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<FirecrawlOptions>>().Value;
            client.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");
            client.Timeout = TimeSpan.FromSeconds(Math.Max(options.TimeoutSeconds + 10, 30));
        });
        services.AddScoped<IFirecrawlMarkdownParser, FirecrawlMarkdownParser>();
        services.Configure<ScraplingOptions>(options =>
        {
            options.PythonExecutable = configuration["SCRAPLING_PYTHON_EXECUTABLE"] ?? "python3";
            options.WorkerScriptPath = configuration["SCRAPLING_WORKER_SCRIPT"] ?? string.Empty;
            options.FetcherType = configuration["SCRAPLING_FETCHER_TYPE"] ?? "fetcher";
            options.RobotsTxtObey = bool.TryParse(configuration["SCRAPLING_ROBOTS_TXT_OBEY"], out var robotsTxtObey)
                ? robotsTxtObey
                : true;
            options.TimeoutSeconds = int.TryParse(configuration["SCRAPLING_TIMEOUT_SECONDS"], out var timeoutSeconds)
                ? timeoutSeconds
                : 120;
            options.ProxyUrl = configuration["SCRAPLING_PROXY_URL"] ?? string.Empty;
            options.MaxPagesPerRun = int.TryParse(configuration["CRAWLER_MAX_PAGES_PER_RUN"], out var maxPages)
                ? Math.Max(maxPages, 1)
                : 50;
            options.DownloadDelaySeconds = double.TryParse(configuration["SCRAPLING_DOWNLOAD_DELAY_SECONDS"], System.Globalization.CultureInfo.InvariantCulture, out var delaySeconds)
                ? Math.Max(delaySeconds, 0)
                : 5;
            options.TimeoutSeconds = Math.Max(
                options.TimeoutSeconds,
                (int)Math.Ceiling(options.MaxPagesPerRun * options.DownloadDelaySeconds) + 60);
        });
        services.AddScoped<IJobSourceAdapter, ScraplingJobAdapter>();
        services.AddScoped<IJobSourceAdapter, FirecrawlJobAdapter>();

        // Round 9: optional OpenAI-compatible field enrichment. Default-off
        // (AI_EXTRACTION_ENABLED), and inert without a real OPENAI_API_KEY.
        services.Configure<AiFieldEnrichmentOptions>(options =>
        {
            options.Enabled = bool.TryParse(configuration["AI_EXTRACTION_ENABLED"], out var aiEnabled) && aiEnabled;
            options.ApiKey = configuration["OPENAI_API_KEY"] ?? string.Empty;
            options.BaseUrl = configuration["OPENAI_BASE_URL"] ?? "https://api.openai.com/v1";
            options.Model = configuration["OPENAI_MODEL"] ?? "gpt-4o-mini";
            options.BatchSize = int.TryParse(configuration["AI_EXTRACTION_BATCH_SIZE"], out var batchSize)
                ? Math.Max(batchSize, 1)
                : 10;
            options.TimeoutSeconds = int.TryParse(configuration["AI_EXTRACTION_TIMEOUT_SECONDS"], out var aiTimeout)
                ? Math.Max(aiTimeout, 5)
                : 60;
        });
        services.AddHttpClient<IAiFieldEnrichmentClient, OpenAiFieldEnrichmentClient>((provider, client) =>
        {
            var options = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<AiFieldEnrichmentOptions>>().Value;
            client.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");
            client.Timeout = TimeSpan.FromSeconds(Math.Max(options.TimeoutSeconds, 5));
        });
        services.AddScoped<ISourceRegistrySynchronizer, SourceRegistrySynchronizer>();
        services.AddSingleton<ISourcePriorityProvider, RegistrySourcePriorityProvider>();
        services.AddSingleton(new CrawlerLifecycleOptions
        {
            StaleAfterDays = int.TryParse(configuration["CRAWLER_JOB_STALE_DAYS"], out var staleDays)
                ? Math.Max(staleDays, 1)
                : CrawlerLifecycleOptions.DefaultStaleAfterDays,
            MissingCrawlThreshold = int.TryParse(configuration["CRAWLER_MISSING_CRAWL_THRESHOLD"], out var threshold)
                ? Math.Max(threshold, 1)
                : 2,
            RetentionDays = int.TryParse(configuration["CRAWLER_RETENTION_DAYS"], out var retentionDays)
                ? Math.Max(retentionDays, 1)
                : CrawlerLifecycleOptions.DefaultRetentionDays
        });

        return services;
    }
}
