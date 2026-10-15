using Application.Common;
using Application.Metrics;
using Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Application.Jobs;

public sealed record JobFieldBackfillResult(
    int JobsScanned,
    int JobsUpdated,
    int SalaryFilled,
    int WorkModeFilled,
    int SeniorityFilled,
    int EmploymentTypeFilled,
    int SalaryConflictsNoted);

public interface IJobFieldExtractionService
{
    /// <summary>
    /// Applies the deterministic field extractors to one job. Only fills a
    /// missing field; never overwrites a source-provided value. The one
    /// deliberate refinement is the uninformative <c>Intermediate</c> seniority
    /// default (title has no explicit marker), which a graduate/entry signal or
    /// a years-of-experience band may correct.
    /// </summary>
    FieldChange ApplyDeterministic(Job job);

    /// <summary>
    /// Idempotent repair pass across every stored job. A second run finds the
    /// same derived values and reports zero changes.
    /// </summary>
    Task<JobFieldBackfillResult> BackfillAsync(CancellationToken cancellationToken);
}

public sealed record FieldChange(bool Salary, bool WorkMode, bool Seniority, bool EmploymentType, bool SalaryConflictNoted)
{
    public static readonly FieldChange None = new(false, false, false, false, false);

    public bool Any => Salary || WorkMode || Seniority || EmploymentType || SalaryConflictNoted;
}

