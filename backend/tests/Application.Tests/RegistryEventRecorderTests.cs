using Application.Crawling;
using Domain.Entities;
using FluentAssertions;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Application.Tests;

public sealed class RegistryEventRecorderTests
{
    private static readonly DateTime Now = new(2026, 11, 6, 0, 30, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Records_one_baseline_event_per_source_then_writes_nothing_when_state_is_unchanged()
    {
        await using var dbContext = CreateDbContext();
        AddSource(dbContext, "Alpha", enabled: true);
        AddSource(dbContext, "Beta", enabled: false);
        await dbContext.SaveChangesAsync();

        var recorder = new RegistryEventRecorder(dbContext, new TestClock(Now));

        (await recorder.RecordAsync(CancellationToken.None)).Should().Be(2);
        var events = await dbContext.RegistryEvents.OrderBy(item => item.SourceName).ToListAsync();
        events.Should().HaveCount(2);
        events.Should().OnlyContain(item => item.EventType == "added");
        // No baseline event exists in this fixture, so the recorder dates the
        // first event from the source row's creation, not from the current day.
        events.Should().OnlyContain(item => item.EventDate == new DateOnly(2026, 10, 27));
        events.Single(item => item.SourceName == "Beta").Enabled.Should().BeFalse();

        (await recorder.RecordAsync(CancellationToken.None)).Should().Be(0);
        (await dbContext.RegistryEvents.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task Appends_enable_disable_and_retire_events_without_rewriting_the_baseline()
    {
        await using var dbContext = CreateDbContext();
        var alpha = AddSource(dbContext, "Alpha", enabled: true);
        dbContext.RegistryEvents.Add(new RegistryEvent
        {
            Id = Guid.NewGuid(),
            SourceName = "Alpha",
            EventType = "state-as-of",
            Enabled = true,
            EventDate = new DateOnly(2026, 11, 5),
            RecordedAtUtc = Now.AddDays(-1),
            Reason = "baseline",
            Actor = "system:migration"
        });
        await dbContext.SaveChangesAsync();

        var recorder = new RegistryEventRecorder(dbContext, new TestClock(Now));

        alpha.Enabled = false;
        await dbContext.SaveChangesAsync();
        (await recorder.RecordAsync(CancellationToken.None)).Should().Be(1);

        alpha.RetiredAt = Now;
        await dbContext.SaveChangesAsync();
        (await new RegistryEventRecorder(dbContext, new TestClock(Now.AddMinutes(1)))
            .RecordAsync(CancellationToken.None)).Should().Be(1);

        alpha.RetiredAt = null;
        alpha.Enabled = true;
        await dbContext.SaveChangesAsync();
        (await new RegistryEventRecorder(dbContext, new TestClock(Now.AddMinutes(2)))
            .RecordAsync(CancellationToken.None)).Should().Be(1);

        var events = await dbContext.RegistryEvents.OrderBy(item => item.RecordedAtUtc).ToListAsync();
        events.Select(item => item.EventType).Should().Equal("state-as-of", "disabled", "retired", "revived");
        events[0].EventDate.Should().Be(new DateOnly(2026, 11, 5), "the dated baseline is never rewritten");
        events.Skip(1).Should().OnlyContain(item => item.EventDate == new DateOnly(2026, 11, 6));
    }

    private static JobSource AddSource(ApplicationDbContext dbContext, string name, bool enabled)
    {
        var source = new JobSource
        {
            Id = Guid.NewGuid(),
            Name = name,
            BaseUrl = $"https://{name.ToLowerInvariant()}.example",
            Method = "Scraping",
            Parser = "generic",
            Category = "company",
            Enabled = enabled,
            CreatedAt = Now.AddDays(-10),
            UpdatedAt = Now.AddDays(-10)
        };
        dbContext.JobSources.Add(source);
        return source;
    }

    private static ApplicationDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ApplicationDbContext(options);
    }
}
