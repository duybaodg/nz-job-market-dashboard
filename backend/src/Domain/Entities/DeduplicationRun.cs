namespace Domain.Entities;

/// <summary>
/// One auditable run of the listing deduplication pass. "Unique vacancies" are
/// only published once at least one run has completed and passed its integrity
/// validation (<see cref="IsValidated"/>), never from an unverified pass.
/// </summary>
public sealed class DeduplicationRun
{
    public Guid Id { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    /// <summary>Running | Completed | Failed.</summary>
    public string Status { get; set; } = "Running";
    public bool IsValidated { get; set; }
    public string MatchVersion { get; set; } = "v1";
    public string Trigger { get; set; } = "startup";
    public int ListingsEvaluated { get; set; }
    public int GroupsFound { get; set; }
    public int ListingsMerged { get; set; }
    public int UniqueVacancies { get; set; }
    public int ReviewCandidateCount { get; set; }
    public string? Notes { get; set; }
}
