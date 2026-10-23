using Domain.Entities;
using FluentAssertions;
using Infrastructure.Crawling;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Application.Tests;

/// <summary>
/// Guards the retirement contract: a row removed from the registry must be
/// marked retired_at (not merely disabled), and a row that returns must be
/// revived. This is the distinction the crawler-health report relies on.
/// </summary>
public sealed class SourceRegistrySynchronizerTests : IDisposable
{
    private static readonly DateTime SeedTime = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly string registryPath = Path.Combine(Path.GetTempPath(), $"sites-{Guid.NewGuid():N}.json");

    [Fact]
    public async Task SyncAsync_retires_rows_absent_from_the_registry_but_not_registry_disabled_rows()
    {
        WriteRegistry(
            Site("Alpha", enabled: true),
            Site("Beta", enabled: false),
            Site("AlreadyOff", enabled: false));
        await using var dbContext = CreateDbContext();
        dbContext.JobSources.AddRange(
            Source("Alpha", enabled: true),
            Source("Beta", enabled: true),
            Source("Removed", enabled: true),
            Source("AlreadyOff", enabled: false));
        await dbContext.SaveChangesAsync();

        var result = await Synchronizer(dbContext).SyncAsync(CancellationToken.None);

        var removed = await dbContext.JobSources.SingleAsync(source => source.Name == "Removed");
        removed.RetiredAt.Should().NotBeNull();
        removed.Enabled.Should().BeFalse();

        // A disabled row that is still in the registry is deliberately disabled,
        // not retired, so it must stay visible to the health report.
        var alreadyOff = await dbContext.JobSources.SingleAsync(source => source.Name == "AlreadyOff");
        alreadyOff.RetiredAt.Should().BeNull();

        var beta = await dbContext.JobSources.SingleAsync(source => source.Name == "Beta");
        beta.RetiredAt.Should().BeNull();
        beta.Enabled.Should().BeFalse();

        result.Retired.Should().Be(1);
        result.Revived.Should().Be(0);
        result.RegistryEntries.Should().Be(3);
    }

    [Fact]
    public async Task SyncAsync_revives_a_source_that_returns_to_the_registry()
    {
        WriteRegistry(Site("Alpha", enabled: true));
        await using var dbContext = CreateDbContext();
        dbContext.JobSources.Add(Source("Alpha", enabled: false, retiredAt: SeedTime));
        await dbContext.SaveChangesAsync();

        var result = await Synchronizer(dbContext).SyncAsync(CancellationToken.None);

        var alpha = await dbContext.JobSources.SingleAsync(source => source.Name == "Alpha");
        alpha.RetiredAt.Should().BeNull();
        alpha.Enabled.Should().BeTrue();
        result.Revived.Should().Be(1);
        result.Retired.Should().Be(0);
    }

    [Fact]
    public async Task SyncAsync_does_not_re_retire_an_already_retired_row()
    {
        WriteRegistry(Site("Alpha", enabled: true));
        await using var dbContext = CreateDbContext();
        dbContext.JobSources.AddRange(
            Source("Alpha", enabled: true),
            Source("Removed", enabled: false, retiredAt: SeedTime));
        await dbContext.SaveChangesAsync();

        var result = await Synchronizer(dbContext).SyncAsync(CancellationToken.None);

        result.Retired.Should().Be(0);
        var removed = await dbContext.JobSources.SingleAsync(source => source.Name == "Removed");
        removed.RetiredAt.Should().Be(SeedTime);
    }

    [Fact]
    public async Task SyncAsync_revokes_approval_when_the_registry_marks_robots_disallowed()
    {
        WriteRegistry(Site("Alpha", enabled: true, robotsAllows: false));
        await using var dbContext = CreateDbContext();
        var source = Source("Alpha", enabled: true);
        source.TermsCheckedAt = SeedTime;
        dbContext.JobSources.Add(source);
        await dbContext.SaveChangesAsync();

        var result = await Synchronizer(dbContext).SyncAsync(CancellationToken.None);

        var alpha = await dbContext.JobSources.SingleAsync(item => item.Name == "Alpha");
        alpha.TermsCheckedAt.Should().BeNull("robotsAllows=false revokes the earlier approval");
        result.Updated.Should().Be(1);
    }

