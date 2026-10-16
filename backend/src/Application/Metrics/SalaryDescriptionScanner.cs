using System.Text.RegularExpressions;
using Application.Jobs;

namespace Application.Metrics;

/// <summary>An advertised figure found in the retained full description text.</summary>
public sealed record DescriptionSalary(SalaryParse Parse, string MatchedText);

/// <summary>
/// Conservative salary scanner for the retained full job description.
///
/// The crawl-time advertised-salary field is always preferred; this scanner only
/// runs for listings that have no source salary. It is deliberately stricter
/// than <see cref="SalaryParser.Parse"/> because a raw description contains
/// numbers that are not pay (dates, revenue, referral bonuses, filter sliders):
/// <list type="bullet">
/// <item>a candidate must be currency-anchored ("$120,000", "NZD 130k") or an
/// explicit "k" figure — bare 4-digit years are never candidates;</item>
/// <item>the surrounding sentence must carry a pay word ("per annum", "salary",
/// "hourly", "rate") unless it is an explicit annual range;</item>
/// <item>non-pay contexts (referral, reserve, revenue, bonus, slider widgets)
/// are rejected, and navigation/filter boilerplate is stripped first.</item>
/// </list>
/// </summary>
public static class SalaryDescriptionScanner
{
    private const string Currency = @"(?:(?:NZ|A|US|C|HK)\$|\$|NZD|AUD|USD|GBP|EUR|CAD|INR)";
    private const string Money =
        @"(?:(?:" + Currency + @")\s?\d{1,3}(?:,\d{3})+(?:\.\d+)?"
        + @"|(?:" + Currency + @")\s?\d{1,6}(?:\.\d+)?\s?[kK]?"
        + @"|(?<![\w.])\d{2,3}\s?[kK](?![a-zA-Z0-9]))";

    private static readonly Regex Candidates = new(
        $@"(?<range>(?<a>{Money})\s*(?:-|–|—|\bto\b)\s*(?<b>{Money}))|(?<single>{Money})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex PayContext = new(
        @"(?ix)
        per\s+annum | per\s+year | \bp\.?a\.?\b | per\s+hour | hourly | p/?h\b | hrly | per\s+hr\b
        | per\s+day | daily | day\s+rate | hourly\s+rate | contract\s+rate | rate\s+of\s+pay
        | salary | remuneration | remunerat | annual\s+(?:salary|package|base|wage|remuneration)
        | base\s+salary | pay\s+range | salary\s+band | package | compensation | total\s+remuneration | \bOTE\b
        ",
        RegexOptions.Compiled | RegexOptions.IgnorePatternWhitespace);

    private static readonly Regex RejectContext = new(
        @"(?ix)\b(?:referral|sign[\s-]?on|reserve|prize|voucher|gift|scholarship|discount|revenue|turnover|budget|funding|investment|valuation|price|fee|fine|grant|donation|savings|cashback)\b",
        RegexOptions.Compiled);

    // Navigation/job-list chrome is rejected even when a pay word is present:
    // a sidebar row ("... $1000 + GST per day View details") is another job's
    // salary, not this one's.
    private static readonly Regex NavContext = new(
        @"(?ix)reset\s+filter|view\s+details|setup\s+job\s+alerts|job\s+alerts|search\s+\d+\s+jobs",
        RegexOptions.Compiled);

    private static readonly Regex FilterWidget = new(
        @"(?ix)(?:annual\s+wage|salary\s+range|salary\s+filter|work\s+style|career\s+field)\s*$",
        RegexOptions.Compiled);

    private static readonly Regex Millions = new(
        @"(?ix)^\s*(?:\$|NZD|AUD|USD|GBP|EUR)?\s*\d[\d,\.]*\s*(?:m|million|b|billion|bn)\b",
        RegexOptions.Compiled);

    public static DescriptionSalary? Scan(string? text, string? title)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var clean = FieldTextSanitizer.Sanitize(text);
        if (string.IsNullOrWhiteSpace(clean))
        {
            return null;
        }

        DescriptionSalary? best = null;
        var bestScore = double.MinValue;

        foreach (Match match in Candidates.Matches(clean))
        {
            var matched = match.Value.Trim();
            if (string.IsNullOrWhiteSpace(matched) || Millions.IsMatch(matched))
            {
                continue;
            }

            var windowStart = Math.Max(0, match.Index - 160);
            var windowEnd = Math.Min(clean.Length, match.Index + match.Length + 160);
            var window = clean[windowStart..windowEnd];

            // "up to"/"from" prefix and the following pay-period words so the
            // parser can keep an open range open and read "per hour"/"per day".
            var prefixStart = Math.Max(0, match.Index - 18);
            var prefix = clean[prefixStart..match.Index];
            var suffixEnd = Math.Min(clean.Length, match.Index + match.Length + 24);
            var suffix = clean[(match.Index + match.Length)..suffixEnd];
            if (FilterWidget.IsMatch(prefix))
            {
                continue;
            }

            var hasPayContext = PayContext.IsMatch(window);
            if (NavContext.IsMatch(window))
            {
                continue;
            }

            if (!hasPayContext && RejectContext.IsMatch(window))
            {
                continue;
            }

            var parse = SalaryParser.Parse(prefix + matched + suffix, title);
            if (!parse.HasValue)
            {
                continue;
            }

            // Without a pay word, only an explicit annual range is accepted.
            var strongRange = parse.Min.HasValue && parse.Max.HasValue
                && parse.PayPeriod == SalaryPayPeriod.Year
                && parse.Min >= SalaryParser.MinimumAnnual
                && parse.Max >= SalaryParser.MinimumAnnual;
            if (!hasPayContext && !strongRange)
            {
                continue;
            }

            var score = (parse.PayPeriod == SalaryPayPeriod.Year ? 100 : 0)
                + (parse.Min.HasValue && parse.Max.HasValue ? 20 : 0)
                + (hasPayContext ? 10 : 0)
                - match.Index / 100_000.0;

            if (score > bestScore)
            {
                bestScore = score;
                best = new DescriptionSalary(parse, matched);
            }
        }

        return best;
    }
}
