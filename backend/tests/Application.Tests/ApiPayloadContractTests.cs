using System.Text.Json;
using Application.Analytics;
using Application.Health;
using Application.History;
using Application.Jobs;
using Domain.Entities;
using FluentAssertions;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Application.Tests;

/// <summary>
/// Round 14 payload contracts. Each widget group reads a fixed set of JSON
/// fields; these tests build the real DTOs through the production services over
/// the live EF model and serialise them with the same web defaults the minimal
/// API uses, then assert every field the frontend reads is present (and, where
/// the data guarantees it, non-null). A rename or removal therefore fails CI
/// here instead of silently blanking a dashboard widget.
/// </summary>
public sealed class ApiPayloadContractTests
{
    // 2026-10-15T00:30Z is 2026-10-15 13:30 NZDT.
    private static readonly DateTime Now = new(2026, 10, 15, 0, 30, 0, DateTimeKind.Utc);

    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private static JsonElement Json(object dto) => JsonSerializer.SerializeToElement(dto, WebJson);

    // --- Find jobs cards -----------------------------------------------------

    [Fact]
    public async Task Job_card_payload_exposes_every_field_the_feed_reads()
    {
        await using var db = CreateDbContext();
        var seed = await SeedAsync(db);

        var jobs = await new JobService(db).GetJobsAsync(
            new JobListQuery(null, null, null, "all", null),
            CancellationToken.None);

        jobs.Should().NotBeEmpty();
        var job = Json(jobs.Single(entry => entry.Id == seed.JobAId));

        // Fields the card, filters, salary/work-mode/date helpers and CSV read.
        Require(job,
            "id", "source", "title", "company", "location", "region",
            "salaryMin", "salaryMax", "salaryText", "salaryCurrency", "salaryPeriod",
            "salaryQuestionable", "salaryQuestionableReason",
            "employmentType", "seniority", "workMode", "experienceBand", "industry",
            "descriptionSummary", "url", "postedDate", "closingDate", "firstSeenAt",
            "lastSeenAt", "isActive", "expiredAt", "skills", "programmingLanguages",
            "sourceCategory", "advertiser", "advertiserType", "hiringEmployer",
            "employerDisclosure", "roleFamily", "roleFamilyInferred", "duplicateGroupId",
            "duplicateCount", "alsoOnSources", "isCanonicalListing",
            "employerCanonicalId", "employerCanonicalName");

        // Values a rendered card depends on must not silently become null.
        RequireNonNull(job,
            "id", "source", "title", "salaryCurrency", "salaryPeriod",
            "employmentType", "seniority", "workMode", "experienceBand", "url",
            "firstSeenAt", "lastSeenAt", "isActive", "skills", "programmingLanguages",
            "sourceCategory", "advertiser", "advertiserType", "employerDisclosure",
            "roleFamily", "roleFamilyInferred", "duplicateCount", "alsoOnSources",
            "isCanonicalListing", "employerCanonicalId", "employerCanonicalName");

        // The salary mid-point the salary widgets consume is present when advertised.
        RequireNonNull(job, "salaryMin", "salaryMax", "salaryText");
    }

    // --- Overview stat cards and freshness pill ------------------------------

