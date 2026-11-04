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
    string Note,
    int RetentionDays);

public sealed record WeeklyNewListingDto(
    string WeekStartLocal,
    string WeekEndLocalExclusive,
    int Count,
    bool IsPartial,
    string? CoverageStartDate,
    string? Note);

/// <summary>
/// A source appearing or disappearing between two retained snapshots, with an
/// evidence-based classification. A missing source is never automatically a
/// closure, and a returning source is never automatically organic growth.
/// </summary>
public sealed record SourceCoverageEventDto(
    string Source,
    string Direction,
    string Classification,
    string Evidence);

public sealed record SourceCoverageChangeDto(
    string SnapshotDate,
    IReadOnlyList<string> SourcesAdded,
    IReadOnlyList<string> SourcesRemoved,
    IReadOnlyList<SourceCoverageEventDto> Events);

/// <summary>
/// A like-for-like active-listing comparison over the sources that have a valid
/// observation on every day of the window. The raw comparison is retained beside
/// it, and the residual between them is left explicit rather than labelled as
/// organic demand.
/// </summary>
public sealed record ConstantSourceComparisonDto(
    bool Available,
    string? Reason,
    string? FromSnapshotDate,
    string? ToSnapshotDate,
    int? ObservedSourceCount,
    int? EligibleSourceCount,
    int? FromActiveCount,
    int? ToActiveCount,
    ChangeDto? Change,
    int? RawFromActiveCount,
    int? RawToActiveCount,
    ChangeDto? RawChange,
    int? CollectionAssociatedResidual,
    string EligibilityRule,
    IReadOnlyList<string> Sources);

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
/// <summary>
/// The cohort selector applied to retained snapshots. Region, role family and
/// seniority are captured per snapshot row; skills are not, so a skill filter
/// makes a historical cohort unavailable rather than silently global.
/// </summary>
public sealed record HistoryFilter(
    string? Region = null,
    string? RoleFamily = null,
    string? Seniority = null,
    string? Skill = null)
{
    public static readonly HistoryFilter None = new();

    public bool HasFilters =>
        !string.IsNullOrWhiteSpace(Region)
        || !string.IsNullOrWhiteSpace(RoleFamily)
        || !string.IsNullOrWhiteSpace(Seniority)
        || !string.IsNullOrWhiteSpace(Skill);

    public bool HasSkillFilter => !string.IsNullOrWhiteSpace(Skill);

    /// <summary>Snapshots retain every other facet but never the skill list.</summary>
    public bool HistorySupported => !HasSkillFilter;
}

public sealed record HistoryFilterDto(
    string? Region,
    string? RoleFamily,
    string? Seniority,
    string? Skill,
    bool HasFilters,
    bool HistoryFilterSupported,
    string Note);

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
    ConstantSourceComparisonDto ConstantSourceComparison,
    HistoryFilterDto Filter,
    string ScopeNote,
    string Note);
