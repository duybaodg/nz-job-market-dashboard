using System.Net;
using Api.Health;
using Api.Security;
using Application.Analytics;
using Application.Crawling;
using Application.Deduplication;
using Application.DependencyInjection;
using Application.Employers;
using Application.Health;
using Application.History;
using Application.Jobs;
using Application.Quality;
using Infrastructure.DependencyInjection;
using Infrastructure.Persistence;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Round 15: the rate limiter and log identity need the real client address.
// Only the compose nginx network is trusted to set X-Forwarded-*; a direct
// caller of port 5050 cannot spoof its address. The network is pinned in
// docker-compose.yml (TRUSTED_PROXY_NETWORKS overrides it for a custom deploy).
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.ForwardLimit = 1;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
    var trusted = builder.Configuration["TRUSTED_PROXY_NETWORKS"] ?? "172.30.0.0/24";
    foreach (var entry in trusted.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        var parts = entry.Split('/', 2);
        if (parts.Length == 2
            && IPAddress.TryParse(parts[0], out var network)
            && int.TryParse(parts[1], out var prefix))
        {
            options.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(network, prefix));
        }
    }
});

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

// Round 13: shared-secret gate for the crawler health view. When the env var is
// absent the guard fails closed and every health request is refused.
builder.Services.AddSingleton(new HealthAccessGuard(builder.Configuration["HEALTH_ACCESS_PASSWORD"]));

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    // Round 15 (M-9): the API and worker can start together on a fresh deploy;
    // an advisory lock serialises the migration + registry sync.
    await DatabaseStartup.MigrateAndSyncAsync(
        dbContext,
        scope.ServiceProvider.GetRequiredService<ISourceRegistrySynchronizer>(),
        scope.ServiceProvider.GetRequiredService<IRegistryEventRecorder>(),
        CancellationToken.None);
}

app.UseCors("Frontend");
// Must run before the admin gate so the health limiter keys on the real client.
app.UseForwardedHeaders();
app.UseAdminAccess();

