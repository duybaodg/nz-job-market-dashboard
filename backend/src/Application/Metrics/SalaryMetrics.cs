namespace Application.Metrics;

/// <summary>One listing's advertised-salary evidence, used for the salary metric.</summary>
public sealed record SalaryObservation(
    decimal? Min,
    decimal? Max,
    string? AdvertisedText,
    string Currency,
    SalaryPayPeriod PayPeriod,
    string EmploymentType,
    bool IsQuestionable)
{
    public decimal? Midpoint => Min.HasValue || Max.HasValue
        ? ((Min ?? Max!.Value) + (Max ?? Min!.Value)) / 2m
        : null;
}

/// <summary>
/// Median advertised annual NZD pay for comparable listings, with the sample
/// size, coverage denominator and exclusion breakdown needed to read it honestly.
/// </summary>
public sealed record SalarySummary(
    decimal? MedianAnnualNzd,
    int EligibleCount,
    int CohortCount,
    decimal CoveragePercent,
    int QuestionableCount,
    bool Suppressed,
    bool SmallSample,
    bool Deduplicated,
    IReadOnlyDictionary<string, int> Exclusions,
    string Currency,
    string PayPeriod,
    string Basis);

public static class SalaryMetrics
{
    /// <summary>Product guardrail: suppress groups below this many observations.</summary>
    public const int MinimumSampleSize = 5;

    /// <summary>Product guardrail: flag groups this size or smaller as small samples.</summary>
    public const int SmallSampleSize = 9;

    public static SalarySummary Compute(IEnumerable<SalaryObservation> observations)
    {
        var all = observations.ToList();
        var excluded = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["missing"] = 0,
            ["non-annual"] = 0,
            ["non-nzd"] = 0,
            ["contract-or-internship"] = 0
        };

        var eligible = new List<decimal>();
        var questionable = 0;

        foreach (var observation in all)
        {
            var midpoint = observation.Midpoint;
            if (midpoint is null)
            {
                excluded["missing"]++;
                continue;
            }

            if (observation.PayPeriod != SalaryPayPeriod.Year)
            {
                excluded["non-annual"]++;
                continue;
            }

            if (!string.Equals(observation.Currency, "NZD", StringComparison.OrdinalIgnoreCase))
            {
                excluded["non-nzd"]++;
                continue;
            }

            if (IsContractLike(observation.EmploymentType))
            {
                excluded["contract-or-internship"]++;
                continue;
            }

            eligible.Add(midpoint.Value);
            if (observation.IsQuestionable)
            {
                questionable++;
            }
        }

        eligible.Sort();
        var median = GetMedian(eligible);
        var suppressed = eligible.Count < MinimumSampleSize;
        var coverage = all.Count == 0
            ? 0m
            : Math.Round((decimal)eligible.Count / all.Count * 100m, 1, MidpointRounding.AwayFromZero);

        return new SalarySummary(
            suppressed ? null : median,
            eligible.Count,
            all.Count,
            coverage,
            questionable,
            suppressed,
            eligible.Count is > 0 and <= SmallSampleSize,
            Deduplicated: false,
            excluded,
            "NZD",
            "Year",
            "Median advertised midpoint for comparable permanent annual NZD listings");
    }

    private static bool IsContractLike(string employmentType) =>
        employmentType.Equals("Contract", StringComparison.OrdinalIgnoreCase)
        || employmentType.Equals("Internship", StringComparison.OrdinalIgnoreCase);

    public static decimal? GetMedian(IReadOnlyList<decimal> sortedValues)
    {
        if (sortedValues.Count == 0)
        {
            return null;
        }

        var midpoint = sortedValues.Count / 2;
        return sortedValues.Count % 2 == 0
            ? (sortedValues[midpoint - 1] + sortedValues[midpoint]) / 2
            : sortedValues[midpoint];
    }
}
