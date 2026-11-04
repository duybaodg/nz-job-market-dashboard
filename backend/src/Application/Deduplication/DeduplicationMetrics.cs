namespace Application.Deduplication;

/// <summary>
/// The coherent, single-pass population behind the published vacancy headline.
/// A "listing" is one active source row; a "vacancy group" is a validated soft
/// group of two or more listings; a "duplicate row" is a member beyond the
/// group's canonical listing.
/// </summary>
public sealed record DeduplicationPopulation(
    int ActiveListings,
    int Groups,
    int ListingsInGroups,
    int ListingsMerged,
    int UniqueVacancies)
{
    /// <summary>
    /// For a complete population partitioned into vacancy groups,
    /// vacancies = listings − sum(group_size − 1). Groups with fewer than two
    /// live members are not vacancy groups and contribute no merge.
    /// </summary>
    public static DeduplicationPopulation FromActiveGroupSizes(
        int activeListings,
        IEnumerable<int> activeGroupSizes)
    {
        if (activeListings < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(activeListings));
        }

        var validSizes = activeGroupSizes.Where(size => size >= 2).ToList();
        var groups = validSizes.Count;
        var listingsInGroups = validSizes.Sum();
        var listingsMerged = validSizes.Sum(size => size - 1);
        var uniqueVacancies = Math.Max(0, activeListings - listingsMerged);

        return new DeduplicationPopulation(
            activeListings,
            groups,
            listingsInGroups,
            listingsMerged,
            uniqueVacancies);
    }
}
