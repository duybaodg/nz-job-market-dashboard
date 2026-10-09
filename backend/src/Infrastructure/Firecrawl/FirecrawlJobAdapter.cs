using Application.Crawling;

namespace Infrastructure.Firecrawl;

public sealed class FirecrawlJobAdapter(
    IFirecrawlClient firecrawlClient,
    IFirecrawlMarkdownParser parser)
    : IJobSourceAdapter
{
    public string Method => "Firecrawl";

    public async Task<SourceFetchResult> FetchJobsAsync(
        JobSearchRequest request,
        CancellationToken cancellationToken)
    {
        var scrape = await firecrawlClient.ScrapeAsync(request.Url, cancellationToken);
        var markdown = scrape.Markdown ?? string.Empty;

        var rawPage = new RawJobPageInput(
            request.Source,
            scrape.Url,
            scrape.RawHtml,
            markdown,
            scrape.RawJson,
            DateTime.UtcNow);

        var jobs = parser.Parse(request.Source, scrape.Url, markdown);

        return new SourceFetchResult([rawPage], jobs, false);
    }
}
