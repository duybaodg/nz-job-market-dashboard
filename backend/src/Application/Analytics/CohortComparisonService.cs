using System.Text.Json;
using Application.Common;
using Application.History;
using Application.Jobs;
using Application.Metrics;
using Microsoft.EntityFrameworkCore;

namespace Application.Analytics;

public interface ICohortComparisonService
{
    Task<CohortComparisonDto> CompareAsync(
        CohortComparisonQuery query,
        CancellationToken cancellationToken);
}

/// <summary>
/// Round 22 (C2): one two-cohort comparison built only from existing filters.
/// Both cohorts use the same active capture for counts/skills/salary and the
/// same complete Pacific/Auckland windows for period counts. A count or share
/// is withheld with a specific reason when retained history cannot reconstruct
/// the cohort; no survivor-only or fabricated baseline is substituted.
/// </summary>
public sealed class CohortComparisonService(
    IApplicationDbContext dbContext,
    IHistoryService historyService,
    TimeProvider? timeProvider = null,
    DataSubRoleTaxonomy? taxonomy = null) : ICohortComparisonService
{
    private const int SkillLimit = 10;
    private const int SkillHistoryLimit = 10;

    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly DataSubRoleTaxonomy rules = taxonomy ?? DataSubRoleTaxonomy.Default;

    public async Task<CohortComparisonDto> CompareAsync(
        CohortComparisonQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var days = Math.Clamp(query.Days, 1, 365);
        var now = clock.GetUtcNow().UtcDateTime;
        var interval = NzReportingCalendar.LastCompleteDays(days, now);
        var previousInterval = NzReportingCalendar.PreviousPeriod(interval);

        var rows = await LoadRowsAsync(cancellationToken);
        var cohortA = await BuildCohortAsync(
            "A",
            query.A,
            rows,
            days,
            interval,
            previousInterval,
            cancellationToken);
        var cohortB = await BuildCohortAsync(
            "B",
            query.B,
            rows,
            days,
            interval,
            previousInterval,
            cancellationToken);

        var countDifference = BuildCountDifference(cohortA.Dto.ActiveListings, cohortB.Dto.ActiveListings);
        var skillDifferences = BuildSkillDifferences(cohortA, cohortB);
        var skillHistory = await BuildSkillHistoryAsync(
            query,
            rows,
            snapshotRows: null,
            interval,
            previousInterval,
            days,
            now,
            cancellationToken);

        var notes = new List<string>();
        if (SameDefinition(query.A, query.B))
        {
            notes.Add("The two cohorts resolve to the same filter definition; differences are zero by construction.");
        }

        if (query.A.DataSubRole is not null || query.B.DataSubRole is not null)
        {
            notes.Add(
                "Sub-role cohorts are classified from the retained title. Current counts, skill shares and salary samples are available, but period counts and skill-share history cannot be reconstructed from older snapshots that do not store the sub-role.");
        }

        if (query.A.Skill is not null || query.B.Skill is not null)
        {
            notes.Add(
                "A skill-filtered cohort cannot provide an unconditional skill-share history; the current skill share is still shown.");
        }

        return new CohortComparisonDto(
            now,
            days,
            MetricIntervalMapper.ToDto(interval, isPartial: false),
            cohortA.Dto,
            cohortB.Dto,
            countDifference,
            skillDifferences,
            skillHistory,
            DateRule,
            "Active tracked listings at one capture time for counts, skill shares and salary; complete Pacific/Auckland windows for period discovery counts. Both cohorts use identical date rules. Salary figures are descriptive advertised medians, not a causal premium.",
            notes);
    }

    private const string DateRule =
        "Both cohorts use identical date rules: the same active as-of capture for listing count, skill share and salary, and the same last complete Pacific/Auckland days for new-listing counts. A period count is withheld unless every day in that cohort's current and previous windows is retained. Skill-share history is withheld unless every day in both windows actually captured skills.";

    private async Task<CohortResult> BuildCohortAsync(
        string key,
        ComparisonCohortQuery query,
        IReadOnlyList<ComparisonRow> allRows,
        int days,
        ReportingInterval interval,
        ReportingInterval previousInterval,
        CancellationToken cancellationToken)
    {
        var rows = allRows.Where(row => Matches(row, query)).ToList();
        var salary = SalaryMetrics.Compute(rows.Select(row => new SalaryObservation(
            row.Job.SalaryMin,
            row.Job.SalaryMax,
            row.Job.SalaryText,
            string.IsNullOrWhiteSpace(row.Job.SalaryCurrency) ? "NZD" : row.Job.SalaryCurrency,
            ParsePayPeriod(row.Job.SalaryPeriod),
            row.Job.EmploymentType,
            row.Job.SalaryQuestionable)));

        var skillCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var listingsWithSkills = 0;
        foreach (var row in rows)
        {
            var unique = row.Skills
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (unique.Count > 0)
            {
                listingsWithSkills++;
            }

            foreach (var skill in unique)
            {
                skillCounts[skill] = skillCounts.GetValueOrDefault(skill) + 1;
            }
        }

        var period = await BuildPeriodAsync(query, days, cancellationToken);
        var dto = new ComparisonCohortDto(
            key,
            LabelFor(query),
            ToDefinitionDto(query),
            rows.Count,
            listingsWithSkills,
            Percent(listingsWithSkills, rows.Count),
            ToSalaryDto(salary),
            period);

        return new CohortResult(dto, skillCounts, rows.Count);
    }

    private async Task<ComparisonPeriodDto> BuildPeriodAsync(
        ComparisonCohortQuery query,
        int days,
        CancellationToken cancellationToken)
    {
        var dataSubRole = Clean(query.DataSubRole);
        var workMode = Clean(query.WorkMode);
        if (dataSubRole is not null)
        {
            return UnavailablePeriod(
                "Historical snapshots retain the role family but not the versioned Data & Analytics sub-role, so this cohort's period count cannot be reconstructed. The current active count is shown; no historical value is substituted.");
        }

        if (workMode is not null)
        {
            return UnavailablePeriod(
                "Historical cohort selection does not support work mode, so this cohort's period count cannot be reconstructed. The current active count is shown; no historical value is substituted.");
        }

        var filter = new HistoryFilter(
            Clean(query.Region),
            Clean(query.RoleFamily),
            Clean(query.Seniority),
            Clean(query.Skill));
        var history = await historyService.GetHistoryAsync(days, filter, cancellationToken);
        var period = history.NewListings;
        var covered = period.CurrentPeriodCovered;

        return new ComparisonPeriodDto(
            covered,
            covered ? null : period.Note ?? "The current period is not fully retained, so it is unavailable.",
            period.Interval,
            covered ? period.Change.Current : null,
            covered ? period.Change.Previous : null,
            covered ? period.Change : null,
            covered,
            period.DateBasis,
            period.Note);
    }

    private static ComparisonPeriodDto UnavailablePeriod(string reason) =>
        new(
            false,
            reason,
            null,
            null,
            null,
            null,
            false,
            "Retained daily snapshots (discovery), by complete Pacific/Auckland days",
            reason);

    private static ComparisonCountDifferenceDto BuildCountDifference(int aCount, int bCount)
    {
        var absolute = bCount - aCount;
        if (aCount == 0)
        {
            return new ComparisonCountDifferenceDto(
                aCount,
                bCount,
                absolute,
                null,
                false,
                "Cohort A",
                "Cohort A has no active listings, so only the absolute difference is shown; a relative change has no baseline. It is not infinity and not zero.");
        }

        return new ComparisonCountDifferenceDto(
            aCount,
            bCount,
            absolute,
            Math.Round((decimal)absolute / aCount * 100m, 1, MidpointRounding.AwayFromZero),
            true,
            "Cohort A",
            "Relative difference uses Cohort A's active listing count as the baseline. It is a descriptive difference between cohorts, not a time trend.");
    }

    private static IReadOnlyList<ComparisonSkillDifferenceDto> BuildSkillDifferences(
        CohortResult a,
        CohortResult b)
    {
        var skills = a.SkillCounts.Keys
            .Union(b.SkillCounts.Keys, StringComparer.OrdinalIgnoreCase)
            .Select(skill => new
            {
                Skill = skill,
                ACount = a.SkillCounts.GetValueOrDefault(skill),
                BCount = b.SkillCounts.GetValueOrDefault(skill)
            })
            .OrderByDescending(item => Math.Max(item.ACount, item.BCount))
            .ThenByDescending(item => item.ACount + item.BCount)
            .ThenBy(item => item.Skill, StringComparer.OrdinalIgnoreCase)
            .Take(SkillLimit)
            .ToList();

        return skills
            .Select(item =>
            {
                var aShare = Percent(item.ACount, a.Dto.ActiveListings);
                var bShare = Percent(item.BCount, b.Dto.ActiveListings);
                return new ComparisonSkillDifferenceDto(
                    item.Skill,
                    item.ACount,
                    aShare,
                    item.BCount,
                    bShare,
                    Math.Round(bShare - aShare, 1, MidpointRounding.AwayFromZero),
                    a.Dto.ActiveListings,
                    b.Dto.ActiveListings);
            })
            .ToList();
    }

    private async Task<SkillHistoryDto> BuildSkillHistoryAsync(
        CohortComparisonQuery query,
        IReadOnlyList<ComparisonRow> currentRows,
        IReadOnlyList<SkillSnapshotRow>? snapshotRows,
        ReportingInterval interval,
        ReportingInterval previousInterval,
        int days,
        DateTime now,
        CancellationToken cancellationToken)
    {
        _ = currentRows;
        _ = snapshotRows;
        _ = now;

        if (Clean(query.A.DataSubRole) is not null || Clean(query.B.DataSubRole) is not null)
        {
            return UnavailableSkillHistory(
                "Historical snapshots retain the role family but not the versioned Data & Analytics sub-role, so a skill-share history comparison for a sub-role cohort cannot be reconstructed.",
                0,
                RequiredDays(interval, previousInterval),
                days,
                null,
                null);
        }

        if (Clean(query.A.Skill) is not null || Clean(query.B.Skill) is not null)
        {
            return UnavailableSkillHistory(
                "A skill-filtered cohort cannot provide an unconditional skill-share history; remove the skill filter to compare skill shares over time.",
                0,
                RequiredDays(interval, previousInterval),
                days,
                null,
                null);
        }

        // Round 21: skill capture is additive from the first capture that wrote
        // SkillsCaptured=true. Use only those days; an old row's empty skills are
        // unknown, not zero.
        var snapshots = await dbContext.ListingSnapshots
            .AsNoTracking()
            .Where(snapshot => snapshot.SnapshotDate >= previousInterval.StartLocalDate
                && snapshot.SnapshotDate < interval.EndLocalDateExclusive
                && snapshot.SkillsCaptured)
            .Select(snapshot => new SkillSnapshotRow(
                snapshot.JobId,
                snapshot.ContentHash,
                snapshot.SnapshotDate,
                snapshot.Region,
                snapshot.Location,
                snapshot.RoleFamily,
                snapshot.Seniority,
                snapshot.WorkMode,
                snapshot.IsActive,
                snapshot.SkillsJson))
            .ToListAsync(cancellationToken);

        var capturedDates = snapshots.Select(snapshot => snapshot.SnapshotDate).Distinct().OrderBy(date => date).ToList();
        var requiredDays = RequiredDays(interval, previousInterval);
        if (capturedDates.Count == 0)
        {
            return UnavailableSkillHistory(
                "No snapshot day has captured skills yet, so a current-versus-previous skill-share comparison is unavailable. Snapshot skill capture begins with the first Round 21 capture.",
                0,
                requiredDays,
                days,
                null,
                null);
        }

        var first = capturedDates[0];
        var last = capturedDates[^1];
        if (!Covers(interval, capturedDates) || !Covers(previousInterval, capturedDates))
        {
            return UnavailableSkillHistory(
                $"Skill-share history is unavailable: only {capturedDates.Count} snapshot day(s) captured skills ({first:yyyy-MM-dd} to {last:yyyy-MM-dd}), while the {days}-day current and previous windows need {requiredDays} complete skill-captured days. The earlier window cannot be reconstructed from survivor rows.",
                capturedDates.Count,
                requiredDays,
                days,
                first,
                last);
        }

        var a = ComputePeriodSkills(snapshots, interval, previousInterval, query.A);
        var b = ComputePeriodSkills(snapshots, interval, previousInterval, query.B);
        var skillNames = a.CurrentCounts.Keys
            .Union(b.CurrentCounts.Keys, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(skill => Math.Max(a.CurrentCounts.GetValueOrDefault(skill), b.CurrentCounts.GetValueOrDefault(skill)))
            .ThenBy(skill => skill, StringComparer.OrdinalIgnoreCase)
            .Take(SkillHistoryLimit)
            .ToList();

        var skills = skillNames.Select(skill =>
        {
            var aCurrent = a.CurrentCounts.GetValueOrDefault(skill);
            var aPrevious = a.PreviousCounts.GetValueOrDefault(skill);
            var bCurrent = b.CurrentCounts.GetValueOrDefault(skill);
            var bPrevious = b.PreviousCounts.GetValueOrDefault(skill);
            var aCurrentShare = Percent(aCurrent, a.CurrentListings);
            var bCurrentShare = Percent(bCurrent, b.CurrentListings);
            var aPreviousShare = a.PreviousListings == 0 ? (decimal?)null : Percent(aPrevious, a.PreviousListings);
            var bPreviousShare = b.PreviousListings == 0 ? (decimal?)null : Percent(bPrevious, b.PreviousListings);

            return new SkillHistorySkillDto(
                skill,
                aCurrent,
                aCurrentShare,
                a.PreviousListings == 0 ? null : aPrevious,
                aPreviousShare,
                aPreviousShare is null ? null : Math.Round(aCurrentShare - aPreviousShare.Value, 1, MidpointRounding.AwayFromZero),
                bCurrent,
                bCurrentShare,
                b.PreviousListings == 0 ? null : bPrevious,
                bPreviousShare,
                bPreviousShare is null ? null : Math.Round(bCurrentShare - bPreviousShare.Value, 1, MidpointRounding.AwayFromZero));
        }).ToList();

        return new SkillHistoryDto(
            true,
            null,
            capturedDates.Count,
            requiredDays,
            first.ToString("yyyy-MM-dd"),
            last.ToString("yyyy-MM-dd"),
            days,
            MetricIntervalMapper.ToDto(interval, isPartial: false).Label,
            MetricIntervalMapper.ToDto(previousInterval, isPartial: false).Label,
            "Active listings observed in each complete period; a skill counts at most once per listing per period. This is not a flow of newly posted roles.",
            a.CurrentListings,
            a.PreviousListings,
            b.CurrentListings,
            b.PreviousListings,
            skills);
    }

    private static SkillHistoryDto UnavailableSkillHistory(
        string reason,
        int capturedDays,
        int requiredDays,
        int periodDays,
        DateOnly? first,
        DateOnly? last) =>
        new(
            false,
            reason,
            capturedDays,
            requiredDays,
            first?.ToString("yyyy-MM-dd"),
            last?.ToString("yyyy-MM-dd"),
            periodDays,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            []);

    private static PeriodSkillResult ComputePeriodSkills(
        IReadOnlyList<SkillSnapshotRow> snapshots,
        ReportingInterval currentInterval,
        ReportingInterval previousInterval,
        ComparisonCohortQuery query)
    {
        var current = ComputeOnePeriod(snapshots, currentInterval, query);
        var previous = ComputeOnePeriod(snapshots, previousInterval, query);
        return new PeriodSkillResult(
            current.Listings,
            previous.Listings,
            current.Counts,
            previous.Counts);
    }

    private static OnePeriodSkillResult ComputeOnePeriod(
        IReadOnlyList<SkillSnapshotRow> snapshots,
        ReportingInterval interval,
        ComparisonCohortQuery query)
    {
        var rows = snapshots
            .Where(snapshot => snapshot.SnapshotDate >= interval.StartLocalDate
                && snapshot.SnapshotDate < interval.EndLocalDateExclusive
                && snapshot.IsActive
                && Matches(snapshot, query))
            .ToList();

        var byListing = rows
            .GroupBy(snapshot => string.IsNullOrWhiteSpace(snapshot.ContentHash)
                ? snapshot.JobId.ToString()
                : snapshot.ContentHash, StringComparer.Ordinal)
            .ToList();
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var listing in byListing)
        {
            var skills = listing
                .SelectMany(snapshot => ParseSkills(snapshot.SkillsJson))
                .Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var skill in skills)
            {
                counts[skill] = counts.GetValueOrDefault(skill) + 1;
            }
        }

        return new OnePeriodSkillResult(byListing.Count, counts);
    }

    private async Task<List<ComparisonRow>> LoadRowsAsync(CancellationToken cancellationToken)
    {
        var jobs = await dbContext.Jobs
            .AsNoTracking()
            .Where(job => job.IsActive)
            .Select(job => new ComparisonJob(
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

        var skillsByJob = (await dbContext.JobSkills
                .AsNoTracking()
                .Where(skill => skill.Job != null && skill.Job.IsActive)
                .Select(skill => new { skill.JobId, skill.SkillName })
                .ToListAsync(cancellationToken))
            .GroupBy(skill => skill.JobId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<string>)group
                    .Select(skill => skill.SkillName)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .ToList());

        return jobs.Select(job =>
        {
            var family = RoleFamilyClassifier.Classify(job.Title).Family;
            var match = DataSubRoleClassifier.Classify(job.Title, family, rules);
            return new ComparisonRow(
                job,
                family,
                match,
                skillsByJob.GetValueOrDefault(job.Id) ?? []);
        }).ToList();
    }

    private static bool Matches(ComparisonRow row, ComparisonCohortQuery query)
    {
        var region = Clean(query.Region);
        if (region is not null
            && !string.Equals(row.Job.Region, region, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(row.Job.Location, region, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var family = Clean(query.RoleFamily);
        if (family is not null && !string.Equals(row.Family, family, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var dataSubRole = Clean(query.DataSubRole);
        if (dataSubRole is not null)
        {
            var matchesSubRole = string.Equals(dataSubRole, DataSubRoleClassifier.UncertainKey, StringComparison.OrdinalIgnoreCase)
                ? row.Match.Uncertain
                : string.Equals(row.Match.Key, dataSubRole, StringComparison.OrdinalIgnoreCase);
            if (!matchesSubRole)
            {
                return false;
            }
        }

        var seniority = Clean(query.Seniority);
        if (seniority is not null && !string.Equals(row.Job.Seniority, seniority, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var workMode = Clean(query.WorkMode);
        if (workMode is not null && !string.Equals(row.Job.WorkMode, workMode, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var skill = Clean(query.Skill);
        return skill is null || row.Skills.Contains(skill, StringComparer.OrdinalIgnoreCase);
    }

    private static bool Matches(SkillSnapshotRow row, ComparisonCohortQuery query)
    {
        var region = Clean(query.Region);
        if (region is not null
            && !string.Equals(row.Region, region, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(row.Location, region, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var family = Clean(query.RoleFamily);
        if (family is not null && !string.Equals(row.RoleFamily, family, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var seniority = Clean(query.Seniority);
        if (seniority is not null && !string.Equals(row.Seniority, seniority, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var workMode = Clean(query.WorkMode);
        return workMode is null || string.Equals(row.WorkMode, workMode, StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> ParseSkills(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<string[]>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static bool Covers(ReportingInterval interval, IReadOnlyList<DateOnly> capturedDates)
    {
        var available = capturedDates.ToHashSet();
        for (var date = interval.StartLocalDate; date < interval.EndLocalDateExclusive; date = date.AddDays(1))
        {
            if (!available.Contains(date))
            {
                return false;
            }
        }

        return true;
    }

    private static int RequiredDays(ReportingInterval current, ReportingInterval previous) =>
        LocalDayCount(current) + LocalDayCount(previous);

    private static int LocalDayCount(ReportingInterval interval) =>
        interval.EndLocalDateExclusive.DayNumber - interval.StartLocalDate.DayNumber;

    private static bool SameDefinition(ComparisonCohortQuery a, ComparisonCohortQuery b) =>
        string.Equals(Clean(a.Region), Clean(b.Region), StringComparison.OrdinalIgnoreCase)
        && string.Equals(Clean(a.RoleFamily), Clean(b.RoleFamily), StringComparison.OrdinalIgnoreCase)
        && string.Equals(Clean(a.DataSubRole), Clean(b.DataSubRole), StringComparison.OrdinalIgnoreCase)
        && string.Equals(Clean(a.Seniority), Clean(b.Seniority), StringComparison.OrdinalIgnoreCase)
        && string.Equals(Clean(a.WorkMode), Clean(b.WorkMode), StringComparison.OrdinalIgnoreCase)
        && string.Equals(Clean(a.Skill), Clean(b.Skill), StringComparison.OrdinalIgnoreCase);

    private static string LabelFor(ComparisonCohortQuery query)
    {
        var parts = new List<string>();
        var dataSubRole = Clean(query.DataSubRole);
        var family = Clean(query.RoleFamily);
        if (dataSubRole is not null)
        {
            parts.Add(string.Equals(dataSubRole, DataSubRoleClassifier.UncertainKey, StringComparison.OrdinalIgnoreCase)
                ? "Uncertain data sub-role"
                : DataSubRoleClassifier.RuleFor(dataSubRole)?.Label ?? dataSubRole);
        }
        else if (family is not null)
        {
            parts.Add(family);
        }

        if (Clean(query.Region) is { } region) parts.Add(region);
        if (Clean(query.Seniority) is { } seniority) parts.Add(seniority);
        if (Clean(query.WorkMode) is { } workMode) parts.Add(workMode);
        if (Clean(query.Skill) is { } skill) parts.Add($"skill {skill}");
        return parts.Count == 0 ? "All active listings" : string.Join(" · ", parts);
    }

    private static ComparisonCohortDefinitionDto ToDefinitionDto(ComparisonCohortQuery query)
    {
        var region = Clean(query.Region);
        var family = Clean(query.RoleFamily);
        var dataSubRole = Clean(query.DataSubRole);
        var seniority = Clean(query.Seniority);
        var workMode = Clean(query.WorkMode);
        var skill = Clean(query.Skill);
        return new ComparisonCohortDefinitionDto(
            region,
            family,
            dataSubRole,
            seniority,
            workMode,
            skill,
            region is not null || family is not null || dataSubRole is not null
                || seniority is not null || workMode is not null || skill is not null);
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

    private sealed record ComparisonJob(
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

    private sealed record ComparisonRow(
        ComparisonJob Job,
        string Family,
        DataSubRoleMatch Match,
        IReadOnlyList<string> Skills);

    private sealed record CohortResult(
        ComparisonCohortDto Dto,
        IReadOnlyDictionary<string, int> SkillCounts,
        int ListingCount);

    private sealed record SkillSnapshotRow(
        Guid JobId,
        string ContentHash,
        DateOnly SnapshotDate,
        string? Region,
        string? Location,
        string RoleFamily,
        string Seniority,
        string WorkMode,
        bool IsActive,
        string SkillsJson);

    private sealed record OnePeriodSkillResult(int Listings, IReadOnlyDictionary<string, int> Counts);

    private sealed record PeriodSkillResult(
        int CurrentListings,
        int PreviousListings,
        IReadOnlyDictionary<string, int> CurrentCounts,
        IReadOnlyDictionary<string, int> PreviousCounts);
}
