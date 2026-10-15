using Application.Metrics;
using FluentAssertions;

namespace Application.Tests;

public sealed class SalaryMetricsTests
{
    private static SalaryObservation Annual(decimal? min, decimal? max, string employmentType = "Full-time", bool questionable = false) =>
        new(min, max, $"${min}-${max}", "NZD", SalaryPayPeriod.Year, employmentType, questionable);

    [Fact]
    public void Median_uses_midpoints_of_eligible_annual_nzd_listings()
    {
        var summary = SalaryMetrics.Compute(
        [
            Annual(100_000m, 120_000m),  // 110k
            Annual(140_000m, 160_000m),  // 150k -> median
            Annual(200_000m, 220_000m),  // 210k
            Annual(300_000m, 340_000m),  // 320k
            Annual(80_000m, 100_000m)    // 90k
        ]);

        summary.MedianAnnualNzd.Should().Be(150_000m);
        summary.EligibleCount.Should().Be(5);
        summary.CohortCount.Should().Be(5);
        summary.CoveragePercent.Should().Be(100m);
        summary.Suppressed.Should().BeFalse();
        summary.SmallSample.Should().BeTrue();
    }

    [Fact]
    public void Coverage_numerator_and_denominator_are_reported_with_exclusions()
    {
        var observations = new List<SalaryObservation>
        {
            Annual(120_000m, 140_000m),
            Annual(120_000m, 140_000m),
            Annual(120_000m, 140_000m),
            Annual(120_000m, 140_000m),
            Annual(120_000m, 140_000m),
            // Excluded: hourly contract rate.
            new(85m, null, "$85 per hour", "NZD", SalaryPayPeriod.Hour, "Contract", false),
            // Excluded: no salary at all.
            new(null, null, null, "NZD", SalaryPayPeriod.Unknown, "Full-time", false),
            // Excluded: foreign currency.
            new(120_000m, 140_000m, "AUD 120,000", "AUD", SalaryPayPeriod.Year, "Full-time", true),
            // Excluded: contract paid annually.
            Annual(150_000m, 170_000m, "Contract")
        };

        var summary = SalaryMetrics.Compute(observations);

        summary.EligibleCount.Should().Be(5);
        summary.CohortCount.Should().Be(9);
        summary.CoveragePercent.Should().Be(55.6m);
        summary.Exclusions["non-annual"].Should().Be(1);
        summary.Exclusions["missing"].Should().Be(1);
        summary.Exclusions["non-nzd"].Should().Be(1);
        summary.Exclusions["contract-or-internship"].Should().Be(1);
        summary.Suppressed.Should().BeFalse();
        summary.SmallSample.Should().BeTrue();
    }

    [Fact]
    public void Questionable_parses_are_included_but_counted_for_investigation()
    {
        var summary = SalaryMetrics.Compute(
        [
            Annual(120_000m, 140_000m),
            Annual(120_000m, 140_000m),
            Annual(120_000m, 140_000m),
            Annual(120_000m, 140_000m),
            Annual(300_000m, null, questionable: true)
        ]);

        summary.EligibleCount.Should().Be(5);
        summary.QuestionableCount.Should().Be(1);
        summary.MedianAnnualNzd.Should().Be(130_000m);
    }

    [Fact]
    public void A_single_explicit_amount_forms_a_one_sided_midpoint()
    {
        var summary = SalaryMetrics.Compute(
        [
            new(150_000m, null, "$150,000", "NZD", SalaryPayPeriod.Year, "Full-time", false),
            new(120_000m, null, "$120,000", "NZD", SalaryPayPeriod.Year, "Full-time", false),
            new(135_000m, null, "$135,000", "NZD", SalaryPayPeriod.Year, "Full-time", false),
            new(160_000m, null, "$160,000", "NZD", SalaryPayPeriod.Year, "Full-time", false),
            new(110_000m, null, "$110,000", "NZD", SalaryPayPeriod.Year, "Full-time", false)
        ]);

        summary.MedianAnnualNzd.Should().Be(135_000m);
        summary.EligibleCount.Should().Be(5);
    }

    [Fact]
    public void Empty_cohort_is_not_a_median_of_zero()
    {
        var summary = SalaryMetrics.Compute([]);

        summary.MedianAnnualNzd.Should().BeNull();
        summary.CoveragePercent.Should().Be(0m);
        summary.Suppressed.Should().BeTrue();
    }
}
