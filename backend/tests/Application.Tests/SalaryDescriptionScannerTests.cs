using Application.Metrics;
using FluentAssertions;

namespace Application.Tests;

public sealed class SalaryDescriptionScannerTests
{
    [Fact]
    public void Finds_an_annual_range_in_a_description()
    {
        var result = SalaryDescriptionScanner.Scan(
            "This role offers $120,000 - $150,000 per annum plus benefits.", "Software Engineer");

        result.Should().NotBeNull();
        result!.Parse.Min.Should().Be(120_000m);
        result.Parse.Max.Should().Be(150_000m);
        result.Parse.PayPeriod.Should().Be(SalaryPayPeriod.Year);
    }

    [Fact]
    public void Finds_a_hourly_rate_in_a_description()
    {
        var result = SalaryDescriptionScanner.Scan(
            "Project Services $50-$60 per hour depending on experience.", "Project Coordinator");

        result.Should().NotBeNull();
        result!.Parse.PayPeriod.Should().Be(SalaryPayPeriod.Hour);
        result.Parse.Min.Should().Be(50m);
        result.Parse.Max.Should().Be(60m);
    }

    [Fact]
    public void Finds_an_unanchored_grouped_annual_range_without_a_pay_word()
    {
        var result = SalaryDescriptionScanner.Scan(
            "Salary (NZD) $120000 - $125000 Permanent Job type Testing", "QA Engineer");

        result.Should().NotBeNull();
        result!.Parse.Min.Should().Be(120_000m);
        result.Parse.Max.Should().Be(125_000m);
    }

    [Fact]
    public void An_open_range_stays_one_sided()
    {
        var result = SalaryDescriptionScanner.Scan(
            "The salary for this role is up to $150,000 depending on experience.", "Engineer");

        result.Should().NotBeNull();
        result!.Parse.Max.Should().Be(150_000m);
        result.Parse.Min.Should().BeNull();
    }

    [Fact]
    public void A_referral_bonus_is_not_a_salary()
    {
        SalaryDescriptionScanner.Scan(
            "Get $1,000 for a successful referral! Refer someone to our team.", "Recruiter")
            .Should().BeNull();
    }

    [Fact]
    public void A_reserve_price_is_not_a_salary()
    {
        SalaryDescriptionScanner.Scan(
            "Marketplace Latest deals Stores Closing soon $1 reserve Home & Living Property", "Engineer")
            .Should().BeNull();
    }

    [Fact]
    public void A_date_is_not_a_salary()
    {
        SalaryDescriptionScanner.Scan(
            "Applications close on 2026 and the role starts 2027.", "Engineer")
            .Should().BeNull();
    }

    [Fact]
    public void Company_revenue_is_not_a_salary()
    {
        SalaryDescriptionScanner.Scan(
            "Annually, we turn $150m of wood fibre into $450m of value-added products.", "Manager")
            .Should().BeNull();
    }

    [Fact]
    public void The_zeil_salary_filter_slider_is_ignored()
    {
        var description = "Edit Advanced Filters Location Work Type Work Style Role Level Salary "
            + "Work Style On Site Remote Hybrid Role Level Intern Entry Level Associate "
            + "Salary Annual Wage $0 $300,000+ $0 $150+ Job Schedule 8 Hour Shift";

        SalaryDescriptionScanner.Scan(description, "IT Support Specialist").Should().BeNull();
    }

    [Fact]
    public void Finds_a_currency_coded_jsonld_style_range()
    {
        var result = SalaryDescriptionScanner.Scan(
            "summary auckland, auckland NZ$130,000 - NZ$155,000 per year, Hybrid, Modern Cloud Stack", "Data Engineer");

        result.Should().NotBeNull();
        result!.Parse.Min.Should().Be(130_000m);
        result.Parse.Max.Should().Be(155_000m);
    }

    [Fact]
    public void Another_jobs_sidebar_row_is_not_this_jobs_salary()
    {
        // A recruitment-site job list: the dollars belong to other vacancies.
        var description = "Salary Annual salary Hourly rate Reset Filter "
            + "Reporting Analyst - SAP Wellington Central Contracting $103836 - $120224 per annum View details 14 Oct 2026 "
            + "Senior GIS Analyst Wellington Central Permanent $95000 - $102000 per annum View details 08 Oct 2026 "
            + "Setup Job Alerts Hear about our latest jobs";

        SalaryDescriptionScanner.Scan(description, "Head of Digital & Technology").Should().BeNull();
    }

    [Fact]
    public void A_decimal_thousands_rate_is_not_read_from_its_fraction()
    {
        // Round 11: "$87.62 k/yr" must not be read as "62 k" / NZD 62,000; the
        // real "$87,624 ... per year" later in the text is what should win.
        var result = SalaryDescriptionScanner.Scan(
            "Starting at 87.62 k/yr The salary will begin at $87,624 pro-rata, per year.", "Specialist");

        result.Should().NotBeNull();
        result!.Parse.Min.Should().Be(87_624m);
    }

    [Fact]
    public void A_file_size_is_not_a_salary()
    {
        SalaryDescriptionScanner.Scan(
            "Digital Workspace Engineer.pdf (PDF, 198KB) Job Description File links:", "Engineer")
            .Should().BeNull();
    }

    [Fact]
    public void A_headline_quantity_is_not_a_salary()
    {
        SalaryDescriptionScanner.Scan(
            "Day-to-day management of a website with over 500k users.", "Content Manager")
            .Should().BeNull();
    }

    [Fact]
    public void A_salary_in_the_jobs_own_header_is_still_found()
    {
        var description = "Search for: View IT Jobs Senior GIS Analyst Auckland Central "
            + "$95000 - $102000 per annum Full Time Permanent Apply now 08 Oct 2026 "
            + "A bit about us Ashburton District Council is an innovative organisation.";

        var result = SalaryDescriptionScanner.Scan(description, "Senior GIS Analyst");

        result.Should().NotBeNull();
        result!.Parse.Min.Should().Be(95_000m);
        result.Parse.Max.Should().Be(102_000m);
    }
}
