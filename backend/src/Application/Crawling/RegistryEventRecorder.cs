using Application.Common;
using Application.Metrics;
using Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Application.Crawling;

public interface IRegistryEventRecorder
{
    /// <summary>
    /// Appends one dated event per source whose registry state changed since the
    /// latest recorded event. Idempotent: a second run with no state change
    /// writes nothing. Never rewrites or deletes an earlier event.
    /// </summary>
    Task<int> RecordAsync(CancellationToken cancellationToken);
}

public sealed class RegistryEventRecorder(
    IApplicationDbContext dbContext,
    TimeProvider? timeProvider = null,
    ILogger<RegistryEventRecorder>? logger = null) : IRegistryEventRecorder
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public async Task<int> RecordAsync(CancellationToken cancellationToken)
    {
        var sources = await dbContext.JobSources
            .AsNoTracking()
            .OrderBy(source => source.Name)
            .ToListAsync(cancellationToken);
        if (sources.Count == 0)
        {
            return 0;
        }

        var events = await dbContext.RegistryEvents
            .AsNoTracking()
            .OrderBy(registryEvent => registryEvent.RecordedAtUtc)
            .ThenBy(registryEvent => registryEvent.EventDate)
            .ToListAsync(cancellationToken);
        var latestBySource = events
            .GroupBy(registryEvent => registryEvent.SourceName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Last(),
                StringComparer.OrdinalIgnoreCase);

        var now = clock.GetUtcNow().UtcDateTime;
        var today = NzReportingCalendar.LocalDate(now);
        var appended = 0;

        foreach (var source in sources)
        {
            if (!latestBySource.TryGetValue(source.Name, out var previous))
            {
                // No event exists for this source. Use the source row's creation
                // date when it is known: that is when the registry first held the
                // row, not a reconstructed earlier history.
                var createdDate = source.CreatedAt == default
                    ? today
                    : NzReportingCalendar.LocalDate(source.CreatedAt);
                Add(
                    source.Name,
                    "added",
                    source.Enabled,
                    source.RetiredAt,
                    createdDate > today ? today : createdDate,
                    "First recorded registry state: the source is present in the registry and no earlier event exists.",
                    now);
                appended++;
                continue;
            }

            string? eventType = null;
            string reason;
            if (previous.RetiredAt is null && source.RetiredAt is not null)
            {
                eventType = "retired";
                reason = "The source was removed from crawler/sources/sites.json; its retained rows are retired, not hard-deleted.";
            }
            else if (previous.RetiredAt is not null && source.RetiredAt is null)
            {
                eventType = "revived";
                reason = "The source returned to crawler/sources/sites.json and is no longer retired.";
            }
            else if (previous.Enabled != source.Enabled)
            {
                eventType = source.Enabled ? "enabled" : "disabled";
                reason = source.Enabled
                    ? "The source was enabled in the registry by a registry synchronization."
                    : "The source was disabled in the registry by a registry synchronization; historic observations remain.";
            }
            else
            {
                continue;
            }

            Add(source.Name, eventType, source.Enabled, source.RetiredAt, today, reason, now);
            appended++;
        }

        if (appended == 0)
        {
            return 0;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        logger?.LogInformation(
            "Registry event log: appended {Count} state change event(s) across {Sources} sources.",
            appended,
            sources.Count);
        return appended;

        void Add(
            string sourceName,
            string eventType,
            bool enabled,
            DateTime? retiredAt,
            DateOnly eventDate,
            string reason,
            DateTime recordedAtUtc)
        {
            dbContext.RegistryEvents.Add(new RegistryEvent
            {
                Id = Guid.NewGuid(),
                SourceName = sourceName,
                EventType = eventType,
                Enabled = enabled,
                RetiredAt = retiredAt,
                EventDate = eventDate,
                RecordedAtUtc = recordedAtUtc,
                Reason = reason,
                Actor = "system:registry-sync"
            });
        }
    }
}
