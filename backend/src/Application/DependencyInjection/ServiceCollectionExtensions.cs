using Application.Analytics;
using Application.Crawling;
using Application.Deduplication;
using Application.Health;
using Application.Jobs;
using Application.Normalisation;
using Microsoft.Extensions.DependencyInjection;

namespace Application.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddApplicationServices(this IServiceCollection services)
    {
        services.AddScoped<IJobService, JobService>();
        services.AddScoped<IJobQualityGuard, JobQualityGuard>();
        services.AddScoped<ICrawlDataRetentionService, CrawlDataRetentionService>();
        services.AddScoped<IJobContentValidator, JobContentValidator>();
        services.AddScoped<IAnalyticsService, AnalyticsService>();
        services.AddScoped<ICrawlerOrchestrationService, CrawlerOrchestrationService>();
        services.AddScoped<INormalisationService, NormalisationService>();
        services.AddScoped<IDeduplicationService, DeduplicationService>();
        services.AddSingleton<HealthReportCache>();
        services.AddScoped<IHealthService, HealthService>();

        return services;
    }
}
