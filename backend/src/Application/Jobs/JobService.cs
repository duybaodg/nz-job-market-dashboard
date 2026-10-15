using Application.Common;
using Application.Employers;
using Domain.Entities;
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

        jobs = query.Status?.ToLowerInvariant() switch
        {
            "all" => jobs,
            "expired" => jobs.Where(job => !job.IsActive),
            _ => jobs.Where(job => job.IsActive)
        };

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

        // "Fresh postings" windows: 2, 7 and 15 days. Fall back to FirstSeenAt when a
        // source does not publish a posting date, so freshly discovered jobs still count.
        if (query.Days is > 0)
        {
            var cutoff = DateTime.UtcNow.AddDays(-query.Days.Value);
            jobs = jobs.Where(job => (job.PostedDate ?? job.FirstSeenAt) >= cutoff);
        }

        var entities = await jobs
            .OrderByDescending(job => job.PostedDate ?? job.FirstSeenAt)
            .ToListAsync(cancellationToken);

        var categories = await LoadSourceCategoriesAsync(cancellationToken);

        return entities.Select(job => ToDto(job, categories)).ToList();
    }

    public async Task<JobDto?> GetJobAsync(Guid id, CancellationToken cancellationToken)
    {
        var job = await dbContext.Jobs
            .AsNoTracking()
            .Include(entity => entity.Skills)
            .SingleOrDefaultAsync(entity => entity.Id == id, cancellationToken);

        if (job is null)
        {
            return null;
        }

        var categories = await LoadSourceCategoriesAsync(cancellationToken);
        return ToDto(job, categories);
    }

    private async Task<IReadOnlyDictionary<string, string>> LoadSourceCategoriesAsync(CancellationToken cancellationToken)
    {
        var categories = await dbContext.JobSources
            .AsNoTracking()
            .Select(source => new { source.Name, source.Category })
            .ToListAsync(cancellationToken);

        return categories
            .GroupBy(source => source.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => string.IsNullOrWhiteSpace(group.First().Category) ? "unknown" : group.First().Category,
                StringComparer.OrdinalIgnoreCase);
    }

    private static JobDto ToDto(Job job, IReadOnlyDictionary<string, string> sourceCategories)
    {
        sourceCategories.TryGetValue(job.Source, out var category);
        var employer = EmployerClassifier.Classify(job.Source, category, job.Company);
        var roleFamily = RoleFamilyClassifier.Classify(job.Title);

        return new JobDto(
            job.Id,
            job.Source,
            job.Title,
            job.Company,
            job.Location,
            job.Region,
            job.SalaryMin,
            job.SalaryMax,
            job.SalaryText,
            job.SalaryCurrency,
            job.SalaryPeriod,
            job.SalaryQuestionable,
            job.SalaryQuestionableReason,
            job.EmploymentType,
            job.Seniority,
            job.WorkMode,
            job.ExperienceBand,
            job.Industry,
            job.DescriptionSummary,
            job.Url,
            job.PostedDate,
            job.ClosingDate,
            job.FirstSeenAt,
            job.LastSeenAt,
            job.IsActive,
            job.ExpiredAt,
            job.Skills.OrderBy(skill => skill.SkillName).Select(skill => skill.SkillName).ToList(),
            job.Skills.Where(skill => skill.SkillType == "ProgrammingLanguage").OrderBy(skill => skill.SkillName).Select(skill => skill.SkillName).ToList(),
            employer.SourceCategory,
            employer.Advertiser,
            employer.AdvertiserType.ToString(),
            employer.HiringEmployer,
            employer.Disclosure.ToString(),
            roleFamily.Family,
            roleFamily.Inferred);
    }
}
