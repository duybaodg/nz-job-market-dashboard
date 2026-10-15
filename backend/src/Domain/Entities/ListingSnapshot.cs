namespace Domain.Entities;

/// <summary>
/// One listing's membership and attributes on one Pacific/Auckland calendar day.
/// Snapshots are self-contained (no FK to <c>jobs</c>) and are never deleted by
/// the 20-day job retention purge, so they preserve the evidence a later
/// migration or a deleted listing would otherwise destroy.
/// </summary>
public sealed class ListingSnapshot
{
    public Guid Id { get; set; }
    public DateOnly SnapshotDate { get; set; }
    public Guid JobId { get; set; }
    public string Source { get; set; } = string.Empty;
    public string SourceCategory { get; set; } = "unknown";
    public string ContentHash { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Company { get; set; }
    public string? HiringEmployer { get; set; }
    public bool AgencyAdvertised { get; set; }
    public string? Location { get; set; }
    public string? Region { get; set; }
    public string WorkMode { get; set; } = "Unknown";
    public string Seniority { get; set; } = "Unknown";
    public string EmploymentType { get; set; } = "Unknown";
    public string RoleFamily { get; set; } = "Other / Unknown";
    public decimal? SalaryMin { get; set; }
    public decimal? SalaryMax { get; set; }
    public string SalaryCurrency { get; set; } = "NZD";
    public string SalaryPeriod { get; set; } = "Unknown";
    public DateTime? PostedDate { get; set; }
    public DateTime FirstSeenAt { get; set; }
    public DateTime LastSeenAt { get; set; }
    public bool IsActive { get; set; }
    public DateTime CapturedAtUtc { get; set; }
}
