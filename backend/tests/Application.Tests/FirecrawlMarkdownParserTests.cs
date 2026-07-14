using FluentAssertions;
using Infrastructure.Firecrawl;

namespace Application.Tests;

public sealed class FirecrawlMarkdownParserTests
{
    private readonly FirecrawlMarkdownParser parser = new();

    [Fact]
    public void Parse_returns_linked_job_cards_when_markdown_contains_job_links()
    {
        const string markdown = """
        # Search results

        [Senior .NET Engineer](https://example.com/jobs/1)
        Company: Harbour Systems
        Location: Auckland
        Salary: $125k - $150k

        [Privacy Policy](https://example.com/privacy)
        """;

        var jobs = parser.Parse("Firecrawl", "https://example.com/search", markdown);

        jobs.Should().ContainSingle();
        jobs[0].Title.Should().Be("Senior .NET Engineer");
        jobs[0].Url.Should().Be("https://example.com/jobs/1");
        jobs[0].Location.Should().Be("Auckland");
        jobs[0].SalaryText.Should().Be("$125k - $150k");
    }

    [Fact]
    public void Parse_falls_back_to_single_page_job_from_first_heading()
    {
        const string markdown = """
        # Graduate Cloud Engineer

        Company: Cloud Kiwi
        Location: Hamilton
        Full-time remote role using Azure and Terraform.
        """;

        var jobs = parser.Parse("Firecrawl", "https://example.com/jobs/2", markdown);

        jobs.Should().ContainSingle();
        jobs[0].Title.Should().Be("Graduate Cloud Engineer");
        jobs[0].Company.Should().Be("Cloud Kiwi");
        jobs[0].Location.Should().Be("Hamilton");
        jobs[0].Url.Should().Be("https://example.com/jobs/2");
    }
}
