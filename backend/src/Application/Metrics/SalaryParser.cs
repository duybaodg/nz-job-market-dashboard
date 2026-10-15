using System.Globalization;
using System.Text.RegularExpressions;

namespace Application.Metrics;

public enum SalaryPayPeriod
{
    Year,
    Hour,
    Day,
    Unknown
}

/// <summary>
/// The result of parsing an advertised salary string. The numeric bounds are
/// preserved alongside the currency and pay period so a contract hourly rate is
/// never silently annualised or mixed into an annual median.
/// </summary>
public sealed record SalaryParse(
    decimal? Min,
    decimal? Max,
    string Currency,
    SalaryPayPeriod PayPeriod,
    bool IsSingleAmount,
    bool IsQuestionable,
    string? QuestionableReason)
{
    public static readonly SalaryParse Empty = new(null, null, "NZD", SalaryPayPeriod.Unknown, false, false, null);

    public bool HasValue => Min.HasValue || Max.HasValue;

    /// <summary>Midpoint of the advertised bounds, or the single explicit amount.</summary>
    public decimal? Midpoint => HasValue
        ? ((Min ?? Max!.Value) + (Max ?? Min!.Value)) / 2m
        : null;
}

/// <summary>
/// Central advertised-salary parser. Extracted from
/// <c>CrawlerOrchestrationService</c> so ingestion, the API and the dashboard
/// all agree on units, currency and what counts as a usable value.
///
/// Rules:
/// <list type="bullet">
/// <item>A range needs an anchored lower and upper bound; a single anchored
/// amount is preserved as a one-sided advertised figure.</item>
/// <item>Currency is detected (NZD assumed for a bare sign in this NZ-market
/// dataset); non-NZD values are preserved but flagged for review.</item>
/// <item>Hourly and daily rates keep their pay period and are never
/// annualised.</item>
/// <item>Implausible single bounds are dropped and the parse is flagged
/// rather than deleted.</item>
/// </list>
/// </summary>
public static class SalaryParser
{
    // A salary amount must be anchored by a currency symbol/code, an explicit "k"
    // suffix or a fully written 4-6 digit number. The currency-anchored form also
    // accepts small un-grouped figures ("$85") so hourly/daily rates survive.
    private const string CurrencyCode = @"(?:\$|NZD|AUD|USD|GBP|EUR|CAD|INR)";
    private const string AmountPattern =
        CurrencyCode + @"\s*\d{1,3}(?:,\d{3})+(?:\.\d+)?"            // $120,000
        + "|" + CurrencyCode + @"\s*\d{1,4}(?:\.\d+)?\s*[kK]?"      // $85 / $120k / $1000
        + @"|\d{2,3}\s*[kK]"                                          // 130k
        + @"|\d{4,6}";                                                // 120000

    private static readonly Regex RangeRegex = new(
        $@"(?<a>{AmountPattern})\s*(?:-|–|—|to)\s*(?<b>{AmountPattern})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SingleRegex = new(
        $@"(?<a>{AmountPattern})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex HourlyRegex = new(
        @"(?ix)(?:per\s+hour|an?\s+hour|hourly|/\s*hr\b|per\s+day|daily|/\s*day\b)",
        RegexOptions.Compiled);

    private static readonly Regex DailyRegex = new(
        @"(?ix)(?:per\s+day|daily|/\s*day\b)",
        RegexOptions.Compiled);

    private static readonly Regex SupportRoleRegex = new(
        @"(?ix)\b(?:support|service\s+desk|help\s*desk|administrator|administration|coordinator|assistant|receptionist|data\s+entry)\b",
        RegexOptions.Compiled);

    private static readonly Regex NonNzdCurrencyRegex = new(
        @"(?ix)(?:\bAUD\b|A\$|\bUSD\b|US\$|\bGBP\b|£|\bEUR\b|€|\bCAD\b|C\$|\bINR\b|₹)",
        RegexOptions.Compiled);

    private const decimal AnnualMin = 30_000m;
    private const decimal AnnualMax = 500_000m;
    private const decimal HourlyMax = 500m;
    private const decimal DailyMax = 5_000m;