    [Fact]
    public async Task SyncAsync_grants_scheduling_only_to_the_single_exempt_source()
    {
        // The registry keeps robotsAllows=false (truthful), but the scoped
        // allowlist-of-one lets LinkedIn Jobs be scheduled. Any other source
        // with robotsAllows=false must stay unapproved.
        WriteRegistry(
            Site("LinkedIn Jobs", enabled: true, robotsAllows: false),
            Site("Alpha", enabled: true, robotsAllows: false));
        await using var dbContext = CreateDbContext();

        await Synchronizer(dbContext).SyncAsync(CancellationToken.None);

        var linkedin = await dbContext.JobSources.SingleAsync(source => source.Name == "LinkedIn Jobs");
        linkedin.TermsCheckedAt.Should().NotBeNull("the operator's scoped exemption approves scheduling");
        var alpha = await dbContext.JobSources.SingleAsync(source => source.Name == "Alpha");
        alpha.TermsCheckedAt.Should().BeNull("the exemption is an allowlist of one");
    }

    [Fact]
    public async Task SyncAsync_treats_unknown_robots_permission_as_unapproved()
    {
        WriteRegistry(Site("Alpha", enabled: true, robotsAllows: null));
        await using var dbContext = CreateDbContext();
        var source = Source("Alpha", enabled: true);
        source.TermsCheckedAt = SeedTime;
        dbContext.JobSources.Add(source);
        await dbContext.SaveChangesAsync();

        await Synchronizer(dbContext).SyncAsync(CancellationToken.None);

        var alpha = await dbContext.JobSources.SingleAsync(item => item.Name == "Alpha");
        alpha.TermsCheckedAt.Should().BeNull("unknown permission is pending review, not a grant");
    }

    [Fact]
    public async Task SyncAsync_keeps_an_approval_when_robots_are_explicitly_allowed()
    {
        WriteRegistry(Site("Alpha", enabled: true, robotsAllows: true));
        await using var dbContext = CreateDbContext();
        var source = Source("Alpha", enabled: true);
        source.TermsCheckedAt = SeedTime;
        dbContext.JobSources.Add(source);
        await dbContext.SaveChangesAsync();

        await Synchronizer(dbContext).SyncAsync(CancellationToken.None);

        var alpha = await dbContext.JobSources.SingleAsync(item => item.Name == "Alpha");
        alpha.TermsCheckedAt.Should().Be(SeedTime);
    }

    public void Dispose()
    {
        if (File.Exists(registryPath))
        {
            File.Delete(registryPath);
        }
    }

    private SourceRegistrySynchronizer Synchronizer(ApplicationDbContext dbContext)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CRAWLER_SOURCE_REGISTRY"] = registryPath
            })
            .Build();

        return new SourceRegistrySynchronizer(dbContext, configuration, NullLogger<SourceRegistrySynchronizer>.Instance);
    }

    private void WriteRegistry(params object[] sites)
    {
        var entries = sites.Select(site => System.Text.Json.JsonSerializer.Serialize(site));
        File.WriteAllText(registryPath, $"{{\"sites\":[{string.Join(",", entries)}]}}");
    }

    private static object Site(string name, bool enabled, bool? robotsAllows = true) => new
    {
        name,
        homepage = $"https://{name.ToLowerInvariant()}.example/",
        jobs_url = $"https://{name.ToLowerInvariant()}.example/jobs",
        parser = "generic",
        category = "company",
        enabled,
        frequency_minutes = 1440,
        robotsAllows
    };

    private static JobSource Source(string name, bool enabled, DateTime? retiredAt = null) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        BaseUrl = $"https://{name.ToLowerInvariant()}.example/jobs",
        Method = "Scrapling",
        Parser = "generic",
        Enabled = enabled,
        CrawlFrequencyMinutes = 1440,
        RetiredAt = retiredAt,
        CreatedAt = SeedTime,
        UpdatedAt = SeedTime
    };

    private static ApplicationDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        var dbContext = new ApplicationDbContext(options);
        dbContext.Database.EnsureCreated();
        return dbContext;
    }
}
