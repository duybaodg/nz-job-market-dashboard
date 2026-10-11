using System.Text.RegularExpressions;
using System.Runtime.ExceptionServices;
using Application.Common;
using Application.Deduplication;
using Application.Jobs;
using Application.Normalisation;
using Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Application.Crawling;

public interface ICrawlerOrchestrationService
{
    Task<CrawlRunResultDto> StartCrawlRunAsync(StartCrawlRunRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyList<CrawlRunDto>> GetCrawlRunsAsync(CancellationToken cancellationToken);
    Task<CrawlRunDto?> GetCrawlRunAsync(Guid id, CancellationToken cancellationToken);
    Task<IReadOnlyList<JobSourceDto>> GetSourcesAsync(CancellationToken cancellationToken);
}

public sealed class CrawlerOrchestrationService(
    IApplicationDbContext dbContext,
    IEnumerable<IJobSourceAdapter> adapters,
    IDeduplicationService deduplicationService,
    INormalisationService normalisationService,
    CrawlerLifecycleOptions? lifecycleOptions = null,
    IJobContentValidator? jobContentValidator = null)
    : ICrawlerOrchestrationService
{
    private readonly CrawlerLifecycleOptions lifecycle = lifecycleOptions ?? CrawlerLifecycleOptions.Default;
    private readonly IJobContentValidator contentValidator = jobContentValidator ?? new JobContentValidator();
    private static readonly Regex HtmlTitleRegex = new(
        @"<title[^>]*>(.*?)</title>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
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
            var pageTitles = result.RawPages
                .Where(page => !string.IsNullOrWhiteSpace(page.Url))
                .GroupBy(page => page.Url, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => ExtractHtmlTitle(group.First().RawHtml), StringComparer.OrdinalIgnoreCase);

            foreach (var rawJob in result.Jobs)
            {
                pageTitles.TryGetValue(rawJob.Url ?? string.Empty, out var pageTitle);
                savedCount += await SaveJobAsync(rawJob, request.Url, pageTitle, cancellationToken);
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

    public async Task<IReadOnlyList<JobSourceDto>> GetSourcesAsync(CancellationToken cancellationToken)
    {
        return await dbContext.JobSources
            .AsNoTracking()
            .OrderBy(source => source.Name)
            .Select(source => new JobSourceDto(
                source.Id,
                source.Name,
                source.BaseUrl,
                source.Method,
                source.Parser,
                source.Enabled,
                source.CrawlFrequencyMinutes,
                source.LastCrawledAt,
                source.TermsCheckedAt))
            .ToListAsync(cancellationToken);
    }

    private async Task<int> SaveJobAsync(
        RawJobInput rawJob,
        string crawlUrl,
        string? pageTitle,
        CancellationToken cancellationToken)
    {
        // A location field can absorb the whole detail block (Comspek) or a
        // trailing ad sentence (SEEK). Clean it for every parser and preserve
        // the removed text in the description instead of discarding it.
        var sanitisedLocation = LocationSanitiser.Sanitise(rawJob.Location);
        var candidate = rawJob with
        {
            Location = sanitisedLocation.Location,
            DescriptionText = LocationSanitiser.PrependRemainder(
                sanitisedLocation.RemovedRemainder,
                rawJob.DescriptionText)
        };

        var validation = contentValidator.Validate(
            candidate,
            new JobValidationContext(CrawlUrl: crawlUrl, PageTitle: pageTitle));

        var contentHash = deduplicationService.GenerateContentHash(candidate);
        var existingJob = await dbContext.Jobs
            .Include(existing => existing.Skills)
            .FirstOrDefaultAsync(existing =>
                existing.ContentHash == contentHash ||
                (candidate.SourceJobId != null && existing.Source == candidate.Source && existing.SourceJobId == candidate.SourceJobId) ||
                existing.Url == candidate.Url,
                cancellationToken);

        if (!validation.IsAccepted)
        {
            // A rejected candidate must never be (re)activated, including when a
            // previous worker version already saved it.
            if (existingJob is not null && existingJob.IsActive)
            {
                Expire(existingJob, DateTime.UtcNow);
                await dbContext.SaveChangesAsync(cancellationToken);
            }

            return 0;
        }

        if (existingJob is not null)
        {
            existingJob.LastSeenAt = DateTime.UtcNow;
            existingJob.UpdatedAt = DateTime.UtcNow;
            existingJob.ClosingDate = candidate.ClosingDate ?? existingJob.ClosingDate;
            existingJob.Company = CleanOptional(candidate.Company) ?? existingJob.Company;
            existingJob.Location = CleanOptional(candidate.Location) ?? existingJob.Location;
            existingJob.DescriptionSummary = Summarise(candidate.DescriptionText) ?? existingJob.DescriptionSummary;
            existingJob.Region = normalisationService.ResolveRegion(
                existingJob.Location,
                existingJob.DescriptionSummary ?? candidate.DescriptionText);
            existingJob.EmploymentType = InferEmploymentType(candidate.DescriptionText);
            existingJob.WorkMode = InferWorkMode(candidate.DescriptionText);
            existingJob.ExperienceBand = InferExperienceBand(candidate.DescriptionText);
            SetSkills(existingJob, candidate);
            existingJob.IsActive = true;
            existingJob.ExpiredAt = null;
            existingJob.MissingCrawlCount = 0;
            return 0;
        }

        var (salaryMin, salaryMax) = ParseSalary(candidate.SalaryText);
        var now = DateTime.UtcNow;
        var job = new Job
        {
            Id = Guid.NewGuid(),
            Source = candidate.Source,
            SourceJobId = candidate.SourceJobId,
            Title = candidate.Title.Trim(),
            Company = CleanOptional(candidate.Company),
            Location = CleanOptional(candidate.Location),
            Region = normalisationService.ResolveRegion(candidate.Location, candidate.DescriptionText),
            SalaryMin = salaryMin,
            SalaryMax = salaryMax,
            EmploymentType = InferEmploymentType(candidate.DescriptionText),
            Seniority = normalisationService.StandardiseSeniority(candidate.Title),
            WorkMode = InferWorkMode(candidate.DescriptionText),
            ExperienceBand = InferExperienceBand(candidate.DescriptionText),
            Industry = "Unknown",
            DescriptionSummary = Summarise(candidate.DescriptionText),
            Url = candidate.Url,
            ContentHash = contentHash,
            PostedDate = candidate.PostedDate,
            ClosingDate = candidate.ClosingDate,
            IsActive = true,
            FirstSeenAt = now,
            LastSeenAt = now,
            CreatedAt = now,
            UpdatedAt = now
        };

        SetSkills(job, candidate);

        dbContext.Jobs.Add(job);
        return 1;
    }

    private async Task ReconcileJobsAsync(
        string source,
        SourceFetchResult result,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var staleCutoff = now.AddDays(-Math.Max(lifecycle.StaleAfterDays, 1));
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
            else if (job.LastSeenAt < staleCutoff)
            {
                // Not seen for the freshness window (2/7/15 days): assume filled.
                Expire(job, now);
            }
            else if (result.IsComplete && !seenUrls.Contains(job.Url))
            {
                job.MissingCrawlCount++;
                job.UpdatedAt = now;
                if (job.MissingCrawlCount >= Math.Max(lifecycle.MissingCrawlThreshold, 1)) Expire(job, now);
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

    private static string? ExtractHtmlTitle(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        var match = HtmlTitleRegex.Match(html);
        if (!match.Success)
        {
            return null;
        }

        var value = Regex.Replace(match.Groups[1].Value, @"\s+", " ").Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
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
