using Application.Analytics;
using Application.Crawling;
using Application.DependencyInjection;
using Application.Jobs;
using Infrastructure.DependencyInjection;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddCors(options =>
{
    options.AddPolicy("Frontend", policy =>
        policy.WithOrigins(
                "http://localhost:5173",
                "http://localhost:5174",
                "http://127.0.0.1:5173",
                "http://127.0.0.1:5174")
            .AllowAnyHeader()
            .AllowAnyMethod());
});
builder.Services.AddApplicationServices();
builder.Services.AddInfrastructureServices(builder.Configuration);

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    await dbContext.Database.MigrateAsync();
}

app.UseCors("Frontend");

app.MapGet("/api/jobs", async (
        string? region,
        string? skill,
        string? search,
        IJobService jobService,
        CancellationToken cancellationToken) =>
    {
        var jobs = await jobService.GetJobsAsync(new JobListQuery(region, skill, search), cancellationToken);
        return Results.Ok(jobs);
    })
    .WithName("GetJobs");

app.MapGet("/api/jobs/{id:guid}", async (
        Guid id,
        IJobService jobService,
        CancellationToken cancellationToken) =>
    {
        var job = await jobService.GetJobAsync(id, cancellationToken);
        return job is null ? Results.NotFound() : Results.Ok(job);
    })
    .WithName("GetJob");

app.MapGet("/api/analytics/overview", async (
        IAnalyticsService analyticsService,
        CancellationToken cancellationToken) =>
    Results.Ok(await analyticsService.GetOverviewAsync(cancellationToken)))
    .WithName("GetAnalyticsOverview");

app.MapGet("/api/analytics/top-skills", async (
        IAnalyticsService analyticsService,
        CancellationToken cancellationToken) =>
    Results.Ok(await analyticsService.GetTopSkillsAsync(cancellationToken)))
    .WithName("GetTopSkills");

app.MapGet("/api/analytics/jobs-by-region", async (
        IAnalyticsService analyticsService,
        CancellationToken cancellationToken) =>
    Results.Ok(await analyticsService.GetJobsByRegionAsync(cancellationToken)))
    .WithName("GetJobsByRegion");

app.MapPost("/api/admin/crawl-runs", async (
        StartCrawlRunRequest request,
        ICrawlerOrchestrationService crawlerService,
        CancellationToken cancellationToken) =>
    {
        var result = await crawlerService.StartCrawlRunAsync(request, cancellationToken);
        return result.Started ? Results.Ok(result) : Results.BadRequest(result);
    })
    .WithName("StartCrawlRun");

app.MapGet("/api/admin/crawl-runs", async (
        ICrawlerOrchestrationService crawlerService,
        CancellationToken cancellationToken) =>
    Results.Ok(await crawlerService.GetCrawlRunsAsync(cancellationToken)))
    .WithName("GetCrawlRuns");

app.MapGet("/api/admin/crawl-runs/{id:guid}", async (
        Guid id,
        ICrawlerOrchestrationService crawlerService,
        CancellationToken cancellationToken) =>
    {
        var crawlRun = await crawlerService.GetCrawlRunAsync(id, cancellationToken);
        return crawlRun is null ? Results.NotFound() : Results.Ok(crawlRun);
    })
    .WithName("GetCrawlRun");

app.Run();
