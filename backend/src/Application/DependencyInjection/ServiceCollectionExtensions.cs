using Application.Analytics;
using Application.Crawling;
using Application.Deduplication;
using Application.Health;
using Application.History;
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
        services.AddScoped<ISkillBackfillService, SkillBackfillService>();
        services.AddScoped<ICrawlDataRetentionService, CrawlDataRetentionService>();
        services.AddScoped<IJobContentValidator, JobContentValidator>();
        services.AddScoped<IAnalyticsService, AnalyticsService>();
        services.AddScoped<IHistoryService, HistoryService>();
        services.AddScoped<IListingSnapshotService, ListingSnapshotService>();
        services.AddScoped<ICrawlerOrchestrationService, CrawlerOrchestrationService>();
        // Injected so analytics interval boundaries are deterministic in tests.
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<INormalisationService, NormalisationService>();
        services.AddScoped<IDeduplicationService, DeduplicationService>();
        services.AddSingleton<HealthReportCache>();
        services.AddScoped<IHealthService, HealthService>();

        // The shared skill taxonomy is loaded once from
        // crawler/skills_taxonomy.json and reused for every extraction.
        services.AddSingleton(SkillsTaxonomy.Default);
        services.AddSingleton(SkillExtractor.Default);

        return services;
    }
}
