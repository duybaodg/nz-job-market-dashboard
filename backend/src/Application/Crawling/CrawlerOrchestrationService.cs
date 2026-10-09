using System.Text.RegularExpressions;
using System.Runtime.ExceptionServices;
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
    private static readonly Regex ExperienceRegex = new(
        @"(?:(?:at least|minimum(?: of)?|over|more than)\s+)?(\d{1,2})(?:\s*\+)?(?:\s*(?:-|to|–)\s*\d{1,2})?\s+years?"
        + @"|"
        + @"experience(?:\s+of|\s+in|\s+with)?\s+(?:at\s+least\s+)?(\d{1,2})(?:\s*\+)?\s+years?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly HashSet<string> ProgrammingLanguages = new(StringComparer.OrdinalIgnoreCase)
    {
        "C#", "C++", "Go", "Java", "JavaScript", "Kotlin", "PHP", "Python", "Ruby", "Rust", "Scala", "Swift", "TypeScript"
    };

    public async Task<CrawlRunResultDto> StartCrawlRunAsync(StartCrawlRunRequest request, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out _))
        {
            return new CrawlRunResultDto(CreateTransientRun(request.Source, "Failed", "A valid absolute URL is required."), false, "A valid absolute URL is required.");
        }

        var requestedSource = string.IsNullOrWhiteSpace(request.Source) ? "Firecrawl" : request.Source.Trim();
        var sourceConfig = await dbContext.JobSources
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Name.ToLower() == requestedSource.ToLower(), cancellationToken);
        var source = sourceConfig?.Name ?? requestedSource;
        var method = sourceConfig?.Method ?? requestedSource;
        var parser = sourceConfig?.Parser ?? "generic";
        var adapter = adapters.FirstOrDefault(item => string.Equals(item.Method, method, StringComparison.OrdinalIgnoreCase));

        if (adapter is null)
        {
            var message = $"No crawl adapter is registered for method '{method}'.";
            return new CrawlRunResultDto(CreateTransientRun(source, "Failed", message), false, message);
        }

        var crawlRun = new CrawlRun
        {
            Id = Guid.NewGuid(),
            Source = source,
            Status = "Running",
            StartedAt = DateTime.UtcNow
        };

        dbContext.CrawlRuns.Add(crawlRun);
        await dbContext.SaveChangesAsync(cancellationToken);

        try
        {
            var result = await adapter.FetchJobsAsync(new JobSearchRequest(source, request.Url, parser), cancellationToken);

            foreach (var rawPage in result.RawPages)
            {
                dbContext.RawJobPages.Add(new RawJobPage
                {
                    Id = Guid.NewGuid(),
                    Source = rawPage.Source,
                    Url = rawPage.Url,
                    RawHtml = rawPage.RawHtml,
                    Markdown = rawPage.Markdown,
                    RawJson = rawPage.RawJson,
                    CrawledAt = rawPage.CrawledAt,
                    CrawlRunId = crawlRun.Id
                });
            }

            var savedCount = 0;

            foreach (var rawJob in result.Jobs)
            {
                savedCount += await SaveJobAsync(rawJob, cancellationToken);
            }

            await ReconcileJobsAsync(source, result, cancellationToken);

            crawlRun.Status = "Completed";
            crawlRun.FinishedAt = DateTime.UtcNow;
            crawlRun.TotalPages = result.RawPages.Count;
            crawlRun.TotalJobsFound = result.Jobs.Count;
            crawlRun.TotalJobsSaved = savedCount;
            await dbContext.SaveChangesAsync(cancellationToken);

            return new CrawlRunResultDto(ToDto(crawlRun), true, "Crawl run completed.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            try
            {
                crawlRun.Status = "Failed";
                crawlRun.FinishedAt = DateTime.UtcNow;
                crawlRun.ErrorMessage = exception.Message;
                await dbContext.SaveChangesAsync(cancellationToken);
            }
            catch
            {
                ExceptionDispatchInfo.Capture(exception).Throw();
            }

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
            existingJob.ClosingDate = rawJob.ClosingDate ?? existingJob.ClosingDate;
            existingJob.Company = CleanOptional(rawJob.Company) ?? existingJob.Company;
            existingJob.Location = CleanOptional(rawJob.Location) ?? existingJob.Location;
            existingJob.Region = normalisationService.StandardiseRegion(existingJob.Location);
            existingJob.EmploymentType = InferEmploymentType(rawJob.DescriptionText);
            existingJob.WorkMode = InferWorkMode(rawJob.DescriptionText);
            existingJob.ExperienceBand = InferExperienceBand(rawJob.DescriptionText);
            existingJob.DescriptionSummary = Summarise(rawJob.DescriptionText);
            SetSkills(existingJob, rawJob);
            existingJob.IsActive = true;
            existingJob.ExpiredAt = null;
            existingJob.MissingCrawlCount = 0;
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
            ExperienceBand = InferExperienceBand(rawJob.DescriptionText),
            Industry = "Unknown",
            DescriptionSummary = Summarise(rawJob.DescriptionText),
            Url = rawJob.Url,
            ContentHash = contentHash,
            PostedDate = rawJob.PostedDate,
            ClosingDate = rawJob.ClosingDate,
            IsActive = true,
            FirstSeenAt = now,
            LastSeenAt = now,
            CreatedAt = now,
            UpdatedAt = now
        };

        SetSkills(job, rawJob);

        dbContext.Jobs.Add(job);
        return 1;
    }

    private async Task ReconcileJobsAsync(
        string source,
        SourceFetchResult result,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var seenUrls = result.Jobs.Select(job => job.Url).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var activeJobs = await dbContext.Jobs
            .Where(job => job.Source == source && job.IsActive)
            .ToListAsync(cancellationToken);

        foreach (var job in activeJobs)
        {
            if (job.ClosingDate?.Date < now.Date)
            {
                Expire(job, now);
            }
            else if (result.IsComplete && !seenUrls.Contains(job.Url))
            {
                job.MissingCrawlCount++;
                job.UpdatedAt = now;
                if (job.MissingCrawlCount >= 2) Expire(job, now);
            }
        }
    }

    private static void Expire(Job job, DateTime now)
    {
        job.IsActive = false;
        job.ExpiredAt = now;
        job.UpdatedAt = now;
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

        var min = ToSalary(match.Groups[1].Value);
        var max = ToSalary(match.Groups[2].Value);

        // Swap if min > max (parser got order wrong)
        if (min > max)
        {
            (min, max) = (max, min);
        }

        // Sanity bounds: reject implausible NZ IT salaries
        const decimal maxPlausible = 500_000; // No NZ IT role pays >$500k annually
        const decimal minPlausible = 30_000;  // Below $30k is likely a misparse (daily rate, contract months, etc.)

        if (min > maxPlausible || max > maxPlausible || min > maxPlausible)
        {
            return (null, null);
        }

        if (min < minPlausible || max < minPlausible)
        {
            return (null, null);
        }

        return (min, max);
    }

    private static decimal ToSalary(string value)
    {
        var number = decimal.Parse(value);
        // Numbers < 1000 are typically in "k" notation (e.g., 130 = $130k)
        return number < 1000 ? number * 1000 : number;
    }

    private static string InferEmploymentType(string? text)
    {
        var value = text?.ToLowerInvariant() ?? string.Empty;
        var labelled = Regex.Match(value, @"employment type:\s*(part-time|full-time|contract|internship)");

        if (labelled.Success) return labelled.Groups[1].Value.ToLowerInvariant() switch
        {
            "part-time" => "Part-time",
            "full-time" => "Full-time",
            "contract" => "Contract",
            _ => "Internship"
        };

        if (value.Contains("part-time")) return "Part-time";
        if (value.Contains("contract")) return "Contract";
        if (value.Contains("internship")) return "Internship";
        if (value.Contains("full-time")) return "Full-time";

        return "Unknown";
    }

    private static string InferWorkMode(string? text)
    {
        var value = text?.ToLowerInvariant() ?? string.Empty;
        var labelled = Regex.Match(value, @"work mode:\s*(remote|hybrid|on-site|onsite)");

        if (labelled.Success) return labelled.Groups[1].Value.ToLowerInvariant() switch
        {
            "remote" => "Remote",
            "hybrid" => "Hybrid",
            _ => "On-site"
        };

        if (value.Contains("remote")) return "Remote";
        if (value.Contains("hybrid")) return "Hybrid";
        if (value.Contains("on-site") || value.Contains("onsite")) return "On-site";

        return "Unknown";
    }

    private static string InferExperienceBand(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "Unknown";

        var years = ExperienceRegex.Matches(text)
            .Select(match => int.Parse(match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value))
            .DefaultIfEmpty(-1)
            .Max();

        if (years < 0) return "Unknown";
        if (years <= 2) return "0-2 years";
        if (years <= 5) return "2-5 years";
        return "More than 5 years";
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
        var knownSkills = new[] { ".NET", "C#", "C++", "React", "TypeScript", "JavaScript", "Java", "Go", "Rust", "Kotlin", "Swift", "PHP", "Ruby", "Scala", "SQL", "PostgreSQL", "Azure", "AWS", "Power BI", "Python", "Terraform" };
        return knownSkills
            .Where(skill => skill == "Go"
                ? Regex.IsMatch(text, @"(?<![\w])(?:Go|Golang)(?![\w])")
                : Regex.IsMatch(text, $@"(?<![\w+#]){Regex.Escape(skill)}(?![\w+#])", RegexOptions.IgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void SetSkills(Job job, RawJobInput rawJob)
    {
        var desired = ExtractSkills($"{rawJob.Title} {rawJob.DescriptionText}").ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var existing in job.Skills.ToList())
        {
            if (!desired.Remove(existing.SkillName))
            {
                continue;
            }

            existing.SkillType = ProgrammingLanguages.Contains(existing.SkillName) ? "ProgrammingLanguage" : "Keyword";
        }

        foreach (var skill in desired)
        {
            var jobSkill = new JobSkill
            {
                Id = Guid.NewGuid(),
                JobId = job.Id,
                SkillName = skill,
                SkillType = ProgrammingLanguages.Contains(skill) ? "ProgrammingLanguage" : "Keyword",
                Confidence = 0.7m
            };
            job.Skills.Add(jobSkill);
            dbContext.JobSkills.Add(jobSkill);
        }
    }
}