app.MapGet("/api/jobs", async (
        string? region,
        string? skill,
        string? search,
        string? status,
        int? days,
        int? limit,
        int? offset,
        IJobService jobService,
        CancellationToken cancellationToken) =>
    {
        if (days is < 1 or > 365)
        {
            return Results.BadRequest(new { error = "days must be between 1 and 365" });
        }

        if (limit is < 1 or > JobService.MaxLimit)
        {
            return Results.BadRequest(new { error = $"limit must be between 1 and {JobService.MaxLimit}" });
        }

        if (offset is < 0)
        {
            return Results.BadRequest(new { error = "offset must be zero or greater" });
        }

        var jobs = await jobService.GetJobsAsync(
            new JobListQuery(region, skill, search, status, days, limit, offset),
            cancellationToken);
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

// Release B1: intentionally limited public quality summary. This is not the
// protected health payload; it exposes coverage/denominator measures only.
app.MapGet("/api/analytics/quality", async (
        IQualitySummaryService qualityService,
        CancellationToken cancellationToken) =>
    Results.Ok(await qualityService.GetQualitySummaryAsync(cancellationToken)))
    .WithName("GetQualitySummary");

// Round 22 (C2): one two-cohort comparison over the existing filter
// vocabulary, with identical date rules and a shared eligibility gate.
app.MapGet("/api/analytics/compare", async (
        int? days,
        string? aRegion,
        string? aRoleFamily,
        string? aDataSubRole,
        string? aSeniority,
        string? aWorkMode,
        string? aSkill,
        string? bRegion,
        string? bRoleFamily,
        string? bDataSubRole,
        string? bSeniority,
        string? bWorkMode,
        string? bSkill,
        ICohortComparisonService comparisonService,
        CancellationToken cancellationToken) =>
    {
        if (days is < 1 or > 365)
        {
            return Results.BadRequest(new { error = "days must be between 1 and 365" });
        }

        var filters = new[]
        {
            aRegion, aRoleFamily, aDataSubRole, aSeniority, aWorkMode, aSkill,
            bRegion, bRoleFamily, bDataSubRole, bSeniority, bWorkMode, bSkill
        };
        if (filters.Any(value => value is { Length: > 120 }))
        {
            return Results.BadRequest(new { error = "comparison filters must be 120 characters or fewer" });
        }

        var query = new CohortComparisonQuery(
            days ?? 7,
            new ComparisonCohortQuery(
                CleanHistoryFilter(aRegion),
                CleanHistoryFilter(aRoleFamily),
                CleanHistoryFilter(aDataSubRole),
                CleanHistoryFilter(aSeniority),
                CleanHistoryFilter(aWorkMode),
                CleanHistoryFilter(aSkill)),
            new ComparisonCohortQuery(
                CleanHistoryFilter(bRegion),
                CleanHistoryFilter(bRoleFamily),
                CleanHistoryFilter(bDataSubRole),
                CleanHistoryFilter(bSeniority),
                CleanHistoryFilter(bWorkMode),
                CleanHistoryFilter(bSkill)));
        return Results.Ok(await comparisonService.CompareAsync(query, cancellationToken));
    })
    .WithName("CompareCohorts");

// Round 22 (C1): versioned Data & Analytics sub-role breakdown. The response
// carries the taxonomy version beside every count and salary sample.
app.MapGet("/api/analytics/data-roles", async (
        string? region,
        string? seniority,
        string? workMode,
        string? skill,
        IDataRoleExplorerService explorerService,
        CancellationToken cancellationToken) =>
    {
        if (new[] { region, seniority, workMode, skill }.Any(value => value is { Length: > 120 }))
        {
            return Results.BadRequest(new { error = "role explorer filters must be 120 characters or fewer" });
        }

        var query = new DataRoleExplorerQuery(
            CleanHistoryFilter(region),
            CleanHistoryFilter(seniority),
            CleanHistoryFilter(workMode),
            CleanHistoryFilter(skill));
        return Results.Ok(await explorerService.GetDataRolesAsync(query, cancellationToken));
    })
    .WithName("GetDataRoles");

app.MapGet("/api/analytics/history", async (
        int? days,
        string? region,
        string? roleFamily,
        string? seniority,
        string? skill,
        IHistoryService historyService,
        CancellationToken cancellationToken) =>
    {
        if (days is < 1 or > 365)
        {
            return Results.BadRequest(new { error = "days must be between 1 and 365" });
        }

        if (new[] { region, roleFamily, seniority, skill }.Any(value => value is { Length: > 120 }))
        {
            return Results.BadRequest(new { error = "history filters must be 120 characters or fewer" });
        }

        var filter = new HistoryFilter(
            CleanHistoryFilter(region),
            CleanHistoryFilter(roleFamily),
            CleanHistoryFilter(seniority),
            CleanHistoryFilter(skill));
        return Results.Ok(await historyService.GetHistoryAsync(days ?? 7, filter, cancellationToken));
    })
    .WithName("GetAnalyticsHistory");

app.MapPost("/api/admin/crawl-runs", async (
        StartCrawlRunRequest request,
        ICrawlerOrchestrationService crawlerService,
        ICrawlRequestValidator crawlRequestValidator,
        CancellationToken cancellationToken) =>
    {
        // Round 15: the trigger is authenticated by the admin gate and the URL
        // is restricted to a registered source origin (SSRF guard).
        var validation = await crawlRequestValidator.ValidateAsync(request, cancellationToken);
        if (!validation.IsAllowed)
        {
            return Results.BadRequest(new { error = validation.Reason });
        }

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

// Round 13: protected by the shared HEALTH_ACCESS_PASSWORD secret.
app.MapHealthEndpoint();

// --- Round 21: additive snapshot capture / registry-event debug surface -----
app.MapGet("/api/admin/snapshots/capture-status", async (
        ISnapshotAuditService auditService,
        CancellationToken cancellationToken) =>
    Results.Ok(await auditService.GetCaptureStatusAsync(cancellationToken)))
    .WithName("GetSnapshotCaptureStatus");

// --- Round 21 (B2): password-gated classification review workflow ----------
app.MapGet("/api/admin/quality/classification-sample", async (
        int? size,
        IClassificationReviewService reviewService,
        CancellationToken cancellationToken) =>
    Results.Ok(await reviewService.GetSampleAsync(size ?? ClassificationReviewService.DefaultSampleSize, cancellationToken)))
    .WithName("GetClassificationSample");

app.MapGet("/api/admin/quality/classification-reviews", async (
        IClassificationReviewService reviewService,
        CancellationToken cancellationToken) =>
    Results.Ok(await reviewService.GetReviewsAsync(cancellationToken)))
    .WithName("GetClassificationReviews");

app.MapPost("/api/admin/quality/classification-labels", async (
        RecordClassificationLabelsRequest request,
        IClassificationReviewService reviewService,
        CancellationToken cancellationToken) =>
    {
        try
        {
            return Results.Ok(await reviewService.SaveLabelsAsync(request, cancellationToken));
        }
        catch (ArgumentException exception)
        {
            return Results.BadRequest(new { error = exception.Message });
        }
    })
    .WithName("SaveClassificationLabels");

// --- Round 9: per-field coverage for the active listing cohort -------------
app.MapGet("/api/admin/coverage", async (
        ICoverageService coverageService,
        CancellationToken cancellationToken) =>
    Results.Ok(await coverageService.GetCoverageAsync(cancellationToken)))
    .WithName("GetCoverage");

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

// --- Round 21 (B3): auditable unmatched-name review queue ------------------
app.MapGet("/api/admin/employers/review-queue", async (
        IEmployerReviewService reviewService,
        CancellationToken cancellationToken) =>
    Results.Ok(await reviewService.GetQueueAsync(cancellationToken)))
    .WithName("GetEmployerReviewQueue");

app.MapPost("/api/admin/employers/review-queue/decisions", async (
        RecordEmployerReviewDecisionsRequest request,
        IEmployerReviewService reviewService,
        CancellationToken cancellationToken) =>
    {
        try
        {
            return Results.Ok(await reviewService.SaveDecisionsAsync(request, cancellationToken));
        }
        catch (ArgumentException exception)
        {
            return Results.BadRequest(new { error = exception.Message });
        }
    })
    .WithName("SaveEmployerReviewDecisions");

// --- Round 8: listing deduplication provenance -----------------------------

app.MapGet("/api/admin/duplicates", async (
        int? page,
        int? pageSize,
        IListingDeduplicationService dedupService,
        CancellationToken cancellationToken) =>
    Results.Ok(await dedupService.GetGroupsAsync(page ?? 1, pageSize ?? 100, cancellationToken)))
    .WithName("GetDuplicateGroups");

app.MapPost("/api/admin/dedup/run", async (
        string? trigger,
        IListingDeduplicationService dedupService,
        CancellationToken cancellationToken) =>
    Results.Ok(await dedupService.RunAsync(trigger ?? "manual", cancellationToken)))
    .WithName("RunDeduplication");

// --- Round 11: salary parse audit ------------------------------------------

app.MapPost("/api/admin/salaries/audit", async (
        ISalaryAuditService salaryAuditService,
        CancellationToken cancellationToken) =>
    Results.Ok(await salaryAuditService.RunAsync(cancellationToken)))
    .WithName("RunSalaryAudit");

static string? CleanHistoryFilter(string? value) =>
    string.IsNullOrWhiteSpace(value) ? null : value.Trim();

app.Run();