    public static SalaryParse Parse(string? salaryText, string? title = null)
    {
        if (string.IsNullOrWhiteSpace(salaryText))
        {
            return SalaryParse.Empty;
        }

        var text = salaryText.Trim();
        var currency = DetectCurrency(text);
        var period = DetectPayPeriod(text);
        var reasons = new List<string>();

        // A non-NZD figure is still read and preserved, but it is flagged below
        // and excluded from the comparable annual NZD metric.
        decimal? min = null;
        decimal? max = null;
        var isSingle = false;

        var range = RangeRegex.Match(text);
        if (range.Success)
        {
            min = ToSalary(range.Groups["a"].Value);
            max = ToSalary(range.Groups["b"].Value);

            if (min > max)
            {
                (min, max) = (max, min);
            }
        }
        else
        {
            var single = SingleRegex.Match(text);
            if (!single.Success)
            {
                return SalaryParse.Empty with { Currency = currency, PayPeriod = period };
            }

            min = ToSalary(single.Groups["a"].Value);
            isSingle = true;
        }

        var (periodMin, periodMax) = period switch
        {
            SalaryPayPeriod.Hour => (20m, HourlyMax),
            SalaryPayPeriod.Day => (150m, DailyMax),
            _ => (AnnualMin, AnnualMax)
        };

        if (min is not null && (min < periodMin || min > periodMax))
        {
            min = null;
            reasons.Add("lower bound outside the plausible range");
        }

        if (max is not null && (max < periodMin || max > periodMax))
        {
            max = null;
            reasons.Add("upper bound outside the plausible range");
        }

        var questionable = reasons.Count > 0;
        if (!string.Equals(currency, "NZD", StringComparison.OrdinalIgnoreCase))
        {
            questionable = true;
            reasons.Add($"advertised in {currency}, not NZD");
        }

        var midpoint = min.HasValue || max.HasValue
            ? ((min ?? max!.Value) + (max ?? min!.Value)) / 2m
            : (decimal?)null;

        if (period == SalaryPayPeriod.Year
            && midpoint is > 150_000m
            && !string.IsNullOrWhiteSpace(title)
            && SupportRoleRegex.IsMatch(title))
        {
            questionable = true;
            reasons.Add("annual salary is unusually high for a support/administration title");
        }

        return new SalaryParse(
            min,
            max,
            currency,
            period,
            isSingle,
            questionable,
            questionable ? string.Join("; ", reasons) : null);
    }

    private static string DetectCurrency(string text)
    {
        if (NonNzdCurrencyRegex.IsMatch(text))
        {
            if (Regex.IsMatch(text, @"(?ix)(?:\bAUD\b|A\$)")) return "AUD";
            if (Regex.IsMatch(text, @"(?ix)(?:\bUSD\b|US\$)")) return "USD";
            if (Regex.IsMatch(text, @"(?ix)(?:\bGBP\b|£)")) return "GBP";
            if (Regex.IsMatch(text, @"(?ix)(?:\bEUR\b|€)")) return "EUR";
            if (Regex.IsMatch(text, @"(?ix)(?:\bCAD\b|C\$)")) return "CAD";
            if (Regex.IsMatch(text, @"(?ix)(?:\bINR\b|₹)")) return "INR";

            return "Other";
        }

        // The tracked market is New Zealand; a bare "$" defaults to NZD, which is
        // documented rather than silently assumed.
        return "NZD";
    }

    private static SalaryPayPeriod DetectPayPeriod(string text)
    {
        if (DailyRegex.IsMatch(text))
        {
            return SalaryPayPeriod.Day;
        }

        if (HourlyRegex.IsMatch(text))
        {
            return SalaryPayPeriod.Hour;
        }

        return SalaryPayPeriod.Year;
    }

    private static decimal ToSalary(string value)
    {
        // Only an explicit "k" suffix means thousands; a bare "$85" hourly rate
        // must not be inflated to $85,000.
        var isThousands = value.TrimEnd().EndsWith('k') || value.TrimEnd().EndsWith('K');

        // Strip every currency symbol/code and grouping separator; the numeric
        // value is all that matters once the currency has been detected.
        var cleaned = Regex.Replace(value, @"[^0-9.]", string.Empty);

        if (!decimal.TryParse(cleaned, NumberStyles.Number, CultureInfo.InvariantCulture, out var number))
        {
            return 0m;
        }

        return isThousands ? number * 1000 : number;
    }
}
