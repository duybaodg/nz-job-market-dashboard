using System.Text.RegularExpressions;
using Application.Common;
using Application.Deduplication;
using Application.Normalisation;
using Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Application.Crawling;

public interface ICrawlerOrchestrationService
{
    Task<CrawlRunResultDto> StartCrawlRunAsync(StartCrawlRunRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyList<CrawlRunDto>> GetCrawlRunsAsync(CancellationToken cancellationToken);
    Task<CrawlRunDto?> GetCrawlRunAsync(Guid id, CancellationToken cancellationToken);
}

public sealed class CrawlerOrchestrationService(
    IApplicationDbContext dbContext,
    IEnumerable<IJobSourceAdapter> adapters,
    IDeduplicationService deduplicationService,
    INormalisationService normalisationService)
    : ICrawlerOrchestrationService
{
    private static readonly Regex SalaryRegex = new(@"\$?\s*(\d{2,3})(?:,\d{3}|k)?\s*(?:-|to|–)\s*\$?\s*(\d{2,3})(?:,\d{3}|k)?", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public async Task<CrawlRunResultDto> StartCrawlRunAsync(StartCrawlRunRequest request, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out _))
        {
            return new CrawlRunResultDto(CreateTransientRun(request.Source, "Failed", "A valid absolute URL is required."), false, "A valid absolute URL is required.");
        }

        var source = string.IsNullOrWhiteSpace(request.Source) ? "Firecrawl" : request.Source.Trim();
        var adapter = adapters.FirstOrDefault(item => string.Equals(item.SourceName, source, StringComparison.OrdinalIgnoreCase));

        if (adapter is null)
        {
            var message = $"No source adapter is registered for '{source}'.";
            return new CrawlRunResultDto(CreateTransientRun(source, "Failed", message), false, message);
        }

        var crawlRun = new CrawlRun
        {
            Id = Guid.NewGuid(),
            Source = adapter.SourceName,
            Status = "Running",
            StartedAt = DateTime.UtcNow
        };

        dbContext.CrawlRuns.Add(crawlRun);
        await dbContext.SaveChangesAsync(cancellationToken);

        try
        {
            var result = await adapter.FetchJobsAsync(new JobSearchRequest(adapter.SourceName, request.Url), cancellationToken);

            dbContext.RawJobPages.Add(new RawJobPage
            {
                Id = Guid.NewGuid(),
                Source = result.RawPage.Source,
                Url = result.RawPage.Url,
                RawHtml = result.RawPage.RawHtml,
                Markdown = result.RawPage.Markdown,
                RawJson = result.RawPage.RawJson,
                CrawledAt = result.RawPage.CrawledAt,
                CrawlRunId = crawlRun.Id
            });

            var savedCount = 0;

            foreach (var rawJob in result.Jobs)
            {
                savedCount += await SaveJobAsync(rawJob, cancellationToken);
            }

            crawlRun.Status = "Completed";
            crawlRun.FinishedAt = DateTime.UtcNow;
            crawlRun.TotalPages = 1;
            crawlRun.TotalJobsFound = result.Jobs.Count;
            crawlRun.TotalJobsSaved = savedCount;
            await dbContext.SaveChangesAsync(cancellationToken);

            return new CrawlRunResultDto(ToDto(crawlRun), true, "Crawl run completed.");
        }
        catch (InvalidOperationException exception)
        {
            crawlRun.Status = "Failed";
            crawlRun.FinishedAt = DateTime.UtcNow;
            crawlRun.ErrorMessage = exception.Message;
            await dbContext.SaveChangesAsync(cancellationToken);

            return new CrawlRunResultDto(ToDto(crawlRun), false, exception.Message);
        }
    }

    public async Task<IReadOnlyList<CrawlRunDto>> GetCrawlRunsAsync(CancellationToken cancellationToken)
    {
        return await dbContext.CrawlRuns
            .AsNoTracking()
            .OrderByDescending(run => run.StartedAt)
            .Select(run => new CrawlRunDto(
                run.Id,
                run.Source,
                run.Status,
                run.StartedAt,
                run.FinishedAt,
                run.TotalPages,
                run.TotalJobsFound,
                run.TotalJobsSaved,
                run.ErrorMessage))
            .ToListAsync(cancellationToken);
    }

    public async Task<CrawlRunDto?> GetCrawlRunAsync(Guid id, CancellationToken cancellationToken)
    {
        return await dbContext.CrawlRuns
            .AsNoTracking()
            .Where(run => run.Id == id)
            .Select(run => new CrawlRunDto(
                run.Id,
                run.Source,
                run.Status,
                run.StartedAt,
                run.FinishedAt,
                run.TotalPages,
                run.TotalJobsFound,
                run.TotalJobsSaved,
                run.ErrorMessage))
            .SingleOrDefaultAsync(cancellationToken);
    }

