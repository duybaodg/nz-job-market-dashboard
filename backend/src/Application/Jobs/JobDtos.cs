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
    string? Industry,
    string? DescriptionSummary,
    string Url,
    DateTime? PostedDate,
    IReadOnlyList<string> Skills);

public sealed record JobListQuery(string? Region, string? Skill, string? Search);
