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
    /// <summary>
    /// How the numeric salary bounds were derived: <c>source</c> (advertised
    /// text supplied by the crawl), <c>description</c> (deep text scan) or
    /// <c>ai-enrichment</c>. The advertised text is always retained in
    /// <see cref="SalaryText"/> regardless of the source.
    /// </summary>
    public string? SalarySource { get; set; }
    public string EmploymentType { get; set; } = "Unknown";
    /// <summary>How <see cref="EmploymentType"/> was derived.</summary>
    public string? EmploymentTypeSource { get; set; }
    public string Seniority { get; set; } = "Unknown";
    /// <summary>How <see cref="Seniority"/> was derived: title, description or ai-enrichment.</summary>
    public string? SenioritySource { get; set; }
    public decimal? SeniorityConfidence { get; set; }
    public string WorkMode { get; set; } = "Unknown";
    /// <summary>
    /// How <see cref="WorkMode"/> was derived. Ordered strongest first:
    /// <c>statement</c> (explicit sentence), <c>title</c>, <c>location</c>
    /// (low-confidence inference) and <c>ai-enrichment</c>.
    /// </summary>
    public string? WorkModeSource { get; set; }
    public decimal? WorkModeConfidence { get; set; }
    public string ExperienceBand { get; set; } = "Unknown";
    /// <summary>Model name recorded when the last AI enrichment touched this row.</summary>
    public string? AiEnrichmentModel { get; set; }
    /// <summary>When AI enrichment last evaluated this row (throttle + audit).</summary>
    public DateTime? AiEnrichedAt { get; set; }
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
