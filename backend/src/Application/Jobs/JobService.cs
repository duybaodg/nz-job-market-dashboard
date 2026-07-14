using Application.Common;
using Microsoft.EntityFrameworkCore;

namespace Application.Jobs;

public interface IJobService
{
    Task<IReadOnlyList<JobDto>> GetJobsAsync(JobListQuery query, CancellationToken cancellationToken);
    Task<JobDto?> GetJobAsync(Guid id, CancellationToken cancellationToken);
}

public sealed class JobService(IApplicationDbContext dbContext) : IJobService
{
    public async Task<IReadOnlyList<JobDto>> GetJobsAsync(JobListQuery query, CancellationToken cancellationToken)
    {
        var jobs = dbContext.Jobs.AsNoTracking().Include(job => job.Skills).AsQueryable();

        if (!string.IsNullOrWhiteSpace(query.Region))
        {
            jobs = jobs.Where(job => job.Region == query.Region);
        }

        if (!string.IsNullOrWhiteSpace(query.Skill))
        {
            jobs = jobs.Where(job => job.Skills.Any(skill => skill.SkillName == query.Skill));
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var search = query.Search.ToLower();
            jobs = jobs.Where(job =>
                job.Title.ToLower().Contains(search) ||
                (job.Company != null && job.Company.ToLower().Contains(search)));
        }

        return await jobs
            .OrderByDescending(job => job.PostedDate ?? job.FirstSeenAt)
            .Select(job => new JobDto(
                job.Id,
                job.Source,
                job.Title,
                job.Company,
                job.Location,
                job.Region,
                job.SalaryMin,
                job.SalaryMax,
                job.EmploymentType,
                job.Seniority,
                job.WorkMode,
                job.Industry,
                job.DescriptionSummary,
                job.Url,
                job.PostedDate,
                job.Skills.OrderBy(skill => skill.SkillName).Select(skill => skill.SkillName).ToList()))
            .ToListAsync(cancellationToken);
    }

    public async Task<JobDto?> GetJobAsync(Guid id, CancellationToken cancellationToken)
    {
        return await dbContext.Jobs
            .AsNoTracking()
            .Include(job => job.Skills)
            .Where(job => job.Id == id)
            .Select(job => new JobDto(
                job.Id,
                job.Source,
                job.Title,
                job.Company,
                job.Location,
                job.Region,
                job.SalaryMin,
                job.SalaryMax,
                job.EmploymentType,
                job.Seniority,
                job.WorkMode,
                job.Industry,
                job.DescriptionSummary,
                job.Url,
                job.PostedDate,
                job.Skills.OrderBy(skill => skill.SkillName).Select(skill => skill.SkillName).ToList()))
            .SingleOrDefaultAsync(cancellationToken);
    }
}
