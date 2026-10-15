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
    string? QuestionableReason,
    bool IsOpenEnded = false,
    string? OpenDirection = null)
{
    public static readonly SalaryParse Empty = new(null, null, "NZD", SalaryPayPeriod.Unknown, false, false, null);

    public bool HasValue => Min.HasValue || Max.HasValue;

    /// <summary>Midpoint of the advertised bounds, or the single explicit amount.</summary>
    public decimal? Midpoint => HasValue
        ? ((Min ?? Max!.Value) + (Max ?? Min!.Value)) / 2m
        : null;

    /// <summary>True when the advertised figure is a contract hourly/daily rate.</summary>
    public bool IsConverted => PayPeriod is SalaryPayPeriod.Hour or SalaryPayPeriod.Day;

    /// <summary>
    /// Documented annualised equivalent (Brief §4): an hourly rate assumes a
    /// 38-hour week over 52 weeks; a day rate assumes a 5-day week over 52
    /// weeks. This is a labelled derived display value only — the original
    /// bounds and pay period are always retained and never overwritten.
    /// </summary>
    public decimal? AnnualEquivalent => PayPeriod switch
    {
        SalaryPayPeriod.Hour when Midpoint is { } hourly => decimal.Round(hourly * SalaryParser.HoursPerWeek * SalaryParser.WeeksPerYear, 2),
        SalaryPayPeriod.Day when Midpoint is { } daily => decimal.Round(daily * SalaryParser.WorkingDaysPerYear, 2),
        SalaryPayPeriod.Year => Midpoint,
        _ => null
    };
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
    private const string CurrencyCode = @"(?:(?:NZ|A|US|C|HK)\$|\$|NZD|AUD|USD|GBP|EUR|CAD|INR)";
    private const string AmountPattern =
        CurrencyCode + @"\s*\d{1,3}(?:,\d{3})+(?:\.\d+)?"            // $120,000
        + "|" + CurrencyCode + @"\s*\d{1,6}(?:\.\d+)?\s*[kK]?"      // $85 / $120k / NZD 55000
        + @"|\d{2,3}\s*[kK]"                                          // 130k
        + @"|\d{4,6}";                                                // 120000

    // Open ranges ("up to", "from", "starting at") must still be anchored by a
    // currency or an explicit "k" so a bare year ("from 2026") cannot be read as
    // a figure. A one-sided bound is preserved but never turned into a range.
    private const string AnchoredAmountPattern =
        CurrencyCode + @"\s*\d{1,3}(?:,\d{3})+(?:\.\d+)?"
        + "|" + CurrencyCode + @"\s*\d{1,6}(?:\.\d+)?\s*[kK]?"
        + @"|\d{2,3}\s*[kK]";

    private static readonly Regex RangeRegex = new(
        $@"(?<a>{AmountPattern})\s*(?:-|–|—|to)\s*(?<b>{AmountPattern})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex OpenRangeRegex = new(
        @"(?ix)(?<dir>up\s+to|no\s+more\s+than|maximum\s+of|from|starting\s+(?:at|from)|at\s+least|minimum\s+of)\s*(?<a>" + AnchoredAmountPattern + ")",
        RegexOptions.Compiled);

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

    public const decimal MinimumAnnual = 30_000m;
    private const decimal AnnualMin = MinimumAnnual;
    private const decimal AnnualMax = 400_000m;
    private const decimal HourlyMin = 20m;
    private const decimal HourlyMax = 200m;
    private const decimal DailyMin = 150m;
    private const decimal DailyMax = 3_000m;

    /// <summary>Documented annualisation assumptions (Brief §4).</summary>
    public const decimal HoursPerWeek = 38m;
    public const decimal WeeksPerYear = 52m;
    public const decimal WorkingDaysPerYear = 260m;

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
        var isOpen = false;
        string? openDirection = null;

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
            var open = OpenRangeRegex.Match(text);
            if (open.Success)
            {
                var amount = ToSalary(open.Groups["a"].Value);
                var direction = open.Groups["dir"].Value.ToLowerInvariant();
                var isCeiling = direction.StartsWith("up") || direction.StartsWith("no more") || direction.StartsWith("maximum");
                if (isCeiling)
                {
                    max = amount;
                    openDirection = "up";
                }
                else
                {
                    min = amount;
                    openDirection = "from";
                }

                isOpen = true;
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
        }

        var (periodMin, periodMax) = period switch
        {
            SalaryPayPeriod.Hour => (HourlyMin, HourlyMax),
            SalaryPayPeriod.Day => (DailyMin, DailyMax),
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
            questionable ? string.Join("; ", reasons) : null,
            isOpen,
            openDirection);
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
