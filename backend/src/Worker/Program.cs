using Application.DependencyInjection;
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
}

await host.RunAsync();
