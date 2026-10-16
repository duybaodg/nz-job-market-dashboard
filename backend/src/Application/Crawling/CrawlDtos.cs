using Application.Deduplication;

namespace Application.Crawling;

public sealed record StartCrawlRunRequest(string Source, string Url);

public sealed record JobSearchRequest(string Source, string Url, string Parser = "generic");

public sealed record RawJobPageInput(
    string Source,
    string Url,
    string? RawHtml,
    string? Markdown,
    string? RawJson,
    DateTime CrawledAt);

public sealed record SourceFetchResult(
    IReadOnlyList<RawJobPageInput> RawPages,
    IReadOnlyList<RawJobInput> Jobs,
    bool IsComplete = false,
    CrawlRunStats? Stats = null);

/// <summary>
/// Candidate/kept counters returned by the Python worker. They let the
/// orchestration log a clear "0 IT roles of N total" line for a healthy source
/// whose board currently holds no technology roles (the Round 4 Group F ask),
/// so an empty source is never mistaken for a crawl failure.
/// </summary>
public sealed record CrawlRunStats(int Candidates, int Kept);

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

public sealed record JobSourceDto(
    Guid Id,
    string Name,
    string? BaseUrl,
    string Method,
    string Parser,
    bool Enabled,
    int CrawlFrequencyMinutes,
    DateTime? LastCrawledAt,
    DateTime? TermsCheckedAt,
    DateTime? RetiredAt);
