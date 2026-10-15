namespace Domain.Entities;

/// <summary>
/// A pair of listings that look like they may be the same vacancy (same
/// normalised title and canonical employer) but do not meet the strong-evidence
/// merge gates. Recorded for human review instead of being merged.
/// </summary>
public sealed class DuplicateReviewCandidate
{
    public Guid Id { get; set; }
    public Guid JobId { get; set; }
    public Guid? CandidateJobId { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string? Title { get; set; }
    public string? Employer { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ResolvedAt { get; set; }
}
