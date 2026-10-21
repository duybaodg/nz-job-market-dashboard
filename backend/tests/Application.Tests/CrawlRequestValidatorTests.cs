using System.Net;
using Application.Crawling;
using Domain.Entities;
using FluentAssertions;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Application.Tests;

public sealed class CrawlRequestValidatorTests
{
    [Fact]
    public async Task Allows_a_registered_source_and_public_origin()
    {
        await using var db = CreateDbContext();
        AddSource(db, "Example", "https://jobs.example.com/careers");
        await db.SaveChangesAsync();
        var validator = new CrawlRequestValidator(db, new FakeResolver(IPAddress.Parse("203.0.113.10")));

        var result = await validator.ValidateAsync(
            new StartCrawlRunRequest("Example", "https://jobs.example.com/careers/it"),
            CancellationToken.None);

        result.IsAllowed.Should().BeTrue();
    }

    [Fact]
    public async Task Rejects_an_origin_that_does_not_match_the_registered_source()
    {
        await using var db = CreateDbContext();
        AddSource(db, "Example", "https://jobs.example.com/careers");
        await db.SaveChangesAsync();
        var validator = new CrawlRequestValidator(db, new FakeResolver(IPAddress.Parse("203.0.113.10")));

        var result = await validator.ValidateAsync(
            new StartCrawlRunRequest("Example", "https://internal.example.net/admin"),
            CancellationToken.None);

        result.IsAllowed.Should().BeFalse();
        result.Reason.Should().Contain("origin");
    }

    [Theory]
    [InlineData("10.1.2.3")]
    [InlineData("127.0.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("172.20.0.5")]
    [InlineData("192.168.1.10")]
    [InlineData("224.0.0.1")]
    [InlineData("::1")]
    [InlineData("fd00::1")]
    [InlineData("fe80::1")]
    public async Task Rejects_private_loopback_link_local_and_multicast_addresses(string address)
    {
        await using var db = CreateDbContext();
        AddSource(db, "Example", "https://jobs.example.com/careers");
        await db.SaveChangesAsync();
        var validator = new CrawlRequestValidator(db, new FakeResolver(IPAddress.Parse(address)));

        var result = await validator.ValidateAsync(
            new StartCrawlRunRequest("Example", "https://jobs.example.com/careers"),
            CancellationToken.None);

        result.IsAllowed.Should().BeFalse();
        result.Reason.Should().Contain("loopback, private");
    }

    [Fact]
    public async Task Rejects_an_unregistered_source_name()
    {
        await using var db = CreateDbContext();
        AddSource(db, "Example", "https://jobs.example.com/careers");
        await db.SaveChangesAsync();
        var validator = new CrawlRequestValidator(db, new FakeResolver(IPAddress.Parse("203.0.113.10")));

        var result = await validator.ValidateAsync(
            new StartCrawlRunRequest("Firecrawl", "https://jobs.example.com/careers"),
            CancellationToken.None);

        result.IsAllowed.Should().BeFalse();
        result.Reason.Should().Contain("not registered");
    }

    [Theory]
    [InlineData("ftp://jobs.example.com/careers")]
    [InlineData("file:///etc/passwd")]
    [InlineData("not-a-url")]
    public async Task Rejects_non_http_schemes_and_malformed_urls(string url)
    {
        await using var db = CreateDbContext();
        AddSource(db, "Example", "https://jobs.example.com/careers");
        await db.SaveChangesAsync();
        var validator = new CrawlRequestValidator(db, new FakeResolver(IPAddress.Parse("203.0.113.10")));

        var result = await validator.ValidateAsync(new StartCrawlRunRequest("Example", url), CancellationToken.None);

        result.IsAllowed.Should().BeFalse();
    }

    private static void AddSource(ApplicationDbContext db, string name, string baseUrl) =>
        db.JobSources.Add(new JobSource
        {
            Id = Guid.NewGuid(),
            Name = name,
            BaseUrl = baseUrl,
            Method = "Scrapling",
            Parser = "generic",
            Enabled = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });

    private static ApplicationDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ApplicationDbContext(options);
    }

    private sealed class FakeResolver(params IPAddress[] addresses) : ICrawlHostResolver
    {
        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken) =>
            Task.FromResult(addresses);
    }
}