    private async Task<int> SaveJobAsync(RawJobInput rawJob, CancellationToken cancellationToken)
    {
        var contentHash = deduplicationService.GenerateContentHash(rawJob);
        var existingJob = await dbContext.Jobs
            .Include(job => job.Skills)
            .FirstOrDefaultAsync(job =>
                job.ContentHash == contentHash ||
                (rawJob.SourceJobId != null && job.Source == rawJob.Source && job.SourceJobId == rawJob.SourceJobId) ||
                job.Url == rawJob.Url,
                cancellationToken);

        if (existingJob is not null)
        {
            existingJob.LastSeenAt = DateTime.UtcNow;
            existingJob.UpdatedAt = DateTime.UtcNow;
            return 0;
        }

        var (salaryMin, salaryMax) = ParseSalary(rawJob.SalaryText);
        var now = DateTime.UtcNow;
        var job = new Job
        {
            Id = Guid.NewGuid(),
            Source = rawJob.Source,
            SourceJobId = rawJob.SourceJobId,
            Title = rawJob.Title.Trim(),
            Company = CleanOptional(rawJob.Company),
            Location = CleanOptional(rawJob.Location),
            Region = normalisationService.StandardiseRegion(rawJob.Location),
            SalaryMin = salaryMin,
            SalaryMax = salaryMax,
            EmploymentType = InferEmploymentType(rawJob.DescriptionText),
            Seniority = normalisationService.StandardiseSeniority(rawJob.Title),
            WorkMode = InferWorkMode(rawJob.DescriptionText),
            Industry = "Unknown",
            DescriptionSummary = Summarise(rawJob.DescriptionText),
            Url = rawJob.Url,
            ContentHash = contentHash,
            PostedDate = rawJob.PostedDate,
            ClosingDate = rawJob.ClosingDate,
            FirstSeenAt = now,
            LastSeenAt = now,
            CreatedAt = now,
            UpdatedAt = now
        };

        foreach (var skill in ExtractSkills($"{rawJob.Title} {rawJob.DescriptionText}"))
        {
            job.Skills.Add(new JobSkill
            {
                Id = Guid.NewGuid(),
                SkillName = skill,
                SkillType = "Keyword",
                Confidence = 0.7m
            });
        }

        dbContext.Jobs.Add(job);
        return 1;
    }

    private static CrawlRunDto CreateTransientRun(string source, string status, string errorMessage)
    {
        return new CrawlRunDto(Guid.Empty, source, status, DateTime.UtcNow, DateTime.UtcNow, 0, 0, 0, errorMessage);
    }

    private static CrawlRunDto ToDto(CrawlRun run)
    {
        return new CrawlRunDto(run.Id, run.Source, run.Status, run.StartedAt, run.FinishedAt, run.TotalPages, run.TotalJobsFound, run.TotalJobsSaved, run.ErrorMessage);
    }

    private static string? CleanOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static (decimal? Min, decimal? Max) ParseSalary(string? salaryText)
    {
        if (string.IsNullOrWhiteSpace(salaryText))
        {
            return (null, null);
        }

        var match = SalaryRegex.Match(salaryText);
        if (!match.Success)
        {
            return (null, null);
        }

        return (ToSalary(match.Groups[1].Value), ToSalary(match.Groups[2].Value));
    }

    private static decimal ToSalary(string value)
    {
        var number = decimal.Parse(value);
        return number < 1000 ? number * 1000 : number;
    }

    private static string InferEmploymentType(string? text)
    {
        var value = text?.ToLowerInvariant() ?? string.Empty;

        if (value.Contains("part-time")) return "Part-time";
        if (value.Contains("contract")) return "Contract";
        if (value.Contains("internship")) return "Internship";
        if (value.Contains("full-time")) return "Full-time";

        return "Unknown";
    }

    private static string InferWorkMode(string? text)
    {
        var value = text?.ToLowerInvariant() ?? string.Empty;

        if (value.Contains("remote")) return "Remote";
        if (value.Contains("hybrid")) return "Hybrid";
        if (value.Contains("on-site") || value.Contains("onsite")) return "On-site";

        return "Unknown";
    }

    private static string? Summarise(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var clean = Regex.Replace(text.Trim(), @"\s+", " ");
        return clean.Length <= 240 ? clean : $"{clean[..237]}...";
    }

    private static IReadOnlyList<string> ExtractSkills(string text)
    {
        var knownSkills = new[] { ".NET", "C#", "React", "TypeScript", "JavaScript", "SQL", "PostgreSQL", "Azure", "AWS", "Power BI", "Python", "Terraform" };
        return knownSkills
            .Where(skill => text.Contains(skill, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
