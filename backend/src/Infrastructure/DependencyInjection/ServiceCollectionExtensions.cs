using Application.Common;
using Application.Crawling;
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
        var connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? configuration["DATABASE_URL"]
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
        });
        services.AddScoped<IJobSourceAdapter, ScraplingJobAdapter>();
        services.AddScoped<IJobSourceAdapter, FirecrawlJobAdapter>();

        return services;
    }
}