    [Fact]
    public async Task Overview_stat_cards_and_freshness_pill_payload_is_complete()
    {
        await using var db = CreateDbContext();
        await SeedAsync(db);

        var overview = Json(await new AnalyticsService(db, new FixedTimeProvider(Now)).GetOverviewAsync(CancellationToken.None));

        Require(overview,
            "generatedAtUtc", "activeListings", "postedDateKnown", "postedDateMissing",
            "newlyDiscovered", "newlyPosted",
            "salary", "employers", "dataStatus", "mostActiveRegion", "mostRequestedSkill",
            "interval", "uniqueVacancies", "deduplication", "discoveryBasis");

        RequireNonNull(overview,
            "generatedAtUtc", "activeListings", "postedDateKnown", "postedDateMissing",
            "mostActiveRegion", "mostRequestedSkill", "discoveryBasis");

        Require(overview,
            "newlyDiscovered.current", "newlyDiscovered.previous", "newlyDiscovered.absoluteChange",
            "newlyDiscovered.percentChange", "newlyDiscovered.comparisonAvailable",
            "newlyDiscovered.noPriorBaseline", "newlyDiscovered.note",
            "newlyPosted.current", "newlyPosted.previous", "newlyPosted.absoluteChange",
            "newlyPosted.percentChange", "newlyPosted.comparisonAvailable",
            "newlyPosted.noPriorBaseline", "newlyPosted.note");
        RequireNonNull(overview, "newlyDiscovered.current", "newlyPosted.current");

        Require(overview,
            "salary.medianAnnualNzd", "salary.eligibleCount", "salary.cohortCount",
            "salary.coveragePercent", "salary.questionableCount", "salary.suppressed",
            "salary.smallSample", "salary.deduplicated", "salary.exclusions",
            "salary.currency", "salary.payPeriod", "salary.basis");
        RequireNonNull(overview,
            "salary.medianAnnualNzd", "salary.eligibleCount", "salary.cohortCount",
            "salary.coveragePercent", "salary.currency", "salary.payPeriod", "salary.basis");
        Require(overview, "salary.exclusions.missing", "salary.exclusions.non-annual",
            "salary.exclusions.non-nzd", "salary.exclusions.contract-or-internship");

        Require(overview,
            "employers.identifiableEmployers", "employers.listingsWithEmployer",
            "employers.listingsWithoutEmployer", "employers.coveragePercent",
            "employers.agencyOnlyListings", "employers.directEmployerListings",
            "employers.agencyAdvertisedListings", "employers.boardAdvertisedListings",
            "employers.canonicalEmployers", "employers.agencyCanonicals",
            "employers.directEmployerCanonicals", "employers.unmatchedEmployerNames");
        RequireNonNull(overview,
            "employers.identifiableEmployers", "employers.listingsWithEmployer",
            "employers.listingsWithoutEmployer", "employers.coveragePercent",
            "employers.canonicalEmployers");

        // Freshness pill + banner.
        Require(overview,
            "dataStatus.state", "dataStatus.lastSuccessfulUpdateUtc",
            "dataStatus.newestListingSeenUtc", "dataStatus.enabledSources",
            "dataStatus.enabledSourcesWithRecentSuccess", "dataStatus.message");
        RequireNonNull(overview,
            "dataStatus.state", "dataStatus.lastSuccessfulUpdateUtc",
            "dataStatus.newestListingSeenUtc", "dataStatus.enabledSources",
            "dataStatus.enabledSourcesWithRecentSuccess", "dataStatus.message");

        Require(overview,
            "interval.startUtc", "interval.endUtc", "interval.startLocalDate",
            "interval.endLocalDateExclusive", "interval.days", "interval.label",
            "interval.isPartial");
    }

    // --- Trends charts: retained history -------------------------------------

    [Fact]
    public async Task History_charts_payload_is_complete_for_seven_fifteen_and_thirty_days()
    {
        await using var db = CreateDbContext();
        await SeedAsync(db);

        foreach (var days in new[] { 7, 15, 30 })
        {
            var history = Json(await new HistoryService(db, new FixedTimeProvider(Now)).GetHistoryAsync(days, CancellationToken.None));

            Require(history,
                "generatedAtUtc", "coverage", "latestActiveCount", "latestSnapshotDate",
                "activeChange", "comparableFromDate", "newListings", "weeklyNewListings",
                "activeTrend", "coverageChanges", "note");
            RequireNonNull(history, "generatedAtUtc", "latestActiveCount", "latestSnapshotDate");

            Require(history,
                "coverage.firstDate", "coverage.lastDate", "coverage.snapshotDays",
                "coverage.expectedDays", "coverage.missingDates", "coverage.hasHistory",
                "coverage.note");
            RequireNonNull(history,
                "coverage.firstDate", "coverage.lastDate", "coverage.snapshotDays",
                "coverage.expectedDays", "coverage.hasHistory", "coverage.note");

            Require(history,
                "activeChange.current", "activeChange.previous", "activeChange.absoluteChange",
                "activeChange.percentChange", "activeChange.comparisonAvailable",
                "activeChange.noPriorBaseline", "activeChange.note");

            Require(history,
                "newListings.days", "newListings.interval", "newListings.change",
                "newListings.currentPeriodCovered", "newListings.dateBasis", "newListings.note");
            RequireNonNull(history, "newListings.days", "newListings.dateBasis");
            Require(history,
                "newListings.change.current", "newListings.change.previous",
                "newListings.change.absoluteChange", "newListings.change.percentChange",
                "newListings.change.comparisonAvailable", "newListings.change.noPriorBaseline",
                "newListings.change.note",
                "newListings.interval.startLocalDate", "newListings.interval.endLocalDateExclusive",
                "newListings.interval.label");

            Require(history,
                "weeklyNewListings[0].weekStartLocal", "weeklyNewListings[0].weekEndLocalExclusive",
                "weeklyNewListings[0].count", "weeklyNewListings[0].isPartial",
                "weeklyNewListings[0].coverageStartDate", "weeklyNewListings[0].note");
            RequireNonNull(history,
                "weeklyNewListings[0].weekStartLocal", "weeklyNewListings[0].weekEndLocalExclusive",
                "weeklyNewListings[0].count", "weeklyNewListings[0].isPartial",
                "weeklyNewListings[0].coverageStartDate");

            Require(history,
                "activeTrend[0].snapshotDate", "activeTrend[0].activeCount",
                "activeTrend[0].sourceCount", "activeTrend[0].coverageChanged",
                "activeTrend[0].sourcesAdded", "activeTrend[0].sourcesRemoved");
            RequireNonNull(history,
                "activeTrend[0].snapshotDate", "activeTrend[0].activeCount",
                "activeTrend[0].sourceCount", "activeTrend[0].coverageChanged");

            Require(history,
                "coverageChanges[0].snapshotDate", "coverageChanges[0].sourcesAdded",
                "coverageChanges[0].sourcesRemoved");
            RequireNonNull(history, "coverageChanges[0].snapshotDate");
        }
    }

