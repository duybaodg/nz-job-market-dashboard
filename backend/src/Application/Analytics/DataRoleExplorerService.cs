using Application.Common;
using Application.Jobs;
using Application.Metrics;
using Microsoft.EntityFrameworkCore;

namespace Application.Analytics;

public sealed record DataRoleExplorerQuery(
    string? Region = null,
    string? Seniority = null,
    string? WorkMode = null,
    string? Skill = null);

public interface IDataRoleExplorerService
{
    Task<DataRoleExplorerDto> GetDataRolesAsync(
        DataRoleExplorerQuery query,
        CancellationToken cancellationToken);
}

/// <summary>
/// Round 22 (C1): the Data &amp; Analytics explorer. Counts, salary samples and
/// skill coverage are all computed over the same active Listing snapshot and the
/// same filter set, and every salary group goes through
/// <see cref="SalaryMetrics"/> so the existing &lt;5 suppression and 5–9
/// warning rules are preserved exactly.
/// </summary>
public sealed class DataRoleExplorerService(
    IApplicationDbContext dbContext,
    TimeProvider? timeProvider = null,
    DataSubRoleTaxonomy? taxonomy = null) : IDataRoleExplorerService
{
    public const string SalaryBasis =
        "Median advertised midpoint for comparable permanent annual NZD listings in the sub-role; groups below 5 are suppressed and 5–9 are flagged.";

    public const string SkillBasis =
        "A normalised skill counts at most once per listing; the denominator is the sub-role's active listing count, so skill shares can sum above 100%.";

    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly DataSubRoleTaxonomy rules = taxonomy ?? DataSubRoleTaxonomy.Default;

    public async Task<DataRoleExplorerDto> GetDataRolesAsync(
        DataRoleExplorerQuery query,
        CancellationToken cancellationToken)
    {
        query ??= new DataRoleExplorerQuery();
        var now = clock.GetUtcNow().UtcDateTime;
        var region = Clean(query.Region);
        var seniority = Clean(query.Seniority);
        var workMode = Clean(query.WorkMode);
        var skill = Clean(query.Skill);

        var jobs = await LoadActiveJobsAsync(cancellationToken);
        var skillsByJob = await LoadSkillsAsync(cancellationToken);
        var rows = new List<ExplorerRow>();

        foreach (var job in jobs)
        {
            var family = RoleFamilyClassifier.Classify(job.Title).Family;
            if (!string.Equals(family, rules.Family, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (region is not null
                && !string.Equals(job.Region, region, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(job.Location, region, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (seniority is not null && !string.Equals(job.Seniority, seniority, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (workMode is not null && !string.Equals(job.WorkMode, workMode, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var jobSkills = skillsByJob.GetValueOrDefault(job.Id) ?? [];
            if (skill is not null
                && !jobSkills.Contains(skill, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            var match = DataSubRoleClassifier.Classify(job.Title, family, rules);
            rows.Add(new ExplorerRow(job, jobSkills, match));
        }

        var cohortCount = rows.Count;
        var groups = rows
            .GroupBy(row => row.Match.Key ?? DataSubRoleClassifier.UncertainKey, StringComparer.Ordinal)
            .Select(group =>
            {
                var first = group.First();
                var uncertain = first.Match.Key is null;
                var salary = SalaryMetrics.Compute(group.Select(row => new SalaryObservation(
                    row.Job.SalaryMin,
                    row.Job.SalaryMax,
                    row.Job.SalaryText,
                    string.IsNullOrWhiteSpace(row.Job.SalaryCurrency) ? "NZD" : row.Job.SalaryCurrency!,
                    ParsePayPeriod(row.Job.SalaryPeriod),
                    row.Job.EmploymentType,
                    row.Job.SalaryQuestionable)));
                var skillCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var listingsWithSkills = 0;

                foreach (var row in group)
                {
                    var unique = row.Skills
                        .Where(name => !string.IsNullOrWhiteSpace(name))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    if (unique.Count > 0)
                    {
                        listingsWithSkills++;
                    }

                    foreach (var name in unique)
                    {
                        skillCounts[name] = skillCounts.GetValueOrDefault(name) + 1;
                    }
                }

                var count = group.Count();
                var topSkills = skillCounts
                    .OrderByDescending(entry => entry.Value)
                    .ThenBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
                    .Take(8)
                    .Select(entry => new DataSubRoleSkillDto(
                        entry.Key,
                        entry.Value,
                        Percent(entry.Value, count)))
                    .ToList();

                return new DataSubRoleBreakdownDto(
                    first.Match.Key ?? DataSubRoleClassifier.UncertainKey,
                    first.Match.Key is null ? rules.UncertainLabel : first.Match.Label!,
                    uncertain,
                    group.SelectMany(row => row.Match.CandidateLabels)
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(label => label, StringComparer.Ordinal)
                        .ToList(),
                    count,
                    Percent(count, cohortCount),
                    ToSalaryDto(salary),
                    listingsWithSkills,
                    Percent(listingsWithSkills, count),
                    topSkills);
            })
            .OrderBy(group => group.Uncertain)
            .ThenByDescending(group => group.ListingCount)
            .ThenBy(group => group.Label, StringComparer.Ordinal)
            .ToList();

        var classified = groups.Where(group => !group.Uncertain).Sum(group => group.ListingCount);
        var uncertainCount = cohortCount - classified;

        return new DataRoleExplorerDto(
            now,
            rules.Family,
            rules.Version,
            rules.EffectiveDate,
            rules.Description,
            rules.Scope,
            rules.UncertainLabel,
            cohortCount,
            classified,
            uncertainCount,
            Percent(classified, cohortCount),
            new DataRoleExplorerFiltersDto(
                region,
                seniority,
                workMode,
                skill,
                region is not null || seniority is not null || workMode is not null || skill is not null,
                "Filters are applied to active Data & Analytics listings only. They do not change the rest of the trends page."),
            groups,
            SalaryBasis,
            SkillBasis,
            "Active tracked listings at the capture time, Data & Analytics family only. Sub-role labels are inferred from the retained title by taxonomy " + rules.Version + "; ambiguous and unspecified titles stay in the explicit uncertain row. Salary groups below five are suppressed, never shown as zero.");
    }

    private async Task<List<ExplorerJob>> LoadActiveJobsAsync(CancellationToken cancellationToken)
    {
        return await dbContext.Jobs
            .AsNoTracking()
            .Where(job => job.IsActive)
            .Select(job => new ExplorerJob(
                job.Id,
                job.Title,
                job.Location,
                job.Region,
                job.Seniority,
                job.WorkMode,
                job.SalaryMin,
                job.SalaryMax,
                job.SalaryText,
                job.SalaryCurrency,
                job.SalaryPeriod,
                job.SalaryQuestionable,
                job.EmploymentType))
            .ToListAsync(cancellationToken);
    }

    private async Task<Dictionary<Guid, IReadOnlyList<string>>> LoadSkillsAsync(CancellationToken cancellationToken)
    {
        var skills = await dbContext.JobSkills
            .AsNoTracking()
            .Where(skill => skill.Job != null && skill.Job.IsActive)
            .Select(skill => new { skill.JobId, skill.SkillName })
            .ToListAsync(cancellationToken);

        return skills
            .GroupBy(skill => skill.JobId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<string>)group
                    .Select(skill => skill.SkillName)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .ToList());
    }

    private static SalarySummaryDto ToSalaryDto(SalarySummary salary) =>
        new(
            salary.MedianAnnualNzd,
            salary.EligibleCount,
            salary.CohortCount,
            salary.CoveragePercent,
            salary.QuestionableCount,
            salary.Suppressed,
            salary.SmallSample,
            salary.Deduplicated,
            salary.Exclusions,
            salary.Currency,
            salary.PayPeriod,
            salary.Basis);

    private static SalaryPayPeriod ParsePayPeriod(string? value) =>
        Enum.TryParse<SalaryPayPeriod>(value, ignoreCase: true, out var period)
            ? period
            : SalaryPayPeriod.Unknown;

    private static decimal Percent(int numerator, int denominator) =>
        denominator == 0
            ? 0m
            : Math.Round((decimal)numerator / denominator * 100m, 1, MidpointRounding.AwayFromZero);

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record ExplorerJob(
        Guid Id,
        string Title,
        string? Location,
        string? Region,
        string Seniority,
        string WorkMode,
        decimal? SalaryMin,
        decimal? SalaryMax,
        string? SalaryText,
        string? SalaryCurrency,
        string? SalaryPeriod,
        bool SalaryQuestionable,
        string EmploymentType);

    private sealed record ExplorerRow(
        ExplorerJob Job,
        IReadOnlyList<string> Skills,
        DataSubRoleMatch Match);
}
