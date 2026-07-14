using Application.Crawling;

namespace Infrastructure.Firecrawl;

public sealed class FirecrawlJobAdapter(
    IFirecrawlClient firecrawlClient,
    IFirecrawlMarkdownParser parser)
    : IJobSourceAdapter
{
    public string SourceName => "Firecrawl";

    public async Task<SourceFetchResult> FetchJobsAsync(
        JobSearchRequest request,
        CancellationToken cancellationToken)
    {
        var scrape = await firecrawlClient.ScrapeAsync(request.Url, cancellationToken);
        var markdown = scrape.Markdown ?? string.Empty;

        var rawPage = new RawJobPageInput(
            SourceName,
            scrape.Url,
            scrape.RawHtml,
            markdown,
            scrape.RawJson,
            DateTime.UtcNow);

        var jobs = parser.Parse(SourceName, scrape.Url, markdown);

        return new SourceFetchResult(rawPage, jobs);
    }
}
