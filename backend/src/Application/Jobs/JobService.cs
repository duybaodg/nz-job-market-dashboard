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
    /// <summary>
    /// Round 15 (H-12): the public feed is bounded. The SPA aggregates client
    /// side by design, so it requests the maximum retained-active window; a
    /// larger dataset is still truncated rather than materialising the table.
    /// </summary>
    public const int DefaultLimit = 2000;

    public const int MaxLimit = 5000;

    public async Task<IReadOnlyList<JobDto>> GetJobsAsync(JobListQuery query, CancellationToken cancellationToken)
    {
        var limit = Math.Clamp(query.Limit ?? DefaultLimit, 1, MaxLimit);
        var offset = Math.Max(query.Offset ?? 0, 0);
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
            .Skip(offset)
            .Take(limit)
            .ToListAsync(cancellationToken);

        var categories = await LoadSourceCategoriesAsync(cancellationToken);
        var duplicateInfo = await LoadDuplicateInfoAsync(entities.Select(job => job.DuplicateGroupId), cancellationToken);
        var employerNames = await LoadEmployerNamesAsync(entities.Select(job => job.EmployerCanonicalId), cancellationToken);

        return entities
            .Select(job => ToDto(
                job,
                categories,
                duplicateInfo.GetValueOrDefault(job.DuplicateGroupId ?? Guid.Empty),
                employerNames))
            .ToList();
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
        var duplicateInfo = await LoadDuplicateInfoAsync([job.DuplicateGroupId], cancellationToken);
        var employerNames = await LoadEmployerNamesAsync([job.EmployerCanonicalId], cancellationToken);
        return ToDto(job, categories, duplicateInfo.GetValueOrDefault(job.DuplicateGroupId ?? Guid.Empty), employerNames);
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

    private async Task<IReadOnlyDictionary<Guid, DuplicateGroupInfo>> LoadDuplicateInfoAsync(
        IEnumerable<Guid?> groupIds,
        CancellationToken cancellationToken)
    {
        var ids = groupIds.Where(id => id.HasValue).Select(id => id!.Value).Distinct().ToList();
        if (ids.Count == 0)
        {
            return new Dictionary<Guid, DuplicateGroupInfo>();
        }

        var canonicalByGroup = await dbContext.DuplicateGroups
            .AsNoTracking()
            .Where(group => ids.Contains(group.Id))
            .Select(group => new { group.Id, group.CanonicalListingId })
            .ToDictionaryAsync(group => group.Id, group => group.CanonicalListingId, cancellationToken);

        var members = await dbContext.Jobs
            .AsNoTracking()
            .Where(job => job.DuplicateGroupId != null && ids.Contains(job.DuplicateGroupId.Value))
            .Select(job => new { job.DuplicateGroupId, job.Source })
            .ToListAsync(cancellationToken);

        return members
            .GroupBy(member => member.DuplicateGroupId!.Value)
            .ToDictionary(
                group => group.Key,
                group => new DuplicateGroupInfo(
                    canonicalByGroup.GetValueOrDefault(group.Key),
                    group.Count(),
                    group.Select(member => member.Source).Distinct(StringComparer.OrdinalIgnoreCase).ToList()));
    }

    private async Task<IReadOnlyDictionary<Guid, string>> LoadEmployerNamesAsync(
        IEnumerable<Guid?> employerIds,
        CancellationToken cancellationToken)
    {
        var ids = employerIds.Where(id => id.HasValue).Select(id => id!.Value).Distinct().ToList();
        if (ids.Count == 0)
        {
            return new Dictionary<Guid, string>();
        }

        return await dbContext.Employers
            .AsNoTracking()
            .Where(employer => ids.Contains(employer.Id))
            .Select(employer => new { employer.Id, employer.CanonicalName })
            .ToDictionaryAsync(employer => employer.Id, employer => employer.CanonicalName, cancellationToken);
    }

    private static JobDto ToDto(
        Job job,
        IReadOnlyDictionary<string, string> sourceCategories,
        DuplicateGroupInfo? duplicateInfo,
        IReadOnlyDictionary<Guid, string> employerNames)
    {
        sourceCategories.TryGetValue(job.Source, out var category);
        var employer = EmployerClassifier.Classify(job.Source, category, job.Company);
        var roleFamily = RoleFamilyClassifier.Classify(job.Title);
        var memberCount = duplicateInfo?.MemberCount ?? 1;
        var alsoOn = duplicateInfo is null
            ? []
            : duplicateInfo.Sources
                .Where(source => !string.Equals(source, job.Source, StringComparison.OrdinalIgnoreCase))
                .ToList();

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
            roleFamily.Inferred,
            job.DuplicateGroupId,
            Math.Max(0, memberCount - 1),
            alsoOn,
            duplicateInfo is not null && duplicateInfo.CanonicalListingId == job.Id,
            job.EmployerCanonicalId,
            job.EmployerCanonicalId is { } canonicalId && employerNames.TryGetValue(canonicalId, out var canonicalName)
                ? canonicalName
                : null);
    }

    private sealed record DuplicateGroupInfo(Guid CanonicalListingId, int MemberCount, IReadOnlyList<string> Sources);
}
