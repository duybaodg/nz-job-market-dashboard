using Application.Analytics;
using Application.Crawling;
using Application.Deduplication;
using Application.DependencyInjection;
using Application.Employers;
using Application.Health;
using Application.History;
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
    // Keep job_sources in step with crawler/sources/sites.json.
    await scope.ServiceProvider.GetRequiredService<ISourceRegistrySynchronizer>().SyncAsync(CancellationToken.None);
}

app.UseCors("Frontend");

app.MapGet("/api/jobs", async (
        string? region,
        string? skill,
        string? search,
        string? status,
        int? days,
        IJobService jobService,
        CancellationToken cancellationToken) =>
    {
        var jobs = await jobService.GetJobsAsync(new JobListQuery(region, skill, search, status, days), cancellationToken);
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

app.MapGet("/api/analytics/freshness", async (
        IAnalyticsService analyticsService,
        CancellationToken cancellationToken) =>
    Results.Ok(await analyticsService.GetFreshnessAsync(cancellationToken)))
    .WithName("GetAnalyticsFreshness");

app.MapGet("/api/analytics/history", async (
        int? days,
        IHistoryService historyService,
        CancellationToken cancellationToken) =>
    Results.Ok(await historyService.GetHistoryAsync(days ?? 7, cancellationToken)))
    .WithName("GetAnalyticsHistory");

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

app.MapGet("/api/admin/sources", async (
        ICrawlerOrchestrationService crawlerService,
        CancellationToken cancellationToken) =>
    Results.Ok(await crawlerService.GetSourcesAsync(cancellationToken)))
    .WithName("GetSources");

app.MapGet("/api/admin/health", async (
        IHealthService healthService,
        CancellationToken cancellationToken) =>
    Results.Ok(await healthService.GetHealthAsync(cancellationToken)))
    .WithName("GetHealth");

app.MapGet("/api/admin/crawl-runs/{id:guid}", async (
        Guid id,
        ICrawlerOrchestrationService crawlerService,
        CancellationToken cancellationToken) =>
    {
        var crawlRun = await crawlerService.GetCrawlRunAsync(id, cancellationToken);
        return crawlRun is null ? Results.NotFound() : Results.Ok(crawlRun);
    })
    .WithName("GetCrawlRun");

// --- Round 8: canonical employer identity (admin-reviewed) -----------------

app.MapGet("/api/admin/employers", async (
        IEmployerIdentityService employerService,
        CancellationToken cancellationToken) =>
    Results.Ok(await employerService.GetReportAsync(cancellationToken)))
    .WithName("GetEmployers");

app.MapPost("/api/admin/employers", async (
        CreateEmployerRequest request,
        IEmployerIdentityService employerService,
        CancellationToken cancellationToken) =>
    {
        if (string.IsNullOrWhiteSpace(request.CanonicalName))
        {
            return Results.BadRequest(new { error = "canonicalName is required" });
        }

        var employer = await employerService.CreateEmployerAsync(request, cancellationToken);
        return Results.Ok(employer);
    })
    .WithName("CreateEmployer");

app.MapPost("/api/admin/employers/{id:guid}/aliases", async (
        Guid id,
        AddEmployerAliasRequest request,
        IEmployerIdentityService employerService,
        CancellationToken cancellationToken) =>
    {
        if (string.IsNullOrWhiteSpace(request.Alias))
        {
            return Results.BadRequest(new { error = "alias is required" });
        }

        var employer = await employerService.AddAliasAsync(id, request, cancellationToken);
        return employer is null
            ? Results.Conflict(new { error = "alias already belongs to another canonical or the employer was not found" })
            : Results.Ok(employer);
    })
    .WithName("AddEmployerAlias");

app.MapDelete("/api/admin/employers/{id:guid}/aliases/{alias}", async (
        Guid id,
        string alias,
        string? actor,
        IEmployerIdentityService employerService,
        CancellationToken cancellationToken) =>
    {
        var removed = await employerService.RemoveAliasAsync(id, alias, actor ?? "admin", cancellationToken);
        return removed ? Results.NoContent() : Results.NotFound();
    })
    .WithName("RemoveEmployerAlias");

app.MapDelete("/api/admin/employers/{id:guid}/aliases", async (
        Guid id,
        string alias,
        string? actor,
        IEmployerIdentityService employerService,
        CancellationToken cancellationToken) =>
    {
        var removed = await employerService.RemoveAliasAsync(id, alias, actor ?? "admin", cancellationToken);
        return removed ? Results.NoContent() : Results.NotFound();
    })
    .WithName("RemoveEmployerAliasByQuery");

app.MapPost("/api/admin/employers/seed", async (
        string? actor,
        IEmployerIdentityService employerService,
        CancellationToken cancellationToken) =>
    Results.Ok(await employerService.SeedFromCompaniesAsync(actor ?? "admin", cancellationToken)))
    .WithName("SeedEmployers");

app.MapPost("/api/admin/employers/match", async (
        string? actor,
        IEmployerIdentityService employerService,
        CancellationToken cancellationToken) =>
    Results.Ok(await employerService.MatchJobsAsync(actor ?? "admin", cancellationToken)))
    .WithName("MatchEmployers");

// --- Round 8: listing deduplication provenance -----------------------------

app.MapGet("/api/admin/duplicates", async (
        IListingDeduplicationService dedupService,
        CancellationToken cancellationToken) =>
    Results.Ok(await dedupService.GetGroupsAsync(cancellationToken)))
    .WithName("GetDuplicateGroups");

app.MapPost("/api/admin/dedup/run", async (
        string? trigger,
        IListingDeduplicationService dedupService,
        CancellationToken cancellationToken) =>
    Results.Ok(await dedupService.RunAsync(trigger ?? "manual", cancellationToken)))
    .WithName("RunDeduplication");

app.Run();
