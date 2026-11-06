namespace Application.Analytics;

/// <summary>
/// The filters applied to the Data &amp; Analytics explorer. The sub-role
/// selection is a UI/URL concern; the breakdown always returns every sub-role
/// so a selected role can be read against its siblings.
/// </summary>
public sealed record DataRoleExplorerFiltersDto(
    string? Region,
    string? Seniority,
    string? WorkMode,
    string? Skill,
    bool HasFilters,
    string Note);

public sealed record DataSubRoleSkillDto(
    string SkillName,
    int ListingCount,
    decimal SharePercent);

/// <summary>
/// One row of the versioned sub-role breakdown. An uncertain row has a null key
/// and a label from <see cref="DataRoleExplorerDto.UncertainLabel"/>; its
/// candidate labels explain what signals were seen without choosing one.
/// </summary>
public sealed record DataSubRoleBreakdownDto(
    string Key,
    string Label,
    bool Uncertain,
    IReadOnlyList<string> CandidateLabels,
    int ListingCount,
    decimal SharePercent,
    SalarySummaryDto Salary,
    int ListingsWithSkills,
    decimal SkillCoveragePercent,
    IReadOnlyList<DataSubRoleSkillDto> TopSkills);

public sealed record DataRoleExplorerDto(
    DateTime GeneratedAtUtc,
    string Family,
    string TaxonomyVersion,
    string TaxonomyEffectiveDate,
    string TaxonomyDescription,
    string TaxonomyScope,
    string UncertainLabel,
    int CohortCount,
    int ClassifiedCount,
    int UncertainCount,
    decimal ClassifiedSharePercent,
    DataRoleExplorerFiltersDto Filters,
    IReadOnlyList<DataSubRoleBreakdownDto> Roles,
    string SalaryBasis,
    string SkillBasis,
    string ScopeNote);
