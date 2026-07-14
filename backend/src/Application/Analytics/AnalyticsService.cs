using Application.Common;
using Microsoft.EntityFrameworkCore;

namespace Application.Analytics;

public interface IAnalyticsService
{
    Task<OverviewDto> GetOverviewAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<SkillDemandDto>> GetTopSkillsAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<JobsByRegionDto>> GetJobsByRegionAsync(CancellationToken cancellationToken);
}

public sealed class AnalyticsService(IApplicationDbContext dbContext) : IAnalyticsService
{
    public async Task<OverviewDto> GetOverviewAsync(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var today = now.Date;
        var weekStart = today.AddDays(-7);

        var salaries = await dbContext.Jobs
            .AsNoTracking()
            .Where(job => job.SalaryMin.HasValue || job.SalaryMax.HasValue)
            .Select(job => new { job.SalaryMin, job.SalaryMax })
            .ToListAsync(cancellationToken);

        var salaryMidpoints = salaries
            .Select(salary => ((salary.SalaryMin ?? salary.SalaryMax)!.Value + (salary.SalaryMax ?? salary.SalaryMin)!.Value) / 2)
            .OrderBy(value => value)
            .ToList();

        return new OverviewDto(
            await dbContext.Jobs.CountAsync(cancellationToken),
            await dbContext.Jobs.CountAsync(job => job.FirstSeenAt >= today, cancellationToken),
            await dbContext.Jobs.CountAsync(job => job.FirstSeenAt >= weekStart, cancellationToken),
            await dbContext.Jobs.Where(job => job.Company != null).Select(job => job.Company).Distinct().CountAsync(cancellationToken),
            salaryMidpoints.Count == 0 ? null : salaryMidpoints.Average(),
            GetMedian(salaryMidpoints),
            await dbContext.Jobs
                .Where(job => job.Region != null)
                .GroupBy(job => job.Region!)
                .OrderByDescending(group => group.Count())
                .Select(group => group.Key)
                .FirstOrDefaultAsync(cancellationToken),
            await dbContext.JobSkills
                .GroupBy(skill => skill.SkillName)
                .OrderByDescending(group => group.Count())
                .Select(group => group.Key)
                .FirstOrDefaultAsync(cancellationToken));
    }

    public async Task<IReadOnlyList<SkillDemandDto>> GetTopSkillsAsync(CancellationToken cancellationToken)
    {
        return await dbContext.JobSkills
            .AsNoTracking()
            .GroupBy(skill => skill.SkillName)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key)
            .Take(10)
            .Select(group => new SkillDemandDto(group.Key, group.Select(skill => skill.JobId).Distinct().Count()))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<JobsByRegionDto>> GetJobsByRegionAsync(CancellationToken cancellationToken)
    {
        return await dbContext.Jobs
            .AsNoTracking()
            .Where(job => job.Region != null)
            .GroupBy(job => job.Region!)
            .OrderByDescending(group => group.Count())
            .Select(group => new JobsByRegionDto(group.Key, group.Count()))
            .ToListAsync(cancellationToken);
    }

    private static decimal? GetMedian(IReadOnlyList<decimal> values)
    {
        if (values.Count == 0)
        {
            return null;
        }

        var midpoint = values.Count / 2;
        return values.Count % 2 == 0
            ? (values[midpoint - 1] + values[midpoint]) / 2
            : values[midpoint];
    }
}
