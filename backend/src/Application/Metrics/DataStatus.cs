namespace Application.Metrics;

public enum DataFreshnessState
{
    Fresh,
    Stale,
    Partial,
    Unavailable
}

public sealed record SourceRunObservation(
    string Source,
    bool Enabled,
    DateTime? LastSuccessfulAt,
    string? LastStatus);

public sealed record DataStatusSummary(
    DataFreshnessState State,
    DateTime? LastSuccessfulUpdateUtc,
    DateTime? NewestListingSeenUtc,
    int EnabledSources,
    int EnabledSourcesWithRecentSuccess,
    string Message);

/// <summary>
/// Fresh/stale/partial/unavailable state for the tracked dataset. A failed
/// refresh must never read as zero demand: the last successful dataset is
/// retained and this status is surfaced instead.
/// </summary>
public static class DataStatusEvaluator
{
    /// <summary>Aggregate dataset is stale after this long without a successful crawl.</summary>
    public static readonly TimeSpan DefaultStaleAfter = TimeSpan.FromHours(48);

    public static DataStatusSummary Evaluate(
        DateTime nowUtc,
        DateTime? lastSuccessfulUpdateUtc,
        DateTime? newestListingSeenUtc,
        IReadOnlyList<SourceRunObservation> sources,
        TimeSpan staleAfter)
    {
        var enabled = sources.Where(source => source.Enabled).ToList();
        var refreshed = enabled.Count(source =>
            source.LastSuccessfulAt is { } at && at >= nowUtc - staleAfter);

        if (lastSuccessfulUpdateUtc is null)
        {
            return new DataStatusSummary(
                DataFreshnessState.Unavailable,
                null,
                newestListingSeenUtc,
                enabled.Count,
                refreshed,
                "No successful crawl has completed yet. Counts are not a zero-demand signal.");
        }

        if (lastSuccessfulUpdateUtc < nowUtc - staleAfter)
        {
            return new DataStatusSummary(
                DataFreshnessState.Stale,
                lastSuccessfulUpdateUtc,
                newestListingSeenUtc,
                enabled.Count,
                refreshed,
                $"Data last updated {FormatAge(nowUtc - lastSuccessfulUpdateUtc.Value)} ago; counts may be out of date.");
        }

        if (refreshed < enabled.Count)
        {
            var missing = enabled.Count - refreshed;
            return new DataStatusSummary(
                DataFreshnessState.Partial,
                lastSuccessfulUpdateUtc,
                newestListingSeenUtc,
                enabled.Count,
                refreshed,
                $"{missing} of {enabled.Count} enabled sources did not refresh within the freshness window; the last successful dataset is shown.");
        }

        return new DataStatusSummary(
            DataFreshnessState.Fresh,
            lastSuccessfulUpdateUtc,
            newestListingSeenUtc,
            enabled.Count,
            refreshed,
            "Data is fresh.");
    }

    private static string FormatAge(TimeSpan age)
    {
        if (age.TotalHours < 1)
        {
            return $"{Math.Max(1, (int)age.TotalMinutes)}m";
        }

        if (age.TotalDays < 1)
        {
            return $"{(int)age.TotalHours}h";
        }

        return $"{(int)age.TotalDays}d";
    }
}
