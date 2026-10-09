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
    IReadOnlyList<string> ProgrammingLanguages);

public sealed record JobListQuery(string? Region, string? Skill, string? Search, string? Status);
