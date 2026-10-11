namespace Application.Crawling;

/// <summary>
/// Job lifecycle policy. The dashboard surfaces 2, 7 and 15 day windows, so a
/// job that has not been seen for <see cref="StaleAfterDays"/> days is expired
/// even when a source never reports it as closed.
/// </summary>
public sealed class CrawlerLifecycleOptions
{
    public const int DefaultStaleAfterDays = 15;

    public const int DefaultRetentionDays = 20;

    public int StaleAfterDays { get; init; } = DefaultStaleAfterDays;

    /// <summary>Consecutive complete crawls a job may be missing before it expires.</summary>
    public int MissingCrawlThreshold { get; init; } = 2;

    /// <summary>
    /// Hard-retention window for crawl data. Jobs and crawl-run history whose
    /// reference timestamp is strictly older than this many days are purged by
    /// the worker (at startup and on every poll). Exactly this many days old is
    /// kept.
    /// </summary>
    public int RetentionDays { get; init; } = DefaultRetentionDays;

    public static CrawlerLifecycleOptions Default { get; } = new();
}
