namespace Domain.Entities;

public sealed class CrawlRun
{
    public Guid Id { get; set; }
    public string Source { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTime StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public int TotalPages { get; set; }
    public int TotalJobsFound { get; set; }
    public int TotalJobsSaved { get; set; }
    public string? ErrorMessage { get; set; }
    public ICollection<RawJobPage> RawPages { get; set; } = new List<RawJobPage>();
}