    // --- Trends charts: top skills and regions -------------------------------

    [Fact]
    public async Task Skill_and_region_chart_payloads_are_complete()
    {
        await using var db = CreateDbContext();
        await SeedAsync(db);

        var analytics = new AnalyticsService(db, new FixedTimeProvider(Now));
        var skills = Json(await analytics.GetTopSkillsAsync(CancellationToken.None));
        var regions = Json(await analytics.GetJobsByRegionAsync(CancellationToken.None));

        skills.GetArrayLength().Should().BeGreaterThan(0);
        regions.GetArrayLength().Should().BeGreaterThan(0);
        RequireNonNull(skills, "[0].skillName", "[0].jobCount");
        RequireNonNull(regions, "[0].region", "[0].jobCount");
    }

    // --- Freshness panel (2/7/15-day windows) --------------------------------

    [Fact]
    public async Task Freshness_window_payload_is_complete_for_every_window()
    {
        await using var db = CreateDbContext();
        await SeedAsync(db);

        var freshness = Json(await new AnalyticsService(db, new FixedTimeProvider(Now)).GetFreshnessAsync(CancellationToken.None));

        Require(freshness, "totalActiveJobs", "generatedAtUtc", "dataStatus", "windows", "uniqueVacancies", "deduplication", "discoveryBasis");
        RequireNonNull(freshness, "totalActiveJobs", "generatedAtUtc", "discoveryBasis");
        Require(freshness,
            "dataStatus.state", "dataStatus.lastSuccessfulUpdateUtc",
            "dataStatus.newestListingSeenUtc", "dataStatus.enabledSources",
            "dataStatus.enabledSourcesWithRecentSuccess", "dataStatus.message");

        freshness.GetProperty("windows").GetArrayLength().Should().Be(3, "the dashboard exposes 2, 7 and 15 day windows");

        foreach (var window in freshness.GetProperty("windows").EnumerateArray())
        {
            Require(window,
                "days", "interval", "newlyPosted", "previousNewlyPosted", "newlyDiscovered",
                "previousNewlyDiscovered", "postedDateKnown", "postedDateMissing",
                "comparisonAvailable");
            RequireNonNull(window,
                "days", "newlyPosted", "previousNewlyPosted", "newlyDiscovered",
                "previousNewlyDiscovered", "postedDateKnown", "postedDateMissing",
                "comparisonAvailable");
            Require(window,
                "interval.startUtc", "interval.endUtc", "interval.startLocalDate",
                "interval.endLocalDateExclusive", "interval.days", "interval.label",
                "interval.isPartial");
        }
    }

    // --- Crawler health page -------------------------------------------------

