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
