using Application.Deduplication;
using Application.Jobs;
using FluentAssertions;

namespace Application.Tests;

public sealed class JobContentValidatorTests
{
    private readonly JobContentValidator _validator = new();

    private static RawJobInput Job(
        string title,
        string url = "https://example.com/jobs/1",
        string? source = "Example",
        string? location = null,
        string? salary = null,
        string? description = null,
        DateTime? postedDate = null,
        string? sourceJobId = null) =>
        new(source ?? "Example", sourceJobId, title, null, location, salary, description, url, postedDate, null);

    [Theory]
    [InlineData("Not found")]
    [InlineData("404 Error: Page Not Found")]
    [InlineData("Page Not Found | Trimble Skip to content")]
    [InlineData("404 - Page not found - Talent International")]
    [InlineData("Error (404) - Pushpay")]
    [InlineData("Page not found – H2R Consulting")]
    [InlineData("Page not found | Alpha Recruitment")]
    public void Rejects_error_page_titles_from_live_data(string title)
    {
        _validator.Validate(Job(title)).IsAccepted.Should().BeFalse();
    }

    [Fact]
    public void Rejects_when_the_fetched_page_title_is_an_error_page()
    {
        var job = Job(
            "Senior Software Engineer",
            url: "https://example.com/jobs/senior-software-engineer",
            location: "Auckland",
            salary: "$120k - $140k");

        _validator.Validate(job).IsAccepted.Should().BeTrue();
        _validator.Validate(job, new JobValidationContext(PageTitle: "404 - Page not found"))
            .IsAccepted.Should().BeFalse();
    }

    [Fact]
    public void Rejects_short_error_page_descriptions_only()
    {
        _validator.Validate(Job("Software Developer", description: "404 Error: Page Not Found"))
            .IsAccepted.Should().BeFalse();

        var longAdvert = Job(
            "Software Developer",
            location: "Wellington",
            salary: "$100k - $120k",
            description: string.Concat(Enumerable.Repeat("Build great products. ", 60)) + "If the API resource is not found, log it.");
        _validator.Validate(longAdvert).IsAccepted.Should().BeTrue();
    }

    [Theory]
    [InlineData("https://www.trademe.co.nz/c/jobs/article/how-to-become-a-civil-structural-engineer-nz")]
    [InlineData("https://example.com/advice/interview-tips")]
    [InlineData("https://example.com/blog/engineering-at-acme")]
    [InlineData("https://example.com/guides/salary-guide")]
    public void Rejects_article_url_paths(string url)
    {
        _validator.Validate(Job("How to get hired", url)).IsAccepted.Should().BeFalse();
    }

    [Fact]
    public void Rejects_article_titles_without_job_signals()
    {
        _validator.Validate(
                Job(
                    "How to become a civil or structural engineer in New Zealand",
                    "https://example.com/careers/how-to-become-an-engineer"))
            .IsAccepted.Should().BeFalse();
    }

    [Fact]
    public void Keeps_article_style_titles_that_carry_job_signals()
    {
        var job = Job(
            "Why you will love this Senior Data Engineer role",
            "https://example.com/jobs/senior-data-engineer",
            location: "Auckland",
            salary: "$130k - $150k",
            description: "Permanent full-time role.");

        _validator.Validate(job).IsAccepted.Should().BeTrue();
    }

    [Theory]
    [InlineData("Recruitment and Executive Search | Tribe Group Find a Job I'm Hiring Executive S")]
    [InlineData("Summer of Tech - empowering New Zealand to hire the next generation of tech talent")]
    [InlineData("People and careers | About Spark | Spark NZ")]
    public void Rejects_live_landing_page_titles(string title)
    {
        _validator.Validate(Job(title)).IsAccepted.Should().BeFalse();
    }

    [Fact]
    public void Rejects_hub_url_equal_to_the_crawl_url()
    {
        var job = Job("Join our team at Example", "https://example.com/careers/");
        _validator.Validate(job, new JobValidationContext(CrawlUrl: "https://example.com/careers/"))
            .IsAccepted.Should().BeFalse();
    }

    [Fact]
    public void Rejects_title_equal_to_source_name()
    {
        _validator.Validate(Job("Fonterra", source: "Fonterra")).IsAccepted.Should().BeFalse();
    }

    [Fact]
    public void Keeps_real_jobs_including_one_missing_a_field()
    {
        var jobs = new[]
        {
            Job("Senior Data Engineer", "https://example.com/jobs/1", location: "Auckland", salary: "$130k - $150k"),
            Job("Senior Software Engineer", "https://example.com/jobs/2", location: "Wellington", salary: "$120k - $140k"),
            Job("Workday Reporting Specialist", "https://example.com/jobs/3", location: "Auckland", salary: "$90k - $110k"),
            Job("IT Help Desk Technician", "https://example.com/jobs/4", location: "Christchurch", salary: "$55k - $65k"),
            Job(
                "Junior React Developer",
                "https://example.com/jobs/5",
                location: "Wellington",
                salary: "$65k - $80k",
                description: "Full-time hybrid role using React and TypeScript."),
            // A real job missing salary and posted date must still save.
            Job("Senior .NET Engineer", "https://example.com/jobs/6", location: "Remote"),
            Job("Careers Advisor", "https://example.com/jobs/7", location: "Hamilton", salary: "$70k - $80k")
        };

        foreach (var job in jobs)
        {
            _validator.Validate(job).IsAccepted.Should().BeTrue(job.Title);
        }
    }
}