    [Fact]
    public async Task Health_page_payload_is_complete()
    {
        await using var db = CreateDbContext();
        await SeedAsync(db);

        var health = Json(await new HealthService(db, new HealthReportCache())
            .ComputeAsync(Now, CancellationToken.None));

        Require(health, "generatedAt", "summary", "sources", "retiredSources");
        Require(health,
            "summary.healthy", "summary.degraded", "summary.stale", "summary.empty",
            "summary.disabled", "summary.neverRun", "summary.retired", "summary.totalSources");
        RequireNonNull(health,
            "summary.healthy", "summary.degraded", "summary.stale", "summary.empty",
            "summary.disabled", "summary.neverRun", "summary.retired", "summary.totalSources");

        Require(health,
            "sources[0].source", "sources[0].enabled", "sources[0].parser", "sources[0].method",
            "sources[0].lastCrawledAt", "sources[0].lastStatus", "sources[0].lastError",
            "sources[0].consecutiveFailures", "sources[0].consecutiveZeroYield",
            "sources[0].jobsYielded", "sources[0].state", "sources[0].degradeReason",
            "sources[0].crawlFrequencyMinutes");
        RequireNonNull(health,
            "sources[0].source", "sources[0].enabled", "sources[0].parser", "sources[0].method",
            "sources[0].consecutiveFailures", "sources[0].consecutiveZeroYield",
            "sources[0].jobsYielded", "sources[0].state", "sources[0].crawlFrequencyMinutes");

        Require(health,
            "retiredSources[0].source", "retiredSources[0].state");
        RequireNonNull(health, "retiredSources[0].source", "retiredSources[0].state");
    }

    // -------------------------------------------------------------------------

    private static void Require(JsonElement root, params string[] paths)
    {
        foreach (var path in paths)
        {
            var _ = Navigate(root, path);
        }
    }

    private static void RequireNonNull(JsonElement root, params string[] paths)
    {
        foreach (var path in paths)
        {
            var element = Navigate(root, path);
            element.ValueKind.Should().NotBe(JsonValueKind.Null, $"contract path '{path}' must not be null");
        }
    }

    private static JsonElement Navigate(JsonElement root, string path)
    {
        var current = root;

        foreach (var rawSegment in path.Split('.'))
        {
            var segment = rawSegment;
            string? index = null;
            var bracket = segment.IndexOf('[');
            if (bracket >= 0)
            {
                index = segment[(bracket + 1)..segment.IndexOf(']')];
                segment = segment[..bracket];
            }

            if (segment.Length > 0)
            {
                if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
                {
                    throw new InvalidOperationException($"contract path '{path}' is missing property '{segment}'");
                }
            }

            if (index is not null)
            {
                var position = int.Parse(index);
                if (current.ValueKind != JsonValueKind.Array || current.GetArrayLength() <= position)
                {
                    throw new InvalidOperationException($"contract path '{path}' is missing index [{position}]");
                }

                current = current[position];
            }
        }

        return current;
    }

