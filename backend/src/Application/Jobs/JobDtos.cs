namespace Application.Jobs;

public sealed record JobDto(
    Guid Id,
    string Source,
    string Title,
    string? Company,
    string? Location,
    string? Region,
    decimal? SalaryMin,
    decimal? SalaryMax,
    string? SalaryText,
    string SalaryCurrency,
    string SalaryPeriod,
    bool SalaryQuestionable,
    string? SalaryQuestionableReason,
    string EmploymentType,
    string Seniority,
    string WorkMode,
    string ExperienceBand,
    string? Industry,
    string? DescriptionSummary,
    string Url,
    DateTime? PostedDate,
    DateTime? ClosingDate,
    DateTime FirstSeenAt,
    DateTime LastSeenAt,
    bool IsActive,
    DateTime? ExpiredAt,
    IReadOnlyList<string> Skills,
    IReadOnlyList<string> ProgrammingLanguages,
    // Advertiser vs hiring employer, derived centrally so every view agrees.
    string SourceCategory,
    string Advertiser,
    string AdvertiserType,
    string? HiringEmployer,
    string EmployerDisclosure,
    // Deterministic title classification; the original title is always retained.
    string RoleFamily,
    bool RoleFamilyInferred);

/// <summary>
/// Job list filters. <c>Days</c> limits results to the freshest postings — the
/// dashboard exposes 2, 7 and 15 day windows.
/// </summary>
public sealed record JobListQuery(string? Region, string? Skill, string? Search, string? Status, int? Days = null);
