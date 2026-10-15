using Application.Crawling;
using Application.DependencyInjection;
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
    // Retention: hard-delete jobs and crawl history older than
    // CRAWLER_RETENTION_DAYS (default 20). Idempotent.
    await scope.ServiceProvider.GetRequiredService<ICrawlDataRetentionService>().PurgeAsync(CancellationToken.None);
    // P2: retain today's listing snapshot before crawling (idempotent per NZ day).
    await scope.ServiceProvider.GetRequiredService<IListingSnapshotService>().CaptureAsync(CancellationToken.None);
}

await host.RunAsync();
