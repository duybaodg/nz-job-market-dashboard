namespace Domain.Entities;

public sealed class JobSource
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? BaseUrl { get; set; }
    public string Method { get; set; } = string.Empty;
    public string Parser { get; set; } = "generic";
    public bool Enabled { get; set; } = true;
    public int CrawlFrequencyMinutes { get; set; } = 1440;
    public DateTime? LastCrawledAt { get; set; }
    public DateTime? TermsCheckedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
