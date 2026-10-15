namespace Domain.Entities;

/// <summary>
/// A soft grouping of cross-board listings of the same vacancy. Grouping never
/// deletes a source row: <see cref="Job.DuplicateGroupId"/> is the only link,
/// and <see cref="CanonicalListingId"/> always points at a live member (the
/// earliest first_seen_at, preferring a source with a posted date).
/// </summary>
public sealed class DuplicateGroup
{
    public Guid Id { get; set; }
    public Guid CanonicalListingId { get; set; }
    public string MatchMethod { get; set; } = string.Empty;
    public string MatchVersion { get; set; } = "v1";
    public string Confidence { get; set; } = "high";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public int MemberCount { get; set; }
}