    private static async Task<Seed> SeedAsync(ApplicationDbContext db)
    {
        var acmeId = Guid.NewGuid();
        var jobAId = Guid.NewGuid();
        var jobBId = Guid.NewGuid();

        db.JobSources.AddRange(
            new JobSource { Id = Guid.NewGuid(), Name = "Acme", Method = "scraping", Parser = "jsonld", Category = "company", BaseUrl = "https://acme.example", Enabled = true, LastCrawledAt = Now.AddHours(-1), CreatedAt = Now, UpdatedAt = Now },
            new JobSource { Id = Guid.NewGuid(), Name = "Beta", Method = "scraping", Parser = "generic", Category = "recruiter", BaseUrl = "https://beta.example", Enabled = true, LastCrawledAt = Now.AddHours(-2), CreatedAt = Now, UpdatedAt = Now },
            new JobSource { Id = Guid.NewGuid(), Name = "Retired Co", Method = "scraping", Parser = "generic", Category = "company", BaseUrl = "https://retired.example", Enabled = false, RetiredAt = Now.AddDays(-1), CreatedAt = Now, UpdatedAt = Now });

        db.Employers.Add(new Employer
        {
            Id = acmeId,
            CanonicalName = "Acme Corp",
            NormalizedName = "acme corp",
            Type = EmployerType.DirectEmployer,
            CreatedAt = Now,
        });

        var jobA = SeedJob(jobAId, "Acme", "Auckland", 130_000m, 150_000m, "Year", "Full-time", new DateTime(2026, 10, 13, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 13, 0, 0, 0, DateTimeKind.Utc), "Acme Corp", acmeId);
        var jobB = SeedJob(jobBId, "Beta", "Auckland", 150_000m, 170_000m, "Year", "Permanent", new DateTime(2026, 10, 10, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 10, 0, 0, 0, DateTimeKind.Utc), "Beyond", null);

        db.Jobs.AddRange(
            jobA,
            jobB,
            SeedJob(Guid.NewGuid(), "Acme", "Wellington", 100_000m, 120_000m, "Year", "Full-time", null, new DateTime(2026, 10, 13, 0, 0, 0, DateTimeKind.Utc), "Tribe", null),
            SeedJob(Guid.NewGuid(), "Acme", "New Zealand", 130_000m, 150_000m, "Year", "Full-time", new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc), "Seek", null),
            SeedJob(Guid.NewGuid(), "Beta", "Canterbury", 90_000m, 110_000m, "Year", "Full-time", new DateTime(2026, 10, 14, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 14, 0, 0, 0, DateTimeKind.Utc), null, null),
            SeedJob(Guid.NewGuid(), "Acme", null, 85m, null, "Hour", "Contract", new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc), "Acme Corp", null, active: true),
            SeedJob(Guid.NewGuid(), "Acme", null, null, null, "Unknown", "Unknown", new DateTime(2026, 10, 12, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 12, 0, 0, 0, DateTimeKind.Utc), null, null, active: false));

        db.JobSkills.AddRange(
            new JobSkill { Id = Guid.NewGuid(), JobId = jobAId, Job = jobA, SkillName = "Python", SkillType = "ProgrammingLanguage" },
            new JobSkill { Id = Guid.NewGuid(), JobId = jobAId, Job = jobA, SkillName = "SQL", SkillType = "ProgrammingLanguage" },
            new JobSkill { Id = Guid.NewGuid(), JobId = jobBId, Job = jobB, SkillName = "Python", SkillType = "ProgrammingLanguage" },
            new JobSkill { Id = Guid.NewGuid(), JobId = jobBId, Job = jobB, SkillName = "AWS", SkillType = "Cloud" });

        db.CrawlRuns.Add(new CrawlRun
        {
            Id = Guid.NewGuid(),
            Source = "Acme",
            Status = "Completed",
            StartedAt = Now.AddHours(-1),
            FinishedAt = Now.AddHours(-1),
            TotalPages = 1,
            TotalJobsFound = 3,
            TotalJobsSaved = 3,
        });

        // Two snapshots so the active trend has a point and the coverage change
        // annotation is exercised; Beta is added on the later day.
        db.ListingSnapshots.AddRange(
            Snapshot(new DateOnly(2026, 10, 8), jobAId, "Acme", "h-a", new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), active: true),
            Snapshot(new DateOnly(2026, 10, 15), jobAId, "Acme", "h-a", new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), active: true),
            Snapshot(new DateOnly(2026, 10, 15), jobBId, "Beta", "h-b", new DateTime(2026, 10, 9, 0, 0, 0, DateTimeKind.Utc), active: true));

        await db.SaveChangesAsync();
        return new Seed(jobAId, jobBId);
    }

    private static Job SeedJob(
        Guid id,
        string source,
        string? region,
        decimal? salaryMin,
        decimal? salaryMax,
        string salaryPeriod,
        string employmentType,
        DateTime? posted,
        DateTime firstSeen,
        string? company,
        Guid? employerCanonicalId,
        bool active = true) => new()
        {
            Id = id,
            Source = source,
            Title = "Software Engineer",
            Company = company,
            Location = region,
            Region = region,
            Url = $"https://{source.ToLowerInvariant()}.example/jobs/{id}",
            ContentHash = id.ToString(),
            IsActive = active,
            PostedDate = posted,
            FirstSeenAt = firstSeen,
            LastSeenAt = Now,
            CreatedAt = firstSeen,
            UpdatedAt = Now,
            SalaryMin = salaryMin,
            SalaryMax = salaryMax,
            SalaryText = salaryMin is null ? null : $"${salaryMin:#,0} - ${salaryMax:#,0}",
            SalaryCurrency = "NZD",
            SalaryPeriod = salaryPeriod,
            EmploymentType = employmentType,
            Seniority = "Senior",
            WorkMode = "Hybrid",
            ExperienceBand = "2-5 years",
            DescriptionSummary = "Build things.",
            EmployerCanonicalId = employerCanonicalId,
        };

    private static ListingSnapshot Snapshot(
        DateOnly date,
        Guid jobId,
        string source,
        string hash,
        DateTime firstSeen,
        bool active) => new()
        {
            Id = Guid.NewGuid(),
            SnapshotDate = date,
            JobId = jobId,
            Source = source,
            SourceCategory = "company",
            ContentHash = hash,
            Title = "Software Engineer",
            Region = "Auckland",
            IsActive = active,
            FirstSeenAt = firstSeen,
            LastSeenAt = Now,
            CapturedAtUtc = Now,
        };

    private static ApplicationDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        var dbContext = new ApplicationDbContext(options);
        dbContext.Database.EnsureCreated();
        return dbContext;
    }

    private sealed record Seed(Guid JobAId, Guid JobBId);

    private sealed class FixedTimeProvider(DateTime nowUtc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc));
    }
}