/// <summary>
/// Deterministic deep extraction for work mode, seniority, employment type and
/// salary, shared by ingestion and the idempotent backfill so both agree.
///
/// Guarantees:
/// <list type="bullet">
/// <item>a source-provided value is never overwritten (backfill and ingestion
/// only fill blanks; AI results are always superseded by a deterministic
/// reading);</item>
/// <item>an hourly/day rate keeps its pay period and original text — the
/// annual-equivalent is a labelled derived value, never a stored replacement
/// (Brief §4);</item>
/// <item>an "up to"/"from" figure stays one-sided;</item>
/// <item>implausible parses are flagged, not silently deleted.</item>
/// </list>
/// </summary>
public sealed class JobFieldExtractionService(
    IApplicationDbContext dbContext,
    ILogger<JobFieldExtractionService>? logger = null) : IJobFieldExtractionService
{
    public FieldChange ApplyDeterministic(Job job)
    {
        var salary = ApplySalary(job);
        var workMode = ApplyWorkMode(job);
        var seniority = ApplySeniority(job);
        var employmentType = ApplyEmploymentType(job);
        var conflict = NoteSalaryConflict(job);

        if (salary || workMode || seniority || employmentType || conflict)
        {
            job.UpdatedAt = DateTime.UtcNow;
        }

        return new FieldChange(salary, workMode, seniority, employmentType, conflict);
    }

    public async Task<JobFieldBackfillResult> BackfillAsync(CancellationToken cancellationToken)
    {
        var jobs = await dbContext.Jobs.ToListAsync(cancellationToken);
        var updated = 0;
        var salaryFilled = 0;
        var workModeFilled = 0;
        var seniorityFilled = 0;
        var employmentTypeFilled = 0;
        var conflictsNoted = 0;

        foreach (var job in jobs)
        {
            var change = ApplyDeterministic(job);
            if (!change.Any)
            {
                continue;
            }

            updated++;
            if (change.Salary) salaryFilled++;
            if (change.WorkMode) workModeFilled++;
            if (change.Seniority) seniorityFilled++;
            if (change.EmploymentType) employmentTypeFilled++;
            if (change.SalaryConflictNoted) conflictsNoted++;
        }

        if (updated > 0)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        logger?.LogInformation(
            "Field extraction backfill: scanned {JobsScanned} jobs, updated {JobsUpdated} (salary {SalaryFilled}, work mode {WorkModeFilled}, seniority {SeniorityFilled}, employment type {EmploymentTypeFilled}, salary conflicts noted {ConflictsNoted}).",
            jobs.Count,
            updated,
            salaryFilled,
            workModeFilled,
            seniorityFilled,
            employmentTypeFilled,
            conflictsNoted);

        return new JobFieldBackfillResult(
            jobs.Count,
            updated,
            salaryFilled,
            workModeFilled,
            seniorityFilled,
            employmentTypeFilled,
            conflictsNoted);
    }

    private static bool ApplyWorkMode(Job job)
    {
        var extraction = WorkModeExtractor.Extract(
            job.Title,
            job.Location,
            job.Region,
            job.DescriptionSummary,
            job.DescriptionText);

        if (!extraction.HasValue)
        {
            return false;
        }

        var current = job.WorkMode;
        var source = job.WorkModeSource;

        // Never overwrite a source statement or a title cue. A low-confidence
        // location inference and an AI result may both be upgraded.
        var mayReplace = string.IsNullOrEmpty(current)
            || current == "Unknown"
            || string.Equals(source, "location", StringComparison.OrdinalIgnoreCase)
            || string.Equals(source, "ai-enrichment", StringComparison.OrdinalIgnoreCase);

        if (!mayReplace || current == extraction.Mode)
        {
            return false;
        }

        job.WorkMode = extraction.Mode;
        job.WorkModeSource = extraction.Source;
        job.WorkModeConfidence = extraction.Confidence;
        return true;
    }

    private static bool ApplySeniority(Job job)
    {
        var extraction = SeniorityExtractor.Extract(job.Title, job.DescriptionSummary, job.DescriptionText);
        var current = job.Seniority;
        var source = job.SenioritySource;

        if (!extraction.HasValue)
        {
            // Keep the historical guarantee that seniority is never blank: an
            // unclassifiable title falls back to the explicit Intermediate
            // default (recorded as such so it can be refined later).
            if (string.IsNullOrEmpty(current) || current == "Unknown")
            {
                job.Seniority = "Intermediate";
                job.SenioritySource = "default";
                job.SeniorityConfidence = 0.2m;
                return true;
            }

            return false;
        }

        if (string.Equals(source, "ai-enrichment", StringComparison.OrdinalIgnoreCase))
        {
            // Deterministic always wins over AI.
        }
        else if (string.IsNullOrEmpty(current) || current == "Unknown" || current == "Intermediate")
        {
            // Blank, or the uninformative Intermediate default/weak band, may be
            // corrected by an explicit title marker or a graduate/years signal.
        }
        else
        {
            return false;
        }

        if (string.Equals(current, extraction.Value, StringComparison.Ordinal))
        {
            return false;
        }

        job.Seniority = extraction.Value;
        job.SenioritySource = extraction.Source;
        job.SeniorityConfidence = extraction.Confidence;
        return true;
    }

    private static bool ApplyEmploymentType(Job job)
    {
        var extraction = EmploymentTypeExtractor.Extract(job.DescriptionSummary, job.DescriptionText);
        if (!extraction.HasValue)
        {
            return false;
        }

        var current = job.EmploymentType;
        var source = job.EmploymentTypeSource;

        var mayReplace = string.IsNullOrEmpty(current)
            || current == "Unknown"
            || string.Equals(source, "ai-enrichment", StringComparison.OrdinalIgnoreCase);

        if (!mayReplace || current == extraction.Value)
        {
            return false;
        }

        job.EmploymentType = extraction.Value;
        job.EmploymentTypeSource = extraction.Source;
        return true;
    }

    /// <summary>
    /// Fills numeric salary bounds from the advertised text when present, else
    /// from the retained description. Existing bounds are never overwritten; an
    /// hourly/day rate keeps its period.
    /// </summary>
    private static bool ApplySalary(Job job)
    {
        var hasBounds = job.SalaryMin is not null || job.SalaryMax is not null;
        if (hasBounds)
        {
            if (string.IsNullOrEmpty(job.SalarySource))
            {
                job.SalarySource = "source";
            }

            return false;
        }

        // 1. The advertised salary text supplied by the crawl always wins.
        if (!string.IsNullOrWhiteSpace(job.SalaryText))
        {
            var sourceParse = SalaryParser.Parse(job.SalaryText, job.Title);
            if (sourceParse.HasValue)
            {
                return StoreSalary(job, sourceParse, job.SalaryText.Trim(), "source");
            }
        }

        // 2. Deep scan of the retained full description.
        var scanned = SalaryDescriptionScanner.Scan(job.DescriptionText, job.Title)
            ?? SalaryDescriptionScanner.Scan(job.DescriptionSummary, job.Title);
        if (scanned is null)
        {
            return false;
        }

        return StoreSalary(job, scanned.Parse, scanned.MatchedText, "description");
    }

    private static bool StoreSalary(Job job, SalaryParse salary, string text, string source)
    {
        job.SalaryMin = salary.Min;
        job.SalaryMax = salary.Max;
        job.SalaryCurrency = salary.Currency;
        job.SalaryPeriod = salary.PayPeriod.ToString();
        job.SalaryQuestionable = salary.IsQuestionable;
        job.SalaryQuestionableReason = salary.QuestionableReason;
        job.SalarySource = source;

        // The advertised text is retained as provenance. A description-derived
        // figure records the matched snippet so it is auditable.
        if (string.IsNullOrWhiteSpace(job.SalaryText))
        {
            job.SalaryText = text;
        }

        return true;
    }

    /// <summary>
    /// Records (once, so the pass stays idempotent) when a description figure
    /// disagrees with the retained advertised salary. The source value wins.
    /// </summary>
    private static bool NoteSalaryConflict(Job job)
    {
        if (job.SalaryMin is null && job.SalaryMax is null)
        {
            return false;
        }

        var scanned = SalaryDescriptionScanner.Scan(job.DescriptionText, job.Title);
        if (scanned is null || !scanned.Parse.HasValue)
        {
            return false;
        }

        var descriptionMidpoint = scanned.Parse.Midpoint;
        var sourceMidpoint = job.SalaryMin.HasValue || job.SalaryMax.HasValue
            ? ((job.SalaryMin ?? job.SalaryMax!.Value) + (job.SalaryMax ?? job.SalaryMin!.Value)) / 2m
            : (decimal?)null;

        if (descriptionMidpoint is null || sourceMidpoint is null)
        {
            return false;
        }

        // A 10% difference is tolerated so a rounded description figure does not
        // create noise; anything larger is a real conflict worth recording.
        var difference = Math.Abs(descriptionMidpoint.Value - sourceMidpoint.Value);
        if (difference <= sourceMidpoint.Value * 0.1m)
        {
            return false;
        }

        var note = $"description salary {scanned.MatchedText} differs from the advertised source salary; source retained";
        if (job.SalaryQuestionableReason?.Contains(note, StringComparison.OrdinalIgnoreCase) == true)
        {
            return false;
        }

        job.SalaryQuestionableReason = string.IsNullOrWhiteSpace(job.SalaryQuestionableReason)
            ? note
            : $"{job.SalaryQuestionableReason}; {note}";
        return true;
    }
}
