using System.Text.RegularExpressions;
using System.Runtime.ExceptionServices;
using Application.Common;
using Application.Deduplication;
using Application.Employers;
using Application.Jobs;
using Application.Metrics;
using Application.Normalisation;
using Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

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
    IJobContentValidator? jobContentValidator = null,
    ILogger<CrawlerOrchestrationService>? logger = null,
    SkillExtractor? skillExtractor = null,
    IEmployerIdentityService? employerIdentityService = null,
    IJobFieldExtractionService? fieldExtractionService = null)
    : ICrawlerOrchestrationService
{
    private readonly CrawlerLifecycleOptions lifecycle = lifecycleOptions ?? CrawlerLifecycleOptions.Default;
    private readonly IJobContentValidator contentValidator = jobContentValidator ?? new JobContentValidator();
    private readonly SkillExtractor skillExtractor = skillExtractor ?? SkillExtractor.Default;
    private readonly IJobFieldExtractionService fieldExtraction =
        fieldExtractionService ?? new JobFieldExtractionService(dbContext);
    private static readonly Regex HtmlTitleRegex = new(
        @"<title[^>]*>(.*?)</title>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    // A salary range must be anchored by a currency symbol, an explicit "k"
    // suffix or a fully written 4-6 digit number on both sides. Without that
    // anchor, bare number pairs such as "100-160" (a SEEK teaser fragment) were
    // misread as a $100k-$160k salary. The parsing itself now lives in
    // Application.Metrics.SalaryParser so ingestion and the dashboard agree.
    private static readonly Regex ExperienceRegex = new(
        @"(?:(?:at least|minimum(?: of)?|over|more than)\s+)?(\d{1,2})(?:\s*\+)?(?:\s*(?:-|to|–)\s*\d{1,2})?\s+years?"
        + @"|"
        + @"experience(?:\s+of|\s+in|\s+with)?\s+(?:at\s+least\s+)?(\d{1,2})(?:\s*\+)?\s+years?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

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

        // Round 15 (H-3): one running run per source. The pre-check gives a
        // clear conflict in the common case; a partial unique index on
        // crawl_runs(source) WHERE status='Running' closes the race between two
        // concurrent callers. Both the scheduler and the admin trigger use this
        // same service, so both respect it.
        var alreadyRunning = await dbContext.CrawlRuns
            .AsNoTracking()
            .AnyAsync(run => run.Source == source && run.Status == "Running", cancellationToken);
        if (alreadyRunning)
        {
            var message = $"A crawl is already running for source '{source}'.";
            return new CrawlRunResultDto(CreateTransientRun(source, "Conflict", message), false, message);
        }

        var crawlRun = new CrawlRun
        {
            Id = Guid.NewGuid(),
            Source = source,
            Status = "Running",
            StartedAt = DateTime.UtcNow
        };

        dbContext.CrawlRuns.Add(crawlRun);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            var message = $"A crawl is already running for source '{source}'.";
            return new CrawlRunResultDto(CreateTransientRun(source, "Conflict", message), false, message);
        }

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

            // Round 15 (M-2): prepare every candidate first, then load the
            // possible existing rows (with skills) in one query instead of one
            // Include round-trip per job.
            var prepared = result.Jobs
                .Select(rawJob =>
                {
                    pageTitles.TryGetValue(rawJob.Url ?? string.Empty, out var pageTitle);
                    return PrepareCandidate(rawJob, request.Url, pageTitle);
                })
                .ToList();

            var existingJobs = await LoadExistingJobsAsync(prepared, cancellationToken);
            var byHash = new Dictionary<string, Job>(StringComparer.Ordinal);
            var byUrl = new Dictionary<string, Job>(StringComparer.OrdinalIgnoreCase);
            var bySourceJobId = new Dictionary<(string Source, string SourceJobId), Job>();
            foreach (var existing in existingJobs)
            {
                byHash[existing.ContentHash] = existing;
                byUrl[existing.Url] = existing;
                if (!string.IsNullOrWhiteSpace(existing.SourceJobId))
                {
                    bySourceJobId[(existing.Source, existing.SourceJobId)] = existing;
                }
            }

            foreach (var candidate in prepared)
            {
                var existing = FindExisting(candidate, byHash, byUrl, bySourceJobId);
                var (saved, created) = await SaveJobAsync(candidate, existing, cancellationToken);
                savedCount += saved;
                if (created is not null)
                {
                    byHash[created.ContentHash] = created;
                    byUrl[created.Url] = created;
                    if (!string.IsNullOrWhiteSpace(created.SourceJobId))
                    {
                        bySourceJobId[(created.Source, created.SourceJobId)] = created;
                    }
                }
            }

            await ReconcileJobsAsync(source, result, cancellationToken);

            crawlRun.Status = "Completed";
            crawlRun.FinishedAt = DateTime.UtcNow;
            crawlRun.TotalPages = result.RawPages.Count;
            crawlRun.TotalJobsFound = result.Jobs.Count;
            crawlRun.TotalJobsSaved = savedCount;
            await dbContext.SaveChangesAsync(cancellationToken);

            // A healthy board with no technology roles must read as such, not as
            // a silent zero/failure (Round 4 Group F).
            if (result.Stats is not null)
            {
                logger?.LogInformation(
                    "Source {Source}: {Kept} IT roles of {Total} total candidates.",
                    source,
                    result.Stats.Kept,
                    result.Stats.Candidates);
            }

            return new CrawlRunResultDto(ToDto(crawlRun), true, "Crawl run completed.");
        }
        catch (OperationCanceledException)
        {
            // A cancelled request (client disconnect / worker shutdown) must not
            // leave the run stuck in "Running" forever.
            crawlRun.Status = "Failed";
            crawlRun.FinishedAt = DateTime.UtcNow;
            crawlRun.ErrorMessage = "Crawl cancelled.";
            await dbContext.SaveChangesAsync(CancellationToken.None);
            throw;
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
            // Retired rows are kept for historical jobs but are not part of the
            // current registry, so they never surface in the admin source list.
            .Where(source => source.RetiredAt == null)
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
                source.TermsCheckedAt,
                source.RetiredAt))
            .ToListAsync(cancellationToken);
    }

    private async Task<(int Saved, Job? Created)> SaveJobAsync(
        PreparedCandidate prepared,
        Job? existingJob,
        CancellationToken cancellationToken)
    {
        var candidate = prepared.Candidate;
        var regionHint = prepared.RegionHint;

        if (!prepared.Validation.IsAccepted)
        {
            // A rejected candidate must never be (re)activated, including when a
            // previous worker version already saved it.
            if (existingJob is not null && existingJob.IsActive)
            {
                Expire(existingJob, DateTime.UtcNow);
                await dbContext.SaveChangesAsync(cancellationToken);
            }

            return (0, null);
        }

        if (existingJob is not null)
        {
            existingJob.LastSeenAt = DateTime.UtcNow;
            existingJob.UpdatedAt = DateTime.UtcNow;
            // The board is authoritative for the advertised title; re-crawls
            // refresh it so title-derived fields (seniority, role family) stay
            // in step with the source.
            if (!string.IsNullOrWhiteSpace(candidate.Title))
            {
                existingJob.Title = candidate.Title.Trim();
            }

            existingJob.ClosingDate = candidate.ClosingDate ?? existingJob.ClosingDate;
            existingJob.Company = CleanOptional(candidate.Company) ?? existingJob.Company;
            existingJob.Location = CleanOptional(candidate.Location) ?? existingJob.Location;
            existingJob.DescriptionSummary = Summarise(candidate.DescriptionText) ?? existingJob.DescriptionSummary;
            existingJob.DescriptionText = CleanDescription(candidate.DescriptionText) ?? existingJob.DescriptionText;
            existingJob.Region = regionHint ?? normalisationService.ResolveRegion(
                existingJob.Location,
                existingJob.DescriptionSummary ?? candidate.DescriptionText);
            existingJob.ExperienceBand = InferExperienceBand(candidate.DescriptionText);
            // Refresh the fields that a better parser or a re-crawl can now fill
            // in; never overwrite a known value with a missing one.
            existingJob.PostedDate = candidate.PostedDate ?? existingJob.PostedDate;
            ApplySalary(existingJob, candidate.SalaryText, candidate.Title);
            SetSkills(existingJob, candidate);
            // Round 9: deterministic deep extraction fills any still-blank work
            // mode, seniority, employment type or salary. Source-provided and
            // already-derived values are never overwritten.
            fieldExtraction.ApplyDeterministic(existingJob);
            existingJob.IsActive = true;
            existingJob.ExpiredAt = null;
            existingJob.MissingCrawlCount = 0;
            existingJob.EmployerCanonicalId = await ResolveEmployerAsync(candidate.Source, existingJob.Company, cancellationToken);
            return (0, null);
        }

        var salary = SalaryParser.Parse(candidate.SalaryText, candidate.Title);
        var now = DateTime.UtcNow;
        var job = new Job
        {
            Id = Guid.NewGuid(),
            Source = candidate.Source,
            SourceJobId = candidate.SourceJobId,
            Title = candidate.Title.Trim(),
            Company = CleanOptional(candidate.Company),
            Location = CleanOptional(candidate.Location),
            Region = regionHint ?? normalisationService.ResolveRegion(candidate.Location, candidate.DescriptionText),
            SalaryMin = salary.Min,
            SalaryMax = salary.Max,
            SalaryText = CleanOptional(candidate.SalaryText),
            SalaryCurrency = salary.Currency,
            SalaryPeriod = salary.PayPeriod.ToString(),
            SalaryQuestionable = salary.IsQuestionable,
            SalaryQuestionableReason = salary.QuestionableReason,
            EmploymentType = "Unknown",
            Seniority = "Unknown",
            WorkMode = "Unknown",
            ExperienceBand = InferExperienceBand(candidate.DescriptionText),
            Industry = "Unknown",
            DescriptionSummary = Summarise(candidate.DescriptionText),
            DescriptionText = CleanDescription(candidate.DescriptionText),
            Url = candidate.Url,
            ContentHash = prepared.ContentHash,
            PostedDate = candidate.PostedDate,
            ClosingDate = candidate.ClosingDate,
            IsActive = true,
            FirstSeenAt = now,
            LastSeenAt = now,
            CreatedAt = now,
            UpdatedAt = now
        };

        SetSkills(job, candidate);
        job.EmployerCanonicalId = await ResolveEmployerAsync(candidate.Source, job.Company, cancellationToken);
        fieldExtraction.ApplyDeterministic(job);

        dbContext.Jobs.Add(job);
        return (1, job);
    }

    private PreparedCandidate PrepareCandidate(
        RawJobInput rawJob,
        string crawlUrl,
        string? pageTitle)
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
        return new PreparedCandidate(
            rawJob,
            candidate,
            validation,
            deduplicationService.GenerateContentHash(candidate),
            pageTitle,
            sanitisedLocation.RegionHint);
    }

    private async Task<List<Job>> LoadExistingJobsAsync(
        IReadOnlyList<PreparedCandidate> prepared,
        CancellationToken cancellationToken)
    {
        var hashes = prepared.Select(candidate => candidate.ContentHash).ToHashSet(StringComparer.Ordinal);
        var urls = prepared.Select(candidate => candidate.Candidate.Url).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sourceJobIds = prepared
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.Candidate.SourceJobId))
            .Select(candidate => candidate.Candidate.SourceJobId!)
            .ToHashSet(StringComparer.Ordinal);

        if (hashes.Count == 0 && urls.Count == 0 && sourceJobIds.Count == 0)
        {
            return [];
        }

        return await dbContext.Jobs
            .Include(existing => existing.Skills)
            .Where(existing =>
                hashes.Contains(existing.ContentHash)
                || urls.Contains(existing.Url)
                || (existing.SourceJobId != null && sourceJobIds.Contains(existing.SourceJobId)))
            .ToListAsync(cancellationToken);
    }

    private static Job? FindExisting(
        PreparedCandidate candidate,
        IReadOnlyDictionary<string, Job> byHash,
        IReadOnlyDictionary<string, Job> byUrl,
        IReadOnlyDictionary<(string Source, string SourceJobId), Job> bySourceJobId)
    {
        if (byHash.TryGetValue(candidate.ContentHash, out var byContentHash))
        {
            return byContentHash;
        }

        if (!string.IsNullOrWhiteSpace(candidate.Candidate.SourceJobId)
            && bySourceJobId.TryGetValue((candidate.Candidate.Source, candidate.Candidate.SourceJobId), out var byId))
        {
            return byId;
        }

        return byUrl.TryGetValue(candidate.Candidate.Url, out var byAddress) ? byAddress : null;
    }

    private sealed record PreparedCandidate(
        RawJobInput Raw,
        RawJobInput Candidate,
        JobValidationResult Validation,
        string ContentHash,
        string? PageTitle,
        string? RegionHint);

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

    private async Task<Guid?> ResolveEmployerAsync(string source, string? company, CancellationToken cancellationToken)
    {
        return employerIdentityService is null
            ? null
            : await employerIdentityService.ResolveCanonicalEmployerAsync(source, company, cancellationToken);
    }

    /// <summary>Full description kept only for dedup similarity, capped so a pathological page cannot bloat a row.</summary>
    private static string? CleanDescription(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var clean = value.Trim();
        return clean.Length <= 20000 ? clean : clean[..20000];
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

    /// <summary>
    /// Refreshes a re-crawled job's salary fields with full provenance. A
    /// missing salary text never clears a known value; an hourly/daily rate is
    /// stored with its own pay period and never annualised.
    /// </summary>
    private static void ApplySalary(Job job, string? salaryText, string? title)
    {
        if (string.IsNullOrWhiteSpace(salaryText))
        {
            return;
        }

        var salary = SalaryParser.Parse(salaryText, title);
        job.SalaryText = salaryText.Trim();
        job.SalaryCurrency = salary.Currency;
        job.SalaryPeriod = salary.PayPeriod.ToString();
        job.SalaryQuestionable = salary.IsQuestionable;
        job.SalaryQuestionableReason = salary.QuestionableReason;

        if (salary.HasValue)
        {
            job.SalaryMin = salary.Min;
            job.SalaryMax = salary.Max;
            job.SalarySource = "source";
        }
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

    private void SetSkills(Job job, RawJobInput rawJob)
    {
        // The Python worker already applies the shared taxonomy and emits a
        // "skills" array; prefer it so both sides stay in lock-step. The
        // Firecrawl path (and anything older) has no skills, so fall back to
        // extracting from the full description text with the same taxonomy.
        var desired = rawJob.Skills is { Count: > 0 }
            ? skillExtractor.NormaliseProvided(rawJob.Skills)
            : skillExtractor.ExtractForJob(rawJob.Title, rawJob.DescriptionText);

        var pending = desired.ToDictionary(skill => skill.Name, skill => skill.Type, StringComparer.OrdinalIgnoreCase);

        foreach (var existing in job.Skills.ToList())
        {
            if (!pending.TryGetValue(existing.SkillName, out var skillType))
            {
                // No longer mentioned: drop it so a re-crawl replaces the set
                // instead of accumulating stale skills.
                job.Skills.Remove(existing);
                dbContext.JobSkills.Remove(existing);
                continue;
            }

            if (!string.Equals(existing.SkillType, skillType, StringComparison.Ordinal))
            {
                existing.SkillType = skillType;
            }

            pending.Remove(existing.SkillName);
        }

        foreach (var (name, skillType) in pending)
        {
            var jobSkill = new JobSkill
            {
                Id = Guid.NewGuid(),
                JobId = job.Id,
                SkillName = name,
                SkillType = skillType,
                Confidence = 0.7m
            };
            job.Skills.Add(jobSkill);
            dbContext.JobSkills.Add(jobSkill);
        }
    }
}
