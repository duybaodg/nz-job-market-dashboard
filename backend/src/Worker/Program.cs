using Application.Crawling;
using Application.Deduplication;
using Application.DependencyInjection;
using Application.Employers;
using Application.History;
using Application.Jobs;
using Infrastructure.DependencyInjection;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Worker;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddApplicationServices();
builder.Services.AddInfrastructureServices(builder.Configuration);
builder.Services.AddHostedService<CrawlWorker>();

var host = builder.Build();
using (var scope = host.Services.CreateScope())
{
    await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database.MigrateAsync();
    // Reconcile job_sources with the crawler registry before crawling starts.
    await scope.ServiceProvider.GetRequiredService<ISourceRegistrySynchronizer>().SyncAsync(CancellationToken.None);
    // One-off repair: remove junk left by older worker versions and
    // re-normalise regions with the Phase-2 mapping. Idempotent.
    await scope.ServiceProvider.GetRequiredService<IJobQualityGuard>().RunAsync(CancellationToken.None);
    // Round 7: re-extract technical skills for every stored job from its
    // description with the shared taxonomy. Idempotent.
    await scope.ServiceProvider.GetRequiredService<ISkillBackfillService>().RunAsync(CancellationToken.None);
    // Round 9: deterministic deep extraction for work mode, seniority,
    // employment type and salary. Only fills blanks; idempotent.
    await scope.ServiceProvider.GetRequiredService<IJobFieldExtractionService>().BackfillAsync(CancellationToken.None);
    // Round 9: one bounded AI enrichment batch at startup, only when enabled.
    var aiEnrichment = scope.ServiceProvider.GetRequiredService<IAiFieldEnrichmentService>();
    if (aiEnrichment.IsEnabled)
    {
        await aiEnrichment.RunBatchAsync(0, CancellationToken.None);
    }
    // Round 8: bootstrap canonical employers once from the distinct company
    // values, then re-resolve every job by exact/alias match (no fuzzy merge).
    var employerIdentity = scope.ServiceProvider.GetRequiredService<IEmployerIdentityService>();
    await employerIdentity.SeedFromCompaniesAsync("worker:startup", CancellationToken.None);
    await employerIdentity.MatchJobsAsync("worker:startup", CancellationToken.None);
    // Round 8: conservative, auditable listing deduplication pass. Incremental
    // and idempotent; publishes unique vacancies only after validation.
    await scope.ServiceProvider.GetRequiredService<IListingDeduplicationService>().RunAsync("worker:startup", CancellationToken.None);
    // Retention: hard-delete jobs and crawl history older than
    // CRAWLER_RETENTION_DAYS (default 20). Idempotent.
    await scope.ServiceProvider.GetRequiredService<ICrawlDataRetentionService>().PurgeAsync(CancellationToken.None);
    // P2: retain today's listing snapshot before crawling (idempotent per NZ day).
    await scope.ServiceProvider.GetRequiredService<IListingSnapshotService>().CaptureAsync(CancellationToken.None);
}

await host.RunAsync();
