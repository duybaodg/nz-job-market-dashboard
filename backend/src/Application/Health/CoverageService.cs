using Application.Common;
using Microsoft.EntityFrameworkCore;

namespace Application.Health;

public sealed record FieldCoverageDto(string Field, int Known, int Total, double Percentage, string? Note = null);

/// <summary>
/// Salary coverage detail. <see cref="Converted"/> counts advertised hourly/day
/// rates whose annual-equivalent is exposed only as a labelled derived value
/// (38h x 52w for hourly, 260 days for daily) — the original bounds and period
/// are never replaced.
/// </summary>
public sealed record SalaryCoverageDetailDto(
    int WithNumericBounds,
    int Annual,
    int HourlyOrDaily,
    int OpenEnded,
    int Questionable,
    int Converted);

public sealed record CoverageValueDto(string Value, int Count);

public sealed record CoverageReportDto(
    DateTime GeneratedAtUtc,
    int TotalJobs,
    int ActiveJobs,
    IReadOnlyList<FieldCoverageDto> Fields,
    SalaryCoverageDetailDto Salary,
    IReadOnlyList<CoverageValueDto> WorkModeBreakdown,
    IReadOnlyList<CoverageValueDto> SeniorityBreakdown);

public interface ICoverageService
{
    Task<CoverageReportDto> GetCoverageAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Per-field coverage report for the active listing cohort. It backs
/// <c>GET /api/admin/coverage</c> and the before/after evidence in the Round 9
/// report; it deliberately reads only stored columns (no recomputation) so the
/// number matches what the dashboard sees.
/// </summary>
public sealed class CoverageService(IApplicationDbContext dbContext) : ICoverageService
{
    public async Task<CoverageReportDto> GetCoverageAsync(CancellationToken cancellationToken)
    {
        var jobs = await dbContext.Jobs
            .AsNoTracking()
            .Where(job => job.IsActive)
            .Select(job => new
            {
                job.Id,
                job.SalaryMin,
                job.SalaryMax,
                job.SalaryPeriod,
                job.SalaryQuestionable,
                job.WorkMode,
                job.Seniority,
                job.EmploymentType,
                job.PostedDate,
                job.EmployerCanonicalId,
                HasSkills = job.Skills.Any()
            })
            .ToListAsync(cancellationToken);

        var total = jobs.Count;

        var salaryKnown = jobs.Count(job => job.SalaryMin is not null || job.SalaryMax is not null);
        var workModeKnown = jobs.Count(job => job.WorkMode != "Unknown");
        var seniorityKnown = jobs.Count(job => job.Seniority != "Unknown");
        var employmentKnown = jobs.Count(job => job.EmploymentType != "Unknown");
        var skillsKnown = jobs.Count(job => job.HasSkills);
        var postedKnown = jobs.Count(job => job.PostedDate is not null);
        var employerKnown = jobs.Count(job => job.EmployerCanonicalId is not null);

        var fields = new List<FieldCoverageDto>
        {
            new("salary", salaryKnown, total, Percent(salaryKnown, total), "usable numeric bounds"),
            new("workMode", workModeKnown, total, Percent(workModeKnown, total)),
            new("seniority", seniorityKnown, total, Percent(seniorityKnown, total)),
            new("employmentType", employmentKnown, total, Percent(employmentKnown, total)),
            new("skills", skillsKnown, total, Percent(skillsKnown, total), "at least one extracted skill"),
            new("postedDate", postedKnown, total, Percent(postedKnown, total)),
            new("employerCanonical", employerKnown, total, Percent(employerKnown, total), "reviewed canonical employer only")
        };

        var salary = new SalaryCoverageDetailDto(
            salaryKnown,
            jobs.Count(job => (job.SalaryMin is not null || job.SalaryMax is not null) && job.SalaryPeriod == "Year"),
            jobs.Count(job => (job.SalaryMin is not null || job.SalaryMax is not null) && job.SalaryPeriod is "Hour" or "Day"),
            jobs.Count(job => job.SalaryMin is null ^ job.SalaryMax is null),
            jobs.Count(job => job.SalaryQuestionable),
            jobs.Count(job => (job.SalaryMin is not null || job.SalaryMax is not null) && job.SalaryPeriod is "Hour" or "Day"));

        var workModeBreakdown = jobs
            .GroupBy(job => job.WorkMode)
            .Select(group => new CoverageValueDto(group.Key, group.Count()))
            .OrderByDescending(item => item.Count)
            .ToList();

        var seniorityBreakdown = jobs
            .GroupBy(job => job.Seniority)
            .Select(group => new CoverageValueDto(group.Key, group.Count()))
            .OrderByDescending(item => item.Count)
            .ToList();

        return new CoverageReportDto(
            DateTime.UtcNow,
            total,
            total,
            fields,
            salary,
            workModeBreakdown,
            seniorityBreakdown);
    }

    private static double Percent(int known, int total) =>
        total == 0 ? 0 : Math.Round(known * 100.0 / total, 1);
}
