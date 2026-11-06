namespace Application.Analytics;

/// <summary>
/// One cohort definition for the two-cohort comparison. Every field is an
/// existing filter vocabulary; no new facet is introduced.
/// </summary>
public sealed record ComparisonCohortQuery(
    string? Region = null,
    string? RoleFamily = null,
    string? DataSubRole = null,
    string? Seniority = null,
    string? WorkMode = null,
    string? Skill = null);

public sealed record CohortComparisonQuery(
    int Days,
    ComparisonCohortQuery A,
    ComparisonCohortQuery B);

public sealed record ComparisonCohortDefinitionDto(
    string? Region,
    string? RoleFamily,
    string? DataSubRole,
    string? Seniority,
    string? WorkMode,
    string? Skill,
    bool HasFilters);

/// <summary>
/// A complete-day new-listing period for one cohort. When the cohort cannot be
/// reconstructed from retained snapshots, <see cref="CurrentNewListings"/> is
/// null and <see cref="UnavailableReason"/> is specific; a zero is never
/// substituted.
/// </summary>
public sealed record ComparisonPeriodDto(
    bool Available,
    string? UnavailableReason,
    MetricIntervalDto? Interval,
    int? CurrentNewListings,
    int? PreviousNewListings,
    ChangeDto? Change,
    bool CurrentPeriodCovered,
    string DateBasis,
    string? Note);

public sealed record ComparisonCohortDto(
    string Key,
    string Label,
    ComparisonCohortDefinitionDto Definition,
    int ActiveListings,
    int ListingsWithSkills,
    decimal SkillCoveragePercent,
    SalarySummaryDto Salary,
    ComparisonPeriodDto NewListings);

/// <summary>
/// A cross-cohort count difference. Cohort A is the explicit baseline; when it
/// is zero the absolute difference is shown with the reason, never infinity.
/// </summary>
public sealed record ComparisonCountDifferenceDto(
    int ACount,
    int BCount,
    int AbsoluteDifference,
    decimal? RelativeDifferencePercent,
    bool BaselineAvailable,
    string Baseline,
    string Note);

/// <summary>
/// A skill-share difference in percentage points (B minus A), never a ratio.
/// The denominators are the two active cohort counts.
/// </summary>
public sealed record ComparisonSkillDifferenceDto(
    string SkillName,
    int ACount,
    decimal ASharePercent,
    int BCount,
    decimal BSharePercent,
    decimal DifferencePercentagePoints,
    int AListingDenominator,
    int BListingDenominator);

/// <summary>
/// One skill's share in each cohort over the current and previous complete
/// periods. A change is null when the previous period has no listing
/// denominator, rather than being shown as an infinite rise.
/// </summary>
public sealed record SkillHistorySkillDto(
    string SkillName,
    int ACurrentCount,
    decimal ACurrentSharePercent,
    int? APreviousCount,
    decimal? APreviousSharePercent,
    decimal? AChangePoints,
    int BCurrentCount,
    decimal BCurrentSharePercent,
    int? BPreviousCount,
    decimal? BPreviousSharePercent,
    decimal? BChangePoints);

/// <summary>
/// Skill-share history is only eligible when every day in both complete
/// comparison windows actually captured skills. Snapshot skill capture began
/// 2026-11-06, so the current thin history is declared unavailable with the
/// specific date/depth reason instead of being reconstructed from survivors.
/// </summary>
public sealed record SkillHistoryDto(
    bool Available,
    string? Reason,
    int SkillsCapturedSnapshotDays,
    int RequiredCompleteDays,
    string? FirstSkillsCapturedDate,
    string? LastSkillsCapturedDate,
    int PeriodDays,
    string? CurrentPeriodLabel,
    string? PreviousPeriodLabel,
    string? Basis,
    int? ACurrentListings,
    int? APreviousListings,
    int? BCurrentListings,
    int? BPreviousListings,
    IReadOnlyList<SkillHistorySkillDto> Skills);

public sealed record CohortComparisonDto(
    DateTime GeneratedAtUtc,
    int Days,
    MetricIntervalDto Interval,
    ComparisonCohortDto A,
    ComparisonCohortDto B,
    ComparisonCountDifferenceDto ActiveCountDifference,
    IReadOnlyList<ComparisonSkillDifferenceDto> SkillDifferences,
    SkillHistoryDto SkillHistory,
    string DateRule,
    string ScopeNote,
    IReadOnlyList<string> Notes);
