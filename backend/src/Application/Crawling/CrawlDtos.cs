using Application.Deduplication;

namespace Application.Crawling;

public sealed record StartCrawlRunRequest(string Source, string Url);

public sealed record JobSearchRequest(string Source, string Url);

public sealed record RawJobPageInput(
    string Source,
    string Url,
    string? RawHtml,
    string? Markdown,
    string? RawJson,
    DateTime CrawledAt);

public sealed record SourceFetchResult(
    RawJobPageInput RawPage,
    IReadOnlyList<RawJobInput> Jobs);

public sealed record CrawlRunDto(
    Guid Id,
    string Source,
    string Status,
    DateTime StartedAt,
    DateTime? FinishedAt,
    int TotalPages,
    int TotalJobsFound,
    int TotalJobsSaved,
    string? ErrorMessage);

public sealed record CrawlRunResultDto(
    CrawlRunDto CrawlRun,
    bool Started,
    string Message);
