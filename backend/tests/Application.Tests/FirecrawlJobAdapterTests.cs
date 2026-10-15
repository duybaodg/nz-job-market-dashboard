using Application.Crawling;
using FluentAssertions;
using Infrastructure.Firecrawl;

namespace Application.Tests;

public sealed class FirecrawlJobAdapterTests
{
    private const string Markdown = """
        # Search results

        [Senior .NET Engineer](https://example.com/jobs/1)
        Company: Harbour Systems
        Location: Auckland
        Salary: $125k - $150k
        """;

    [Fact]
    public async Task FetchJobsAsync_refuses_a_robots_disallowed_url_without_calling_firecrawl()
    {
        var client = new RecordingFirecrawlClient();
        var adapter = new FirecrawlJobAdapter(client, new FirecrawlMarkdownParser(), new StubRobotsPolicy(false));

        var act = () => adapter.FetchJobsAsync(
            new JobSearchRequest("Example", "https://example.com/jobs/1"),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("robots.txt does not allow*");
        client.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task FetchJobsAsync_scrapes_and_parses_when_robots_allows()
    {
        var client = new RecordingFirecrawlClient();
        var adapter = new FirecrawlJobAdapter(client, new FirecrawlMarkdownParser(), new StubRobotsPolicy(true));

        var result = await adapter.FetchJobsAsync(
            new JobSearchRequest("Example", "https://example.com/jobs/1"),
            CancellationToken.None);

        client.Calls.Should().ContainSingle().Which.Should().Be("https://example.com/jobs/1");
        result.Jobs.Should().ContainSingle();
        result.Jobs[0].Title.Should().Be("Senior .NET Engineer");
    }

    private sealed class StubRobotsPolicy(bool allowed) : IRobotsTxtPolicy
    {
        public Task<bool> CanFetchAsync(string url, CancellationToken cancellationToken) => Task.FromResult(allowed);
    }

    private sealed class RecordingFirecrawlClient : IFirecrawlClient
    {
        public List<string> Calls { get; } = [];

        public Task<FirecrawlScrapeResult> ScrapeAsync(string url, CancellationToken cancellationToken)
        {
            Calls.Add(url);
            return Task.FromResult(new FirecrawlScrapeResult(
                url,
                Markdown,
                null,
                "<html><body>listing</body></html>",
                "{}"));
        }
    }
}
