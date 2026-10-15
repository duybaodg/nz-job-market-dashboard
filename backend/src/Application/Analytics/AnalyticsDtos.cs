namespace Application.Analytics;

public sealed record SkillDemandDto(string SkillName, int JobCount);

public sealed record JobsByRegionDto(string Region, int JobCount);

/// <summary>
/// An explicit reporting interval: inclusive UTC start, exclusive UTC end, plus
/// the Pacific/Auckland local dates it was derived from.
/// </summary>
public sealed record MetricIntervalDto(
    DateTime StartUtc,
    DateTime EndUtc,
    string StartLocalDate,
    string EndLocalDateExclusive,
    int Days,
    string Label,
    bool IsPartial);

/// <summary>
/// Change against the equal-duration previous period. A null
/// <see cref="Previous"/> means no comparable history exists and no percentage
/// is shown; a zero previous value yields the absolute change only.
/// </summary>
public sealed record ChangeDto(
    int Current,
    int? Previous,
    int? AbsoluteChange,
    decimal? PercentChange,
    bool ComparisonAvailable,
    bool NoPriorBaseline,
    string? Note);

public sealed record SalarySummaryDto(
    decimal? MedianAnnualNzd,
    int EligibleCount,
    int CohortCount,
    decimal CoveragePercent,
    int QuestionableCount,
    bool Suppressed,
    bool SmallSample,
    bool Deduplicated,
    IReadOnlyDictionary<string, int> Exclusions,
    string Currency,
    string PayPeriod,
    string Basis);

public sealed record EmployerSummaryDto(
    int IdentifiableEmployers,
    int ListingsWithEmployer,
    int ListingsWithoutEmployer,
    decimal CoveragePercent);

public sealed record DataStatusDto(
    string State,
    DateTime? LastSuccessfulUpdateUtc,
    DateTime? NewestListingSeenUtc,
    int EnabledSources,
    int EnabledSourcesWithRecentSuccess,
    string Message);

/// <summary>
/// The dashboard summary metric. Every field carries the cohort, interval and
/// date basis it was computed from so cards can never claim a different scope
/// from the charts or the freshness panel.
/// </summary>
public sealed record OverviewDto(
    DateTime GeneratedAtUtc,
    int ActiveListings,
    ChangeDto NewlyDiscovered,
    ChangeDto NewlyPosted,
    SalarySummaryDto Salary,
    EmployerSummaryDto Employers,
    DataStatusDto DataStatus,
    string? MostActiveRegion,
    string? MostRequestedSkill,
    MetricIntervalDto Interval);

public sealed record FreshnessWindowDto(
    int Days,
    MetricIntervalDto Interval,
    int NewlyPosted,
    int PreviousNewlyPosted,
    int NewlyDiscovered,
    int PreviousNewlyDiscovered,
    int PostedDateKnown,
    int PostedDateMissing,
    bool ComparisonAvailable);

public sealed record FreshnessDto(
    int TotalActiveJobs,
    DateTime GeneratedAtUtc,
    DataStatusDto DataStatus,
    IReadOnlyList<FreshnessWindowDto> Windows);
