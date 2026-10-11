namespace Application.Analytics;

public sealed record OverviewDto(
    int TotalActiveJobs,
    int NewJobsToday,
    int NewJobsThisWeek,
    int TotalCompanies,
    decimal? AverageSalary,
    decimal? MedianSalary,
    string? MostActiveRegion,
    string? MostRequestedSkill);

public sealed record SkillDemandDto(string SkillName, int JobCount);

public sealed record JobsByRegionDto(string Region, int JobCount);

/// <summary>Fresh-posting counts for the 2, 7 and 15 day windows.</summary>
public sealed record FreshnessWindowDto(int Days, int JobCount);

public sealed record FreshnessDto(int TotalActiveJobs, IReadOnlyList<FreshnessWindowDto> Windows);
