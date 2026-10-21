using System.Text.Json;
using Application.Common;
using Application.Employers;
using Application.Jobs;
using Application.Metrics;
using Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Application.History;

public sealed record ListingSnapshotResult(bool Captured, DateOnly SnapshotDate, int Listings, int Active);

public interface IListingSnapshotService
{
    Task<ListingSnapshotResult> CaptureAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Writes one listing-level snapshot per Pacific/Auckland day. Re-running on the
/// same day is a no-op, so the worker can call it on every poll. Snapshot rows
/// are self-contained and survive the job retention purge.
/// </summary>
public sealed class ListingSnapshotService(
    IApplicationDbContext dbContext,
    TimeProvider? timeProvider = null,
    ILogger<ListingSnapshotService>? logger = null) : IListingSnapshotService
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public async Task<ListingSnapshotResult> CaptureAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var snapshotDate = NzReportingCalendar.LocalDate(now);

        var alreadyCaptured = await dbContext.ListingSnapshotRuns
            .AsNoTracking()
            .AnyAsync(run => run.SnapshotDate == snapshotDate, cancellationToken);

        if (alreadyCaptured)
        {
            return new ListingSnapshotResult(false, snapshotDate, 0, 0);
        }

        // Round 15 (H-8): project only the columns a snapshot needs. The full
        // description text is never loaded for this pass.
        var jobs = await dbContext.Jobs
            .AsNoTracking()
            .Select(job => new SnapshotJob(
                job.Id,
                job.Source,
                job.ContentHash,
                job.Title,
                job.Company,
                job.Location,
                job.Region,
                job.WorkMode,
                job.Seniority,
                job.EmploymentType,
                job.SalaryMin,
                job.SalaryMax,
                job.SalaryCurrency,
                job.SalaryPeriod,
                job.PostedDate,
                job.FirstSeenAt,
                job.LastSeenAt,
                job.IsActive))
            .ToListAsync(cancellationToken);
        if (jobs.Count == 0)
        {
            return new ListingSnapshotResult(false, snapshotDate, 0, 0);
        }

        var categories = await LoadSourceCategoriesAsync(cancellationToken);
        var rows = new List<ListingSnapshot>(jobs.Count);

        foreach (var job in jobs)
        {
            categories.TryGetValue(job.Source, out var category);
            var employer = EmployerClassifier.Classify(job.Source, category, job.Company);
            var roleFamily = RoleFamilyClassifier.Classify(job.Title);

            rows.Add(new ListingSnapshot
            {
                Id = Guid.NewGuid(),
                SnapshotDate = snapshotDate,
                JobId = job.Id,
                Source = job.Source,
                SourceCategory = employer.SourceCategory,
                ContentHash = job.ContentHash,
                Title = job.Title,
                Company = job.Company,
                HiringEmployer = employer.HiringEmployer,
                AgencyAdvertised = employer.AdvertiserType == AdvertiserType.Agency,
                Location = job.Location,
                Region = job.Region,
                WorkMode = job.WorkMode,
                Seniority = job.Seniority,
                EmploymentType = job.EmploymentType,
                RoleFamily = roleFamily.Family,
                SalaryMin = job.SalaryMin,
                SalaryMax = job.SalaryMax,
                SalaryCurrency = job.SalaryCurrency,
                SalaryPeriod = job.SalaryPeriod,
                PostedDate = job.PostedDate,
                FirstSeenAt = job.FirstSeenAt,
                LastSeenAt = job.LastSeenAt,
                IsActive = job.IsActive,
                CapturedAtUtc = now
            });
        }

        var activeRows = rows.Where(row => row.IsActive).ToList();
        var coverage = rows
            .GroupBy(row => row.Source, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count());

        dbContext.ListingSnapshots.AddRange(rows);
        dbContext.ListingSnapshotRuns.Add(new ListingSnapshotRun
        {
            Id = Guid.NewGuid(),
            SnapshotDate = snapshotDate,
            CapturedAtUtc = now,
            ListingCount = rows.Count,
            ActiveCount = activeRows.Count,
            SourceCount = coverage.Count,
            IdentifiableEmployerCount = activeRows
                .Where(row => !string.IsNullOrWhiteSpace(row.HiringEmployer))
                .Select(row => row.HiringEmployer!.Trim().ToLowerInvariant())
                .Distinct()
                .Count(),
            SourceCoverageJson = JsonSerializer.Serialize(coverage)
        });

        await dbContext.SaveChangesAsync(cancellationToken);
        logger?.LogInformation(
            "Listing snapshot captured for {Date}: {Listings} listings, {Active} active across {Sources} sources.",
            snapshotDate,
            rows.Count,
            activeRows.Count,
            coverage.Count);

        return new ListingSnapshotResult(true, snapshotDate, rows.Count, activeRows.Count);
    }

    private sealed record SnapshotJob(
        Guid Id,
        string Source,
        string ContentHash,
        string Title,
        string? Company,
        string? Location,
        string? Region,
        string WorkMode,
        string Seniority,
        string EmploymentType,
        decimal? SalaryMin,
        decimal? SalaryMax,
        string SalaryCurrency,
        string SalaryPeriod,
        DateTime? PostedDate,
        DateTime FirstSeenAt,
        DateTime LastSeenAt,
        bool IsActive);

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
}
