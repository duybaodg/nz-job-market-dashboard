namespace Domain.Entities;

/// <summary>
/// One capture per Pacific/Auckland day. Records the source coverage of that
/// day so a later source addition or removal can be annotated instead of being
/// mistaken for demand growth.
/// </summary>
public sealed class ListingSnapshotRun
{
    public Guid Id { get; set; }
    public DateOnly SnapshotDate { get; set; }
    public DateTime CapturedAtUtc { get; set; }
    public int ListingCount { get; set; }
    public int ActiveCount { get; set; }
    public int SourceCount { get; set; }
    public int IdentifiableEmployerCount { get; set; }
    /// <summary>JSON object of source name → listing count for this snapshot day.</summary>
    public string SourceCoverageJson { get; set; } = "{}";
}
