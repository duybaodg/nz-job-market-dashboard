using Application.Metrics;
using FluentAssertions;

namespace Application.Tests;

public sealed class SalaryParserTests
{
    [Fact]
    public void Parses_an_anchored_annual_range_as_nzd_year()
    {
        var salary = SalaryParser.Parse("$120,000 - $150,000 per annum", "Software Engineer");

        salary.Min.Should().Be(120_000m);
        salary.Max.Should().Be(150_000m);
        salary.Currency.Should().Be("NZD");
        salary.PayPeriod.Should().Be(SalaryPayPeriod.Year);
        salary.Midpoint.Should().Be(135_000m);
        salary.IsQuestionable.Should().BeFalse();
    }

    [Fact]
    public void Parses_a_single_explicit_amount_as_a_one_sided_figure()
    {
        var salary = SalaryParser.Parse("$150,000", "Data Engineer");

        salary.Min.Should().Be(150_000m);
        salary.Max.Should().BeNull();
        salary.IsSingleAmount.Should().BeTrue();
        salary.Midpoint.Should().Be(150_000m);
        salary.PayPeriod.Should().Be(SalaryPayPeriod.Year);
    }

    [Fact]
    public void Hourly_rates_keep_their_pay_period_and_are_never_annualised()
    {
        var salary = SalaryParser.Parse("$85 per hour", "Contract Developer");

        salary.PayPeriod.Should().Be(SalaryPayPeriod.Hour);
        salary.Min.Should().Be(85m);
        salary.Max.Should().BeNull();
        salary.Midpoint.Should().Be(85m);
    }

    [Fact]
    public void Daily_rates_keep_their_pay_period_and_are_never_annualised()
    {
        var salary = SalaryParser.Parse("$900 per day", "Contract Developer");

        salary.PayPeriod.Should().Be(SalaryPayPeriod.Day);
        salary.Min.Should().Be(900m);
    }

    [Fact]
    public void A_bare_number_pair_without_an_anchor_is_not_a_salary()
    {
        var salary = SalaryParser.Parse("100-160", "Consultant");

        salary.HasValue.Should().BeFalse();
    }

    [Fact]
    public void Non_nzd_currency_is_preserved_but_flagged_for_review()
    {
        var salary = SalaryParser.Parse("AUD 120,000 - AUD 140,000", "Engineer");

        salary.Currency.Should().Be("AUD");
        salary.Min.Should().Be(120_000m);
        salary.IsQuestionable.Should().BeTrue();
        salary.QuestionableReason.Should().Contain("AUD");
    }

    [Fact]
    public void An_implausibly_high_support_role_salary_is_flagged_not_deleted()
    {
        // Reproduces the ZEIL support-role $300,000 filter-widget misparse: the
        // value is kept for investigation but flagged as questionable.
        var salary = SalaryParser.Parse("$300,000", "Tier 2 IT Support Specialist");

        salary.Min.Should().Be(300_000m);
        salary.IsQuestionable.Should().BeTrue();
        salary.QuestionableReason.Should().Contain("support");
    }

    [Fact]
    public void A_high_salary_for_a_senior_technical_title_is_not_flagged()
    {
        var salary = SalaryParser.Parse("$300,000", "Principal Engineer");

        salary.Min.Should().Be(300_000m);
        salary.IsQuestionable.Should().BeFalse();
    }

    [Fact]
    public void An_amount_below_the_annual_floor_is_dropped_and_flagged()
    {
        var salary = SalaryParser.Parse("$25,000", "Intern");

        salary.HasValue.Should().BeFalse();
        salary.IsQuestionable.Should().BeTrue();
        salary.QuestionableReason.Should().Contain("plausible");
    }

    [Fact]
    public void Orders_a_backwards_range()
    {
        var salary = SalaryParser.Parse("$150,000 to $120,000", "Engineer");

        salary.Min.Should().Be(120_000m);
        salary.Max.Should().Be(150_000m);
    }

    [Fact]
    public void Competitive_salary_text_yields_no_value()
    {
        var salary = SalaryParser.Parse("Competitive salary", "Engineer");

        salary.HasValue.Should().BeFalse();
    }
}
