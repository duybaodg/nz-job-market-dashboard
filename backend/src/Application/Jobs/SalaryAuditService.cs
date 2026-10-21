using System.Text.RegularExpressions;
using Application.Common;
using Application.Metrics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Application.Jobs;

/// <summary>Outcome of one auditable salary pass.</summary>
public sealed record SalaryAuditResult(
    int JobsScanned,
    int NonPaySalaryIgnored,
    int SeniorLowPayFlagged,
    int OutOfRangeCleared);

public interface ISalaryAuditService
{
    /// <summary>
    /// Re-runs questionable-parse detection over every stored salary. The pass is
    /// idempotent: it only acts on a row once and reports zero changes on a
    /// second run.
    /// </summary>
    Task<SalaryAuditResult> RunAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Round 11 salary audit. The generic worker extractor once read a bare "NNk"
/// token out of page chrome and stored it as the advertised salary:
/// <list type="bullet">
/// <item>the Potentia contact string "+64kayla@potentia.co.nz" became NZD
/// 64,000/yr for a Senior iOS contract that advertised no salary;</item>
/// <item>"Job Description (PDF, 198KB)" became NZD 198,000/yr;</item>
/// <item>"$87.62 k/yr" became NZD 62,000/yr;</item>
/// <item>"over 500k users" became NZD 500,000/yr.</item>
/// </list>
/// The crawler pattern is fixed at the source; this pass repairs rows already
/// written. It also re-flags low annual pay against a Senior/Lead title and
/// clears below-floor annual bounds that can only be an hourly/day rate.
/// </summary>
public sealed class SalaryAuditService(
    IApplicationDbContext dbContext,
    ILogger<SalaryAuditService>? logger = null) : ISalaryAuditService
{
    private const decimal MinimumAnnual = SalaryParser.MinimumAnnual;
    private const decimal SeniorAnnualFloor = 50_000m;
    private const decimal SeniorAnnualCeiling = 80_000m;

    private static readonly Regex BareAmountRegex = new(
        @"^\s*\$?\s*(?:\d{2,3}\s?[kK]|\d{4,6})\s*$",
        RegexOptions.Compiled);

    private static readonly Regex BareAmountValueRegex = new(
        @"^\s*\$?\s*(?:(?<k>\d{2,3})\s?[kK]|(?<n>\d{4,6}))\s*$",
        RegexOptions.Compiled);

    public async Task<SalaryAuditResult> RunAsync(CancellationToken cancellationToken)
    {
        var jobsScanned = 0;
        var nonPayIgnored = 0;
        var seniorLowPayFlagged = 0;
        var outOfRangeCleared = 0;
        var offset = 0;

        // Round 15 (H-8): page the salary rows and load only this batch's page
        // text (markdown first, raw HTML only as a fallback), keeping memory
        // bounded on the Pi.
        while (true)
        {
            var jobs = await dbContext.Jobs
                .Where(job => job.SalaryMin != null || job.SalaryMax != null || job.SalaryText != null)
                .OrderBy(job => job.Id)
                .Skip(offset)
                .Take(500)
                .ToListAsync(cancellationToken);
            if (jobs.Count == 0)
            {
                break;
            }

            jobsScanned += jobs.Count;
            offset += jobs.Count;

            var urls = jobs.Select(job => job.Url).Distinct().ToList();
            var rawPages = urls.Count == 0
                ? []
                : await dbContext.RawJobPages
                    .Where(page => urls.Contains(page.Url))
                    .Select(page => new
                    {
                        page.Url,
                        Text = page.Markdown ?? page.RawHtml,
                        page.CrawledAt
                    })
                    .ToListAsync(cancellationToken);
            var pageByUrl = rawPages
                .GroupBy(page => page.Url)
                .ToDictionary(group => group.Key, group => group.OrderByDescending(page => page.CrawledAt).First());

            var changed = false;
            foreach (var job in jobs)
            {
                // Upgrade rows an earlier audit cleared before the "ignored" marker
                // existed, so the deterministic extractor no longer re-parses them.
                if (job.SalaryMin is null && job.SalaryMax is null
                    && (AlreadyNoted(job.SalaryQuestionableReason, "non-pay token")
                        || AlreadyNoted(job.SalaryQuestionableReason, "below the annual floor"))
                    && !string.Equals(job.SalarySource, "ignored", StringComparison.OrdinalIgnoreCase))
                {
                    job.SalarySource = "ignored";
                    job.UpdatedAt = DateTime.UtcNow;
                    changed = true;
                }

                pageByUrl.TryGetValue(job.Url, out var page);
                var context = string.Join(
                    "\n",
                    new[] { job.DescriptionText, page?.Text }.Where(value => !string.IsNullOrWhiteSpace(value)));

                // 1. A bare "NNk"/"NN000" salary text that is really page chrome.
                //    The parser has already dropped an implausible bound, so read the
                //    value straight from the text when no bound survives.
                var salaryAmount = job.SalaryMin ?? job.SalaryMax ?? BareAmountValue(job.SalaryText);
                if (LooksLikeBareAmount(job.SalaryText)
                    && salaryAmount is { } amount
                    && NonPaySalaryEvidence.Matches(context, job.SalaryText, amount))
                {
                    if (!AlreadyNoted(job.SalaryQuestionableReason, "non-pay token"))
                    {
                        ClearSalary(
                            job,
                            $"advertised salary '{job.SalaryText}' ignored: it is a non-pay token (contact string, file size or quantity)");
                        nonPayIgnored++;
                        changed = true;
                    }

                    continue;
                }

                var hasBounds = job.SalaryMin is not null || job.SalaryMax is not null;
                if (!hasBounds)
                {
                    continue;
                }

                var midpoint = Midpoint(job);

                // 2. A low annual figure against a Senior/Lead title is a misparse risk.
                if (string.Equals(job.SalaryPeriod, "Year", StringComparison.OrdinalIgnoreCase)
                    && midpoint is >= SeniorAnnualFloor and <= SeniorAnnualCeiling
                    && IsSeniorOrLead(job.Seniority)
                    && !AlreadyNoted(job.SalaryQuestionableReason, "Senior/Lead"))
                {
                    job.SalaryQuestionable = true;
                    job.SalaryQuestionableReason = Join(
                        job.SalaryQuestionableReason,
                        "annual salary looks low for a Senior/Lead role");
                    job.UpdatedAt = DateTime.UtcNow;
                    seniorLowPayFlagged++;
                    changed = true;
                    continue;
                }

                // 3. An annual figure below the annual floor can only be an hourly or
                //    day rate that was mislabelled as a year figure.
                if (string.Equals(job.SalaryPeriod, "Year", StringComparison.OrdinalIgnoreCase)
                    && midpoint is < MinimumAnnual
                    && !AlreadyNoted(job.SalaryQuestionableReason, "below the annual floor"))
                {
                    ClearSalary(
                        job,
                        $"advertised salary '{job.SalaryText}' cleared: it is below the annual floor and looks like an hourly/day rate");
                    outOfRangeCleared++;
                    changed = true;
                }
            }

            if (changed)
            {
                await dbContext.SaveChangesAsync(cancellationToken);
            }
        }

        logger?.LogInformation(
            "Salary audit: scanned {JobsScanned} salary rows, ignored {NonPayIgnored} non-pay tokens, flagged {SeniorLowPayFlagged} low Senior/Lead annual figures, cleared {OutOfRangeCleared} below-floor rows.",
            jobsScanned,
            nonPayIgnored,
            seniorLowPayFlagged,
            outOfRangeCleared);

        return new SalaryAuditResult(jobsScanned, nonPayIgnored, seniorLowPayFlagged, outOfRangeCleared);
    }

    private static void ClearSalary(Domain.Entities.Job job, string reason)
    {
        job.SalaryMin = null;
        job.SalaryMax = null;
        job.SalaryPeriod = SalaryPayPeriod.Unknown.ToString();
        // Mark the retained text as ignored so the deterministic extractor does
        // not re-derive the same bogus bounds on the next startup.
        job.SalarySource = "ignored";
        job.SalaryQuestionable = true;
        job.SalaryQuestionableReason = Join(job.SalaryQuestionableReason, reason);
        job.UpdatedAt = DateTime.UtcNow;
    }

    private static bool LooksLikeBareAmount(string? salaryText)
        => !string.IsNullOrWhiteSpace(salaryText) && BareAmountRegex.IsMatch(salaryText);

    /// <summary>
    /// Reads the numeric value of a bare "NNk"/"NN000" text even when the parser
    /// dropped it as implausibly large (which is exactly what "500k" does).
    /// </summary>
    private static decimal? BareAmountValue(string? salaryText)
    {
        if (string.IsNullOrWhiteSpace(salaryText))
        {
            return null;
        }

        var match = BareAmountValueRegex.Match(salaryText);
        if (!match.Success)
        {
            return null;
        }

        if (match.Groups["k"].Success)
        {
            return decimal.Parse(match.Groups["k"].Value) * 1000m;
        }

        return decimal.Parse(match.Groups["n"].Value);
    }

    private static decimal? Midpoint(Domain.Entities.Job job)
    {
        if (job.SalaryMin is null && job.SalaryMax is null)
        {
            return null;
        }

        var min = job.SalaryMin ?? job.SalaryMax!.Value;
        var max = job.SalaryMax ?? job.SalaryMin!.Value;
        return (min + max) / 2m;
    }

    private static bool IsSeniorOrLead(string? seniority)
        => string.Equals(seniority, "Senior", StringComparison.OrdinalIgnoreCase)
            || string.Equals(seniority, "Lead", StringComparison.OrdinalIgnoreCase);

    private static bool AlreadyNoted(string? reason, string marker)
        => reason?.Contains(marker, StringComparison.OrdinalIgnoreCase) == true;

    private static string Join(string? existing, string addition)
        => string.IsNullOrWhiteSpace(existing) ? addition : $"{existing}; {addition}";
}

/// <summary>
/// Finds amounts in page text that a bare "NNk" scan can mistake for pay. Each
/// hit is a real non-pay figure (contact country code, decimal rate, file size
/// or headline quantity), not a generic number.
/// </summary>
internal static class NonPaySalaryEvidence
{
    private static readonly Regex BareThousands = new(
        @"\d{2,3}\s?[kK]",
        RegexOptions.Compiled);

