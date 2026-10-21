using System.Text.RegularExpressions;
using Application.Common;
using Application.Deduplication;
using Application.Metrics;
using Application.Normalisation;
using Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Application.Jobs;

public sealed record JobQualityGuardResult(int JunkRemoved, int RegionsNormalised, int LocationsRepaired, int SalariesBackfilled);

/// <summary>
/// Startup repair pass for data written by older worker versions:
/// <list type="bullet">
/// <item>removes records the Phase-1 <see cref="IJobContentValidator"/> rejects
/// (error/404 pages, articles, hub/landing pages);</item>
/// <item>re-normalises <c>region</c> for every remaining record with the
/// Phase-2 mapping.</item>
/// </list>
/// Both steps are idempotent: a second run finds nothing to remove and every
/// region already matches, so it reports zero changes.
/// </summary>
public interface IJobQualityGuard
{
    Task<JobQualityGuardResult> RunAsync(CancellationToken cancellationToken);
}

public sealed class JobQualityGuard(
    IApplicationDbContext dbContext,
    IJobContentValidator contentValidator,
    INormalisationService normalisationService,
    ILogger<JobQualityGuard> logger) : IJobQualityGuard
{
    private static readonly Regex HubTitle = new(
        """
        (?ix)
        ^\s*(?:home|careers?|jobs?|job\s+search|search\s+results?|working\s+here|people\s+and\s+careers
            |register(?:\s+as)?|login|sign\s+in|find\s+a\s+job|current\s+vacancies|job\s+vacancies
            |job\s+listings?|vacancies)\b
        | \bcareers?\s+at\b
        | \bjob\s+search\b
        | \bjobs?\s+in\s+(?:new\s+zealand|nz)\b
        | \brecruitment\s+specialists?\b
        | \blaunch\s+your\b
        | \bjob\s?seek(?:er)?s?\b
        | \bregister\s+as\b
        | \bprogram\s+your\s+path\b
        | \b\d+\s+jobs?\s+found\b
        | \bjob\s+alerts?\b
        | \bjob\s+board\b
        | \bcurrent\s+opportunities\b
        | \bjob\s+opportunit\w*
        | \bgraduate\s+jobs?\b
        | \bopen\s+now\b
        | \bcareers?\s+home\b
        | \bjoin\s+(?:our|the)\s+team\b
        | \bcareers\b
        | \bopportunit\w*\s*$
        """,
        RegexOptions.Compiled | RegexOptions.IgnorePatternWhitespace | RegexOptions.IgnoreCase);

    public static bool IsLowQualityTitle(string? title, string? source)
    {
        var value = (title ?? string.Empty).Trim();
        if (value.Length < 4)
        {
            return true;
        }

        if (HubTitle.IsMatch(value))
        {
            return true;
        }

        return source is not null && string.Equals(value, source.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    public async Task<JobQualityGuardResult> RunAsync(CancellationToken cancellationToken)
    {
        var sourceList = await dbContext.JobSources
            .AsNoTracking()
            .Select(source => new { source.Name, source.BaseUrl })
            .ToListAsync(cancellationToken);
        var baseUrls = sourceList.ToDictionary(
            source => source.Name,
            source => source.BaseUrl,
            StringComparer.OrdinalIgnoreCase);

        var now = DateTime.UtcNow;
        var junkRemoved = 0;
        var regionsNormalised = 0;
        var locationsRepaired = 0;
        var salariesBackfilled = 0;
        var offset = 0;

        // Round 15 (H-8): page by keyset order so a Pi never materialises the
        // whole full-text jobs table. Updates do not change the ordering key.
        while (true)
        {
            var jobs = await dbContext.Jobs
                .OrderBy(job => job.Id)
                .Skip(offset)
                .Take(500)
                .ToListAsync(cancellationToken);
            if (jobs.Count == 0)
            {
                break;
            }

            offset += jobs.Count;

        foreach (var job in jobs)
        {
            baseUrls.TryGetValue(job.Source, out var baseUrl);
            var validation = contentValidator.Validate(
                ToRawJobInput(job),
                new JobValidationContext(CrawlUrl: baseUrl));

            if (!validation.IsAccepted)
            {
                // Hard-remove: the record is not a job at all, so it must not
                // reappear through the status=all view either.
                dbContext.Jobs.Remove(job);
                junkRemoved++;
                continue;
            }

            // One-off provenance backfill for salary rows written before the
            // advertised text/units existed. All legacy values were annual NZD,
            // so synthesise a parse input from the numeric bounds and let the
            // central parser set the period and flag questionable parses. Rows
            // written by the current worker already carry salary_text and are
            // skipped, which keeps this pass idempotent.
            if (job.SalaryText is null && (job.SalaryMin is not null || job.SalaryMax is not null))
            {
                var synthetic = job.SalaryMin is not null && job.SalaryMax is not null
                    ? $"${job.SalaryMin} - ${job.SalaryMax}"
                    : $"${job.SalaryMin ?? job.SalaryMax}";
                var salary = SalaryParser.Parse(synthetic, job.Title);
                job.SalaryText = synthetic;
                job.SalaryCurrency = salary.Currency;
                job.SalaryPeriod = salary.PayPeriod.ToString();
                job.SalaryQuestionable = salary.IsQuestionable;
                job.SalaryQuestionableReason = salary.QuestionableReason;
                job.UpdatedAt = now;
                salariesBackfilled++;
            }

            // One-off repair for rows written before the shared location
            // sanitiser existed. Idempotent: a second run sees a clean
            // location and leaves it alone.
            var sanitised = LocationSanitiser.Sanitise(job.Location);
            if (!string.Equals(job.Location, sanitised.Location, StringComparison.Ordinal))
            {
                job.Location = sanitised.Location;
                job.DescriptionSummary = Summarise(
                    LocationSanitiser.PrependRemainder(sanitised.RemovedRemainder, job.DescriptionSummary));
                job.UpdatedAt = now;
                locationsRepaired++;
            }

            var region = sanitised.RegionHint
                ?? normalisationService.ResolveRegion(job.Location, job.DescriptionSummary);
            if (!string.Equals(job.Region, region, StringComparison.Ordinal))
            {
                job.Region = region;
                job.UpdatedAt = now;
                regionsNormalised++;
            }
        }

            if (junkRemoved > 0 || regionsNormalised > 0 || locationsRepaired > 0 || salariesBackfilled > 0)
            {
                await dbContext.SaveChangesAsync(cancellationToken);
            }
        }

        logger.LogInformation(
            "Job quality guard: removed {JunkRemoved} junk records, normalised {RegionsNormalised} regions, repaired {LocationsRepaired} locations, backfilled {SalariesBackfilled} salary rows.",
            junkRemoved,
            regionsNormalised,
            locationsRepaired,
            salariesBackfilled);
        return new JobQualityGuardResult(junkRemoved, regionsNormalised, locationsRepaired, salariesBackfilled);
    }

    private static string? Summarise(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var clean = System.Text.RegularExpressions.Regex.Replace(text.Trim(), @"\s+", " ");
        return clean.Length <= 240 ? clean : $"{clean[..237]}...";
    }

    private static RawJobInput ToRawJobInput(Job job)
    {
        string? salaryText = null;
        if (job.SalaryMin is not null || job.SalaryMax is not null)
        {
            salaryText = $"${job.SalaryMin ?? job.SalaryMax} - ${job.SalaryMax ?? job.SalaryMin}";
        }

        return new RawJobInput(
            job.Source,
            job.SourceJobId,
            job.Title,
            job.Company,
            job.Location,
            salaryText,
            job.DescriptionSummary,
            job.Url,
            job.PostedDate,
            job.ClosingDate);
    }
}
