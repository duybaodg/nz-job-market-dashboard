using Application.Crawling;

namespace Infrastructure.Firecrawl;

public sealed class FirecrawlJobAdapter(
    IFirecrawlClient firecrawlClient,
    IFirecrawlMarkdownParser parser,
    IRobotsTxtPolicy robotsPolicy)
    : IJobSourceAdapter
{
    public string Method => "Firecrawl";

    public async Task<SourceFetchResult> FetchJobsAsync(
        JobSearchRequest request,
        CancellationToken cancellationToken)
    {
        // The self-hosted Firecrawl scrape endpoint does not read robots.txt, so
        // this path refuses a disallowed URL before the request leaves the app.
        // The Python fetch layer reuses crawler/adapters/scrapling/robots.py for
        // the same guarantee; this is the mirror for the direct adapter.
        if (!await robotsPolicy.CanFetchAsync(request.Url, cancellationToken))
        {
            throw new InvalidOperationException(
                $"robots.txt does not allow Firecrawl to fetch {request.Url}.");
        }

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
