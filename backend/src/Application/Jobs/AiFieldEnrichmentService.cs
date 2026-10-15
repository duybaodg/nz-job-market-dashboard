using System.Globalization;
using Application.Common;
using Application.Metrics;
using Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Application.Jobs;

public sealed record AiFieldEnrichmentBatchResult(
    int Candidates,
    int Attempted,
    int Updated,
    int Failed,
    int SalaryFilled,
    int WorkModeFilled,
    int SeniorityFilled,
    bool Enabled);

public interface IAiFieldEnrichmentService
{
    bool IsEnabled { get; }

    /// <summary>
    /// Enriches at most <paramref name="max"/> jobs that are still missing a
    /// deterministic value. Bounded per call so a worker cycle cannot run up an
    /// unbounded API bill.
    /// </summary>
    Task<AiFieldEnrichmentBatchResult> RunBatchAsync(int max, CancellationToken cancellationToken);
}

/// <summary>
/// Optional AI enrichment pass, disabled by default (AI_EXTRACTION_ENABLED).
///
/// It only ever fills fields the deterministic extractors could not:
/// <list type="bullet">
/// <item>salary only when no numeric bounds exist;</item>
/// <item>work mode only when Unknown;</item>
/// <item>seniority only when Unknown or still the low-confidence
/// <c>default</c> Intermediate.</item>
/// </list>
/// A deterministic reading always supersedes an AI one on the next pass, and
/// every AI value is recorded as method <c>ai-enrichment</c> with the model name
/// and confidence. Failures are logged, throttled and skipped — they never
/// block crawling.
/// </summary>
public sealed class AiFieldEnrichmentService(
    IApplicationDbContext dbContext,
    IAiFieldEnrichmentClient client,
    IOptions<AiFieldEnrichmentOptions> options,
    ILogger<AiFieldEnrichmentService>? logger = null) : IAiFieldEnrichmentService
{
    private readonly AiFieldEnrichmentOptions settings = options.Value;

    public bool IsEnabled => settings.Enabled && !string.IsNullOrWhiteSpace(settings.ApiKey);

    public async Task<AiFieldEnrichmentBatchResult> RunBatchAsync(int max, CancellationToken cancellationToken)
    {
        if (!IsEnabled)
        {
            return new AiFieldEnrichmentBatchResult(0, 0, 0, 0, 0, 0, 0, false);
        }

        var batchSize = max > 0 ? max : Math.Max(settings.BatchSize, 1);
        var now = DateTime.UtcNow;
        var throttleCutoff = now.AddHours(-Math.Max(settings.ThrottleHours, 0));

        var candidates = await dbContext.Jobs
            .Where(job => job.IsActive
                && (job.WorkMode == "Unknown"
                    || (job.SalaryMin == null && job.SalaryMax == null)
                    || job.Seniority == "Unknown"
                    || job.SenioritySource == "default")
                && (job.AiEnrichedAt == null || job.AiEnrichedAt < throttleCutoff))
            .OrderBy(job => job.UpdatedAt)
            .Take(batchSize)
            .ToListAsync(cancellationToken);

        var attempted = 0;
        var updated = 0;
        var failed = 0;
        var salaryFilled = 0;
        var workModeFilled = 0;
        var seniorityFilled = 0;

        foreach (var job in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            attempted++;

            AiFieldEnrichmentResponse? response;
            try
            {
                response = await client.ExtractAsync(
                    new AiFieldEnrichmentRequest(job.Title, job.Location, BuildDescription(job), job.SalaryText),
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger?.LogWarning(exception, "AI enrichment failed for job {JobId}", job.Id);
                response = null;
            }

            var changed = false;
            if (response is not null)
            {
                var salary = TryApplySalary(job, response);
                var workMode = TryApplyWorkMode(job, response);
                var seniority = TryApplySeniority(job, response);
                changed = salary || workMode || seniority;
                if (salary) salaryFilled++;
                if (workMode) workModeFilled++;
                if (seniority) seniorityFilled++;
            }
            else
            {
                failed++;
            }

            // Throttle every attempt, successful or not, so an unhelpful or
            // failing listing is not retried (and re-billed) every cycle.
            job.AiEnrichedAt = now;
            job.AiEnrichmentModel = response?.Model ?? settings.Model;
            if (changed)
            {
                job.UpdatedAt = now;
                updated++;
            }
        }

        if (attempted > 0)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        logger?.LogInformation(
            "AI field enrichment ({Model}): {Attempted} attempted of {Candidates} candidates, {Updated} updated (salary {Salary}, work mode {WorkMode}, seniority {Seniority}), {Failed} failed.",
            settings.Model,
            attempted,
            candidates.Count,
            updated,
            salaryFilled,
            workModeFilled,
            seniorityFilled,
            failed);

        return new AiFieldEnrichmentBatchResult(
            candidates.Count, attempted, updated, failed, salaryFilled, workModeFilled, seniorityFilled, true);
    }

    private bool TryApplySalary(Job job, AiFieldEnrichmentResponse response)
    {
        if (job.SalaryMin is not null || job.SalaryMax is not null)
        {
            return false;
        }

        if (response.SalaryMin is null && response.SalaryMax is null)
        {
            return false;
        }

        // Rebuild a synthetic advertised text and run it through the central
        // parser so every plausibility guard (hourly/day/annual bounds,
        // one-sided ranges, currency) applies to AI figures too.
        var currency = string.IsNullOrWhiteSpace(response.Currency) ? "NZD" : response.Currency.Trim().ToUpperInvariant();
        var symbol = currency == "NZD" ? "$" : currency + " ";
        var min = response.SalaryMin;
        var max = response.SalaryMax;
        var text = min is not null && max is not null
            ? $"{symbol}{Format(min.Value)} - {symbol}{Format(max.Value)}"
            : $"{symbol}{Format((min ?? max)!.Value)}";
        text += NormalisePeriod(response.Period) switch
        {
            "Hour" => " per hour",
            "Day" => " per day",
            _ => " per annum"
        };

        var parse = SalaryParser.Parse(text, job.Title);
        if (!parse.HasValue)
        {
            return false;
        }

        job.SalaryMin = parse.Min;
        job.SalaryMax = parse.Max;
        job.SalaryCurrency = parse.Currency;
        job.SalaryPeriod = parse.PayPeriod.ToString();
        job.SalaryQuestionable = parse.IsQuestionable;
        job.SalaryQuestionableReason = parse.QuestionableReason;
        job.SalarySource = "ai-enrichment";
        if (string.IsNullOrWhiteSpace(job.SalaryText))
        {
            job.SalaryText = text;
        }

        return true;
    }

    private bool TryApplyWorkMode(Job job, AiFieldEnrichmentResponse response)
    {
        if (job.WorkMode != "Unknown")
        {
            return false;
        }

        var mode = response.WorkMode?.Trim().ToLowerInvariant() switch
        {
            "remote" => "Remote",
            "hybrid" => "Hybrid",
            "on-site" or "onsite" or "on site" => "On-site",
            _ => null
        };

        if (mode is null || !MeetsConfidence(response.Confidence))
        {
            return false;
        }

        job.WorkMode = mode;
        job.WorkModeSource = "ai-enrichment";
        job.WorkModeConfidence = response.Confidence;
        return true;
    }

    private bool TryApplySeniority(Job job, AiFieldEnrichmentResponse response)
    {
        var fillable = job.Seniority == "Unknown"
            || string.Equals(job.SenioritySource, "default", StringComparison.OrdinalIgnoreCase);
        if (!fillable)
        {
            return false;
        }

        var seniority = response.Seniority?.Trim().ToLowerInvariant() switch
        {
            "junior" or "graduate" or "intern" => "Junior",
            "intermediate" or "mid" => "Intermediate",
            "senior" => "Senior",
            "lead" or "principal" or "staff" => "Lead",
            _ => null
        };

        if (seniority is null || !MeetsConfidence(response.Confidence))
        {
            return false;
        }

        job.Seniority = seniority;
        job.SenioritySource = "ai-enrichment";
        job.SeniorityConfidence = response.Confidence;
        return true;
    }

    private bool MeetsConfidence(decimal? confidence) =>
        confidence is null || confidence.Value >= settings.MinConfidence;

    private static string BuildDescription(Job job)
    {
        var text = string.IsNullOrWhiteSpace(job.DescriptionText) ? job.DescriptionSummary : job.DescriptionText;
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        text = FieldTextSanitizer.Sanitize(text);
        const int cap = 4_000;
        return text.Length <= cap ? text : text[..cap];
    }

    private static string Format(decimal value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static string NormalisePeriod(string? period) => period?.Trim().ToLowerInvariant() switch
    {
        "hour" or "hourly" or "per hour" => "Hour",
        "day" or "daily" or "per day" => "Day",
        _ => "Year"
    };
}
