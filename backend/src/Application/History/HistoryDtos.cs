using Application.Analytics;

namespace Application.History;

/// <summary>How much daily snapshot history has actually been retained.</summary>
public sealed record SnapshotCoverageDto(
    string? FirstDate,
    string? LastDate,
    int SnapshotDays,
    int ExpectedDays,
    IReadOnlyList<string> MissingDates,
    bool HasHistory,
    string Note);

public sealed record WeeklyNewListingDto(
    string WeekStartLocal,
    string WeekEndLocalExclusive,
    int Count,
    bool IsPartial,
    string? CoverageStartDate,
    string? Note);

public sealed record SourceCoverageChangeDto(
    string SnapshotDate,
    IReadOnlyList<string> SourcesAdded,
    IReadOnlyList<string> SourcesRemoved);

public sealed record ActiveTrendPointDto(
    string SnapshotDate,
    int ActiveCount,
    int SourceCount,
    bool CoverageChanged,
    IReadOnlyList<string> SourcesAdded,
    IReadOnlyList<string> SourcesRemoved);

/// <summary>
/// New listings over an explicit complete-day interval, with the equal-duration
/// previous period and a coverage flag so a partially retained period is never
/// compared as if complete.
/// </summary>
public sealed record NewListingPeriodDto(
    int Days,
    MetricIntervalDto Interval,
    ChangeDto Change,
    bool CurrentPeriodCovered,
    string DateBasis,
    string? Note);

public sealed record HistoryDto(
    DateTime GeneratedAtUtc,
    SnapshotCoverageDto Coverage,
    int LatestActiveCount,
    string? LatestSnapshotDate,
    ChangeDto ActiveChange,
    string? ComparableFromDate,
    NewListingPeriodDto NewListings,
    IReadOnlyList<WeeklyNewListingDto> WeeklyNewListings,
    IReadOnlyList<ActiveTrendPointDto> ActiveTrend,
    IReadOnlyList<SourceCoverageChangeDto> CoverageChanges,
    string Note);
