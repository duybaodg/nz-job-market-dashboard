using Application.Crawling;
using Application.DependencyInjection;
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
    // Repair hub/landing-page records created by older worker versions.
    await scope.ServiceProvider.GetRequiredService<IJobQualityGuard>().DeactivateLowQualityJobsAsync(CancellationToken.None);
}

await host.RunAsync();
