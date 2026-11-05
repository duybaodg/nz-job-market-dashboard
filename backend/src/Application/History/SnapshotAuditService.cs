using Application.Common;
using Microsoft.EntityFrameworkCore;

namespace Application.History;

public sealed record SnapshotCaptureDayDto(
    DateOnly SnapshotDate,
    DateTime CapturedAtUtc,
    int ListingCount,
    int ActiveCount,
    int SourceCount,
    int SkillsCapturedRows,
    int RowsWithSkills,
    decimal SkillCoveragePercent);

public sealed record RegistryEventDto(
    string SourceName,
    string EventType,
    bool Enabled,
    bool Retired,
    DateOnly EventDate,
    DateTime RecordedAtUtc,
    string Reason);

/// <summary>
/// Small admin/debug surface added in Round 21. It proves the additive capture
/// is actually populating skills without exposing operational crawl errors; the
/// route sits behind the existing admin password gate.
/// </summary>
public sealed record SnapshotCaptureStatusDto(
    DateTime GeneratedAtUtc,
    IReadOnlyList<SnapshotCaptureDayDto> Days,
    int RegistryEventCount,
    int SourcesWithRegistryEvents,
    IReadOnlyList<RegistryEventDto> RecentRegistryEvents);

public interface ISnapshotAuditService
{
    Task<SnapshotCaptureStatusDto> GetCaptureStatusAsync(CancellationToken cancellationToken);
}

public sealed class SnapshotAuditService(IApplicationDbContext dbContext, TimeProvider? timeProvider = null)
    : ISnapshotAuditService
{
    private const int DayLimit = 30;
    private const int EventLimit = 40;
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public async Task<SnapshotCaptureStatusDto> GetCaptureStatusAsync(CancellationToken cancellationToken)
    {
        var runs = await dbContext.ListingSnapshotRuns
            .AsNoTracking()
            .OrderByDescending(run => run.SnapshotDate)
            .Take(DayLimit)
            .ToListAsync(cancellationToken);

        var dates = runs.Select(run => run.SnapshotDate).ToList();
        var snapshotStats = dates.Count == 0
            ? []
            : await dbContext.ListingSnapshots
                .AsNoTracking()
                .Where(snapshot => dates.Contains(snapshot.SnapshotDate))
                .GroupBy(snapshot => snapshot.SnapshotDate)
                .Select(group => new
                {
                    SnapshotDate = group.Key,
                    SkillsCapturedRows = group.Count(snapshot => snapshot.SkillsCaptured),
                    RowsWithSkills = group.Count(snapshot => snapshot.SkillsCaptured && snapshot.SkillsJson != "[]")
                })
                .ToListAsync(cancellationToken);
        var statsByDate = snapshotStats.ToDictionary(row => row.SnapshotDate);

        var days = runs.Select(run =>
        {
            statsByDate.TryGetValue(run.SnapshotDate, out var stats);
            var captured = stats?.SkillsCapturedRows ?? 0;
            var withSkills = stats?.RowsWithSkills ?? 0;
            var percent = captured == 0
                ? 0m
                : Math.Round((decimal)withSkills / captured * 100m, 1, MidpointRounding.AwayFromZero);
            return new SnapshotCaptureDayDto(
                run.SnapshotDate,
                run.CapturedAtUtc,
                run.ListingCount,
                run.ActiveCount,
                run.SourceCount,
                captured,
                withSkills,
                percent);
        }).ToList();

        var registryEvents = await dbContext.RegistryEvents
            .AsNoTracking()
            .OrderByDescending(registryEvent => registryEvent.RecordedAtUtc)
            .Take(EventLimit)
            .Select(registryEvent => new RegistryEventDto(
                registryEvent.SourceName,
                registryEvent.EventType,
                registryEvent.Enabled,
                registryEvent.RetiredAt != null,
                registryEvent.EventDate,
                registryEvent.RecordedAtUtc,
                registryEvent.Reason))
            .ToListAsync(cancellationToken);

        var eventCount = await dbContext.RegistryEvents.CountAsync(cancellationToken);
        var sourceCount = await dbContext.RegistryEvents
            .Select(registryEvent => registryEvent.SourceName)
            .Distinct()
            .CountAsync(cancellationToken);

        return new SnapshotCaptureStatusDto(
            clock.GetUtcNow().UtcDateTime,
            days,
            eventCount,
            sourceCount,
            registryEvents);
    }
}
