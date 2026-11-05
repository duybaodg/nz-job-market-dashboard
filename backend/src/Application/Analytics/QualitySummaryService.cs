using Application.Analytics;
using Application.Common;
using Application.Jobs;
using Application.Metrics;
using Microsoft.EntityFrameworkCore;

namespace Application.Analytics;

public interface IQualitySummaryService
{
    Task<QualitySummaryDto> GetQualitySummaryAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Release B1: an intentionally limited public quality summary. It reuses the
/// shared overview calculations for salary, employer and dedup status, and adds
/// only coverage counts that can be computed from permitted records. Operational
/// crawl errors and the gated health payload are never included.
/// </summary>
public sealed class QualitySummaryService(
    IApplicationDbContext dbContext,
    IAnalyticsService analyticsService,
    TimeProvider? timeProvider = null,
    IClassificationEvaluationProvider? classificationProvider = null) : IQualitySummaryService
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly IClassificationEvaluationProvider classification = classificationProvider
        ?? new UnavailableClassificationEvaluationProvider();

    public async Task<QualitySummaryDto> GetQualitySummaryAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var overview = await analyticsService.GetOverviewAsync(cancellationToken);
        var activeListings = overview.ActiveListings;

        var activeJobs = await dbContext.Jobs
            .AsNoTracking()
            .Where(job => job.IsActive)
            .Select(job => new { job.Id, job.Title, job.EmployerCanonicalId })
            .ToListAsync(cancellationToken);
        var skillJobIds = (await dbContext.JobSkills
                .AsNoTracking()
                .Where(skill => skill.Job != null && skill.Job.IsActive)
                .Select(skill => skill.JobId)
                .Distinct()
                .ToListAsync(cancellationToken))
            .ToHashSet();

        var unknownRoles = activeJobs.Count(job =>
            string.Equals(RoleFamilyClassifier.Classify(job.Title).Family, RoleFamilyClassifier.UnknownFamily, StringComparison.Ordinal));
        var canonicalEmployers = activeJobs.Count(job => job.EmployerCanonicalId is not null);
        var listingsWithSkill = activeJobs.Count(job => skillJobIds.Contains(job.Id));

        var sourceDenominator = overview.DataStatus.EnabledSources;
        var sourceNumerator = overview.DataStatus.EnabledSourcesWithRecentSuccess;

        var measures = new List<QualityMeasureDto>
        {
            PercentMeasure(
                "source-refresh-coverage",
                "Source refresh coverage",
                sourceNumerator,
                sourceDenominator,
                now,
                "Enabled sources with a successful crawl run in the freshness window.",
                QualityDefinitions.SourceRefreshCoverage,
                sourceDenominator == 0 ? "No enabled sources are registered." : null),
            PercentMeasure(
                "comparable-salary-share",
                "Comparable salary share",
                overview.Salary.EligibleCount,
                activeListings,
                now,
                QualityDefinitions.ActiveCohortScope,
                QualityDefinitions.ComparableSalaryShare,
                activeListings == 0 ? "No active listings were captured." : null),
            PercentMeasure(
                "unknown-role-share",
                "Unknown role share",
                unknownRoles,
                activeListings,
                now,
                QualityDefinitions.ActiveCohortScope,
                QualityDefinitions.UnknownRoleShare,
                activeListings == 0 ? "No active listings were captured." : null),
            PercentMeasure(
                "skill-extraction-coverage",
                "Skill extraction coverage",
                listingsWithSkill,
                activeListings,
                now,
                QualityDefinitions.ActiveCohortScope,
                QualityDefinitions.SkillExtractionCoverage,
                activeListings == 0 ? "No active listings were captured." : null),
            PercentMeasure(
                "employer-resolution",
                "Employer resolution",
                canonicalEmployers,
                activeListings,
                now,
                QualityDefinitions.ActiveCohortScope,
                QualityDefinitions.EmployerResolution,
                activeListings == 0 ? "No active listings were captured." : null),
            DeduplicationMeasure(overview, now),
            await SnapshotMeasureAsync(now, cancellationToken)
        };

