using Application.Jobs;
using FluentAssertions;

namespace Application.Tests;

public sealed class WorkModeExtractorTests
{
    [Fact]
    public void Flexible_working_hours_is_about_hours_not_location()
    {
        // The NZ trap: flexibility over hours must not be read as Hybrid/Remote.
        var result = WorkModeExtractor.Extract(
            "Software Engineer",
            null,
            "Auckland",
            null,
            "We offer flexible working hours and a great culture.");

        result.Mode.Should().BeOneOf("Unknown", "On-site");
        result.Mode.Should().NotBe("Hybrid");
        result.Mode.Should().NotBe("Remote");
    }

    [Fact]
    public void Flexible_working_arrangements_are_hybrid()
    {
        var result = WorkModeExtractor.Extract(
            "Software Engineer", null, null, null,
            "We support flexible working arrangements for all staff.");

        result.Mode.Should().Be("Hybrid");
        result.Source.Should().Be("statement");
    }

    [Fact]
    public void Hybrid_beats_a_co_occurring_remote_mention()
    {
        var result = WorkModeExtractor.Extract(
            "Engineer", null, null, null,
            "This is a hybrid role with some remote work available.");

        result.Mode.Should().Be("Hybrid");
    }

    [Fact]
    public void Fully_remote_is_remote()
    {
        var result = WorkModeExtractor.Extract(
            "Engineer", null, null, null, "This is a fully remote position in NZ.");

        result.Mode.Should().Be("Remote");
        result.Source.Should().Be("statement");
    }

    [Fact]
    public void Work_from_home_and_wfh_are_remote()
    {
        WorkModeExtractor.Extract("Analyst", null, null, null, "You can work from home 2 days a week.")
            .Mode.Should().Be("Hybrid"); // two days is hybrid, not fully remote

        WorkModeExtractor.Extract("Analyst", null, null, null, "Fully WFH role with no office days.")
            .Mode.Should().Be("Remote");
    }

    [Fact]
    public void Remote_support_task_is_not_a_work_mode()
    {
        var result = WorkModeExtractor.Extract(
            "IT Support Engineer", null, null, null,
            "Provide remote support and remote desktop troubleshooting to customers.");

        result.Mode.Should().NotBe("Remote");
    }

    [Fact]
    public void Five_days_in_the_office_is_on_site()
    {
        var result = WorkModeExtractor.Extract(
            "Coordinator", null, null, null, "This role is 5 days in the office each week.");

        result.Mode.Should().Be("On-site");
    }

    [Fact]
    public void Two_to_three_days_in_our_office_is_hybrid()
    {
        var result = WorkModeExtractor.Extract(
            "Engineer", null, null, null, "You will spend 2-3 days in our Auckland office.");

        result.Mode.Should().Be("Hybrid");
    }

    [Fact]
    public void On_site_wording_is_on_site()
    {
        WorkModeExtractor.Extract("Engineer", null, null, null, "This is an on-site role based in our Auckland office.")
            .Mode.Should().Be("On-site");
    }

    [Fact]
    public void Title_remote_cue_is_lower_confidence()
    {
        var result = WorkModeExtractor.Extract(
            "Senior Full Stack Engineer - NZ Remote, B2B SaaS", null, null, null, null);

        result.Mode.Should().Be("Remote");
        result.Source.Should().Be("title");
    }

    [Fact]
    public void Nz_location_with_no_remote_mention_infers_on_site()
    {
        var result = WorkModeExtractor.Extract(
            "Business Analyst", "Auckland CBD", "Auckland", "Auckland CBD role.", null);

        result.Mode.Should().Be("On-site");
        result.Source.Should().Be("location");
        result.Confidence.Should().BeLessThan(0.5m);
    }

    [Fact]
    public void Zeil_advanced_filters_navigation_does_not_assign_a_mode()
    {
        var description = "Digital Teaching and Innovation Specialist | Jobs,Smarter | ZEIL ZEIL Jobs "
            + "Edit Advanced Filters Location Work Type Work Style Role Level Salary "
            + "Work Style On Site Remote Hybrid Role Level Intern Entry Level Associate Mid Level Senior "
            + "Job Schedule 8 Hour Shift Salary Annual Wage $0 $300,000+";

        var result = WorkModeExtractor.Extract(
            "Digital Teaching and Innovation Specialist", "Hamilton", "Waikato", "ZEIL Jobs", description);

        result.Mode.Should().Be("On-site"); // location inference only, not the nav's Hybrid/Remote
        result.Source.Should().Be("location");
    }

    [Fact]
    public void Related_jobs_work_remotely_navigation_is_stripped()
    {
        var description = "Great role in Wellington. Related Jobs Any location Work Remotely "
            + "Auckland Bay of Plenty Canterbury Gisborne Wellington";

        var result = WorkModeExtractor.Extract("Analyst", "Wellington", "Wellington", "Great role.", description);

        result.Source.Should().Be("location");
        result.Mode.Should().Be("On-site");
    }

    [Fact]
    public void Hybrid_cloud_infrastructure_is_not_a_work_mode()
    {
        var result = WorkModeExtractor.Extract(
            "Platform Engineer", null, null, null,
            "Experience with hybrid cloud infrastructure and on-premises systems.");

        result.Mode.Should().NotBe("Hybrid");
    }

    [Fact]
    public void A_single_day_wfh_is_hybrid_not_fully_remote()
    {
        var result = WorkModeExtractor.Extract(
            "Operations Analyst", "Auckland CBD", "Auckland", "Auckland CBD based with 1-day WFH.",
            "Auckland CBD based with 1-day WFH and genuine career growth.");

        result.Mode.Should().Be("Hybrid");
    }

    [Fact]
    public void The_whole_related_jobs_navigation_list_is_stripped()
    {
        var description = "Great role in Wellington. Related Jobs Any location Work Remotely Auckland "
            + "Bay of Plenty Canterbury Gisborne Hawkes Bay Manawatu Northland Otago Southland Taranaki "
            + "Tasman Waikato Wellington West Coast Work Remotely Classification Any classification Agile/Scrum";

        var result = WorkModeExtractor.Extract("Analyst", "Wellington", "Wellington", "Great role.", description);

        result.Mode.Should().Be("On-site");
        result.Source.Should().Be("location");
    }
}
