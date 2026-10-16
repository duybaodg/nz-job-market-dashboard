namespace Domain.Entities;

public sealed class JobSource
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? BaseUrl { get; set; }
    public string Method { get; set; } = string.Empty;
    public string Parser { get; set; } = "generic";
    /// <summary>Reviewed registry category: company | government | recruiter | job-board | aggregator | graduate.</summary>
    public string Category { get; set; } = "unknown";
    public bool Enabled { get; set; } = true;
    public int CrawlFrequencyMinutes { get; set; } = 1440;
    public DateTime? LastCrawledAt { get; set; }
    public DateTime? TermsCheckedAt { get; set; }
    /// <summary>
    /// When the source was removed from the registry. Null means the source is
    /// part of the current registry (even if it is disabled there). Retired rows
    /// are kept so historical jobs keep a source label, but they are excluded
    /// from the crawler-health report and the admin source list.
    /// </summary>
    public DateTime? RetiredAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
