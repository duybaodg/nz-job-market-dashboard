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
    /// <summary>The advertised salary text exactly as extracted, kept for provenance.</summary>
    public string? SalaryText { get; set; }
    public string SalaryCurrency { get; set; } = "NZD";
    /// <summary>Year | Hour | Day | Unknown.</summary>
    public string SalaryPeriod { get; set; } = "Unknown";
    public bool SalaryQuestionable { get; set; }
    public string? SalaryQuestionableReason { get; set; }
    public string EmploymentType { get; set; } = "Unknown";
    public string Seniority { get; set; } = "Unknown";
    public string WorkMode { get; set; } = "Unknown";
    public string ExperienceBand { get; set; } = "Unknown";
    public string? Industry { get; set; }
    public string? DescriptionSummary { get; set; }
    /// <summary>
    /// Full text used only for deduplication similarity; retained so a match can
    /// be audited. Never shown in the feed (the summary is).
    /// </summary>
    public string? DescriptionText { get; set; }
    public string Url { get; set; } = string.Empty;
    public string ContentHash { get; set; } = string.Empty;
    public DateTime? PostedDate { get; set; }
    public DateTime? ClosingDate { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime? ExpiredAt { get; set; }
    public int MissingCrawlCount { get; set; }
    public DateTime FirstSeenAt { get; set; }
    public DateTime LastSeenAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    /// <summary>Reviewed canonical employer; null when the name is unmatched (never guessed).</summary>
    public Guid? EmployerCanonicalId { get; set; }
    /// <summary>Soft duplicate grouping; null for a standalone listing.</summary>
    public Guid? DuplicateGroupId { get; set; }
    public ICollection<JobSkill> Skills { get; set; } = new List<JobSkill>();
}