        return new QualitySummaryDto(
            now,
            QualityDefinitions.ActiveCohortScope,
            activeListings,
            measures,
            classification.GetEvaluation(),
            "Public quality summary: coverage and completeness only. Every percentage is shown with its numerator and denominator; no accuracy figure is published without a reviewed labelled sample.");    }

    private static QualityMeasureDto PercentMeasure(
        string key,
        string label,
        int numerator,
        int denominator,
        DateTime capturedAtUtc,
        string scope,
        string definition,
        string? unavailableReason)
    {
        var available = unavailableReason is null && denominator > 0;
        var percent = available
            ? Math.Round((decimal)numerator / denominator * 100m, 1, MidpointRounding.AwayFromZero)
            : (decimal?)null;

        return new QualityMeasureDto(
            key,
            label,
            numerator,
            denominator,
            percent,
            available ? $"{numerator:N0} of {denominator:N0} ({percent:0.0}%)" : "Unavailable",
            scope,
            capturedAtUtc,
            available,
            available ? null : unavailableReason ?? "The denominator is zero, so a percentage is not defined.",
            definition);
    }

    private static QualityMeasureDto DeduplicationMeasure(OverviewDto overview, DateTime now)
    {
        var dedup = overview.Deduplication;
        var available = dedup.Status == "validated" && dedup.UniqueVacancies is not null;
        var display = available
            ? $"{dedup.Groups:N0} group(s) · {dedup.ListingsMerged:N0} duplicate row(s) removed · {dedup.ReviewCandidates:N0} review candidate(s)"
            : $"Unavailable — {dedup.UnavailableReason ?? "no validated deduplication pass"}";

        return new QualityMeasureDto(
            "deduplication-status",
            "Deduplication status",
            available ? dedup.UniqueVacancies : null,
            overview.ActiveListings,
            null,
            display,
            "Current active population; only validated groups publish a unique-vacancy count.",
            dedup.LastValidatedAtUtc ?? now,
            available,
            available ? null : dedup.UnavailableReason,
            QualityDefinitions.DeduplicationStatus);
    }

    private async Task<QualityMeasureDto> SnapshotMeasureAsync(DateTime now, CancellationToken cancellationToken)
    {
        var runs = await dbContext.ListingSnapshotRuns
            .AsNoTracking()
            .OrderBy(run => run.SnapshotDate)
            .Select(run => new { run.SnapshotDate, run.CapturedAtUtc })
            .ToListAsync(cancellationToken);

        if (runs.Count == 0)
        {
            return new QualityMeasureDto(
                "snapshot-completeness",
                "Snapshot completeness",
                null,
                null,
                null,
                "Unavailable",
                "Retained daily snapshots.",
                now,
                false,
                "No daily snapshots have been retained yet.",
                QualityDefinitions.SnapshotCompleteness);
        }

        var first = runs[0].SnapshotDate;
        var last = runs[^1].SnapshotDate;
        var expected = last.DayNumber - first.DayNumber + 1;
        var present = runs.Select(run => run.SnapshotDate).ToHashSet();
        var missing = 0;
        for (var date = first; date <= last; date = date.AddDays(1))
        {
            if (!present.Contains(date))
            {
                missing++;
            }
        }

        var percent = Math.Round((decimal)runs.Count / expected * 100m, 1, MidpointRounding.AwayFromZero);
        var display = $"{runs.Count:N0} of {expected:N0} days ({percent:0.0}%)";
        if (missing > 0)
        {
            display += $" · {missing} missing";
        }

        var capturedAt = runs[^1].CapturedAtUtc;
        return new QualityMeasureDto(
            "snapshot-completeness",
            "Snapshot completeness",
            runs.Count,
            expected,
            percent,
            display,
            $"{first:yyyy-MM-dd} to {last:yyyy-MM-dd} Pacific/Auckland snapshot dates.",
            capturedAt,
            true,
            null,
            QualityDefinitions.SnapshotCompleteness);
    }
}
