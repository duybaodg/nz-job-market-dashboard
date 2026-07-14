namespace Domain.Entities;

public sealed class RawJobPage
{
    public Guid Id { get; set; }
    public string Source { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string? RawHtml { get; set; }
    public string? Markdown { get; set; }
    public string? RawJson { get; set; }
    public DateTime CrawledAt { get; set; }
    public Guid? CrawlRunId { get; set; }
    public CrawlRun? CrawlRun { get; set; }
}
