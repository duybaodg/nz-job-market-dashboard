namespace Domain.Entities;

/// <summary>
/// Round 21 (Release B3): a human decision for one unmatched advertised
/// employer name. The queue groups similar names for review but never merges
/// them automatically; only an explicit decision can create an alias. The
/// advertised string is preserved here even when a canonical is chosen.
/// </summary>
public sealed class EmployerAliasReview
{
    public Guid Id { get; set; }
    public string NormalizedName { get; set; } = string.Empty;
    public string AdvertisedName { get; set; } = string.Empty;
    /// <summary><c>matched</c>, <c>kept-separate</c> or <c>agency-client</c>.</summary>
    public string Decision { get; set; } = string.Empty;
    public Guid? CanonicalEmployerId { get; set; }
    public string GroupKey { get; set; } = string.Empty;
    public string? Reason { get; set; }
    public string Reviewer { get; set; } = "admin";
    public DateTime CreatedAt { get; set; }
    public DateTime ReviewedAt { get; set; }
}
