namespace Domain.Entities;

public sealed class Job
{
    public Guid Id { get; set; }
    public string Source { get; set; } = string.Empty;
    public string? SourceJobId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Company { get; set; }
    public string? Location { get; set; }
    public string? Region { get; set; }
    public decimal? SalaryMin { get; set; }
    public decimal? SalaryMax { get; set; }
    public string EmploymentType { get; set; } = "Unknown";
    public string Seniority { get; set; } = "Unknown";
    public string WorkMode { get; set; } = "Unknown";
    public string? Industry { get; set; }
    public string? DescriptionSummary { get; set; }
    public string Url { get; set; } = string.Empty;
    public string ContentHash { get; set; } = string.Empty;
    public DateTime? PostedDate { get; set; }
    public DateTime? ClosingDate { get; set; }
    public DateTime FirstSeenAt { get; set; }
    public DateTime LastSeenAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public ICollection<JobSkill> Skills { get; set; } = new List<JobSkill>();
}