    private static readonly Regex PhoneCountryCode = new(
        @"\+(?<n>\d{1,3})\s?[kK](?=[a-zA-Z])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex DecimalThousands = new(
        @"(?<!\.)\.(?<n>\d{2,3})\s?[kK]\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex FileSize = new(
        @"(?<![\d.])(?<n>\d{1,4})\s?(?<u>[kKmMgG]?)[bB]\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex Quantity = new(
        @"(?<![\d.])(?<n>\d{1,4})\s?(?<u>[kKmM]?)\s*(?:users?|followers?|customers?|subscribers?|downloads?|installs?|records?|rows?|employees?|clients?|companies|businesses|students?|members?|visits?|views?|hits?|pages?|results?)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool Matches(string? context, string? salaryText, decimal amount)
    {
        if (string.IsNullOrWhiteSpace(context) || string.IsNullOrWhiteSpace(salaryText))
        {
            return false;
        }

        // A stored "k" figure can only have come from the loose bare-k scan, so a
        // matching file-size/quantity/decimal token is conclusive. A full
        // "$198,000" is a real salary unless the token is a contact country code
        // (the Potentia "+64k..." synthesized into "$64000").
        var isBareThousands = BareThousands.IsMatch(salaryText);

        foreach (var (candidate, kind) in FindEvidence(context))
        {
            if (Math.Abs(candidate - amount) >= 0.5m)
            {
                continue;
            }

            if (kind == "phone" || isBareThousands)
            {
                return true;
            }
        }

        return false;
    }

    internal static IEnumerable<(decimal Amount, string Kind)> FindEvidence(string context)
    {
        foreach (Match match in PhoneCountryCode.Matches(context))
        {
            yield return (decimal.Parse(match.Groups["n"].Value) * 1000m, "phone");
        }

        foreach (Match match in DecimalThousands.Matches(context))
        {
            yield return (decimal.Parse(match.Groups["n"].Value) * 1000m, "decimal");
        }

        foreach (Match match in FileSize.Matches(context))
        {
            yield return (decimal.Parse(match.Groups["n"].Value) * UnitMultiplier(match.Groups["u"].Value), "size");
        }

        foreach (Match match in Quantity.Matches(context))
        {
            yield return (decimal.Parse(match.Groups["n"].Value) * UnitMultiplier(match.Groups["u"].Value), "quantity");
        }
    }

    private static decimal UnitMultiplier(string unit) => unit.ToLowerInvariant() switch
    {
        "k" => 1000m,
        "m" => 1_000_000m,
        "g" => 1_000_000_000m,
        _ => 1m
    };
}
