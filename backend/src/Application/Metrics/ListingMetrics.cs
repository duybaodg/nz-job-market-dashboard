namespace Application.Metrics;

/// <summary>Which date field a "new listing" count is based on.</summary>
public enum DateBasis
{
    /// <summary>A trustworthy source-posted date (a missing posted date is excluded).</summary>
    Posted,

    /// <summary>The crawler's first-seen timestamp (discovery).</summary>
    Discovered
}

public sealed record ListingDateObservation(DateTime? PostedDate, DateTime FirstSeenAt);

public sealed record ListingCount(
    int Count,
    DateBasis Basis,
    int WithPostedDate,
    int MissingPostedDate,
    int CohortCount,
    ReportingInterval Interval)
{
    /// <summary>Share of the cohort that carries a trustworthy posted date.</summary>
    public decimal PostedDateCoveragePercent => CohortCount == 0
        ? 0m
        : Math.Round((decimal)WithPostedDate / CohortCount * 100m, 1, MidpointRounding.AwayFromZero);
}

public static class ListingMetrics
{
    /// <summary>
    /// Count listings in the interval for the requested date basis. A
    /// <see cref="DateBasis.Posted"/> count never falls back to first-seen: the
    /// label and the number must describe the same field.
    /// </summary>
    public static ListingCount Count(
        IEnumerable<ListingDateObservation> listings,
        ReportingInterval interval,
        DateBasis basis)
    {
        var cohort = listings.ToList();
        var withPosted = cohort.Count(listing => listing.PostedDate.HasValue);

        var count = cohort.Count(listing => basis switch
        {
            DateBasis.Posted => listing.PostedDate is { } posted && interval.Contains(posted),
            _ => interval.Contains(listing.FirstSeenAt)
        });

        return new ListingCount(
            count,
            basis,
            withPosted,
            cohort.Count - withPosted,
            cohort.Count,
            interval);
    }
}
