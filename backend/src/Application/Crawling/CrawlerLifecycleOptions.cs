namespace Application.Crawling;

/// <summary>
/// Job lifecycle policy. The dashboard surfaces 2, 7 and 15 day windows, so a
/// job that has not been seen for <see cref="StaleAfterDays"/> days is expired
/// even when a source never reports it as closed.
/// </summary>
public sealed class CrawlerLifecycleOptions
{
    public const int DefaultStaleAfterDays = 15;

    public int StaleAfterDays { get; init; } = DefaultStaleAfterDays;

    /// <summary>Consecutive complete crawls a job may be missing before it expires.</summary>
    public int MissingCrawlThreshold { get; init; } = 2;

    public static CrawlerLifecycleOptions Default { get; } = new();
}
