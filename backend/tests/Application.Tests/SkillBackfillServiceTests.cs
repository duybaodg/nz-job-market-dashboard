using Application.Jobs;
using Domain.Entities;
using FluentAssertions;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Application.Tests;

public sealed class SkillBackfillServiceTests
{
    [Fact]
    public async Task Backfill_enriches_a_skill_less_job_and_is_idempotent()
    {
        await using var dbContext = CreateDbContext();
        var job = CreateJob("https://example.com/jobs/data", summary: "Data platform role.");
        dbContext.Jobs.Add(job);
        dbContext.RawJobPages.Add(CreatePage(
            job.Url,
            "We build on Azure with Kubernetes, Terraform, Docker and Power BI. Python and SQL are essential."));
        await dbContext.SaveChangesAsync();

        var service = new SkillBackfillService(dbContext, SkillExtractor.Default);

        var first = await service.RunAsync(CancellationToken.None);

        first.JobsUpdated.Should().Be(1);
        first.SkillsAdded.Should().BeGreaterThan(0);
        var stored = dbContext.Jobs.Single().Skills.Select(skill => skill.SkillName).ToList();
        stored.Should().Contain(new[] { "Azure", "Kubernetes", "Terraform", "Power BI", "Python", "SQL" });

        var second = await service.RunAsync(CancellationToken.None);

        second.JobsUpdated.Should().Be(0);
        second.SkillsAdded.Should().Be(0);
        second.SkillsRemoved.Should().Be(0);
        dbContext.Jobs.Single().Skills.Select(skill => skill.SkillName)
            .Should().BeEquivalentTo(stored, options => options.WithoutStrictOrdering());
    }

    [Fact]
    public async Task Backfill_replaces_legacy_skills_when_the_full_text_is_retained()
    {
        await using var dbContext = CreateDbContext();
        var job = CreateJob("https://example.com/jobs/legacy", summary: "Java role.");
        job.Skills.Add(new JobSkill
        {
            Id = Guid.NewGuid(),
            JobId = job.Id,
            SkillName = "React",
            SkillType = "Keyword"
        });
        dbContext.Jobs.Add(job);
        dbContext.RawJobPages.Add(CreatePage(job.Url, "Java, Spring Boot and microservices on AWS."));
        await dbContext.SaveChangesAsync();

        var result = await new SkillBackfillService(dbContext, SkillExtractor.Default).RunAsync(CancellationToken.None);

        var stored = dbContext.Jobs.Single().Skills.Select(skill => skill.SkillName).ToList();
        stored.Should().Contain("Java");
        stored.Should().NotContain("React");
        result.SkillsRemoved.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Backfill_unions_when_no_full_text_is_retained_so_skills_never_regress()
    {
        await using var dbContext = CreateDbContext();
        var job = CreateJob("https://example.com/jobs/summary-only", summary: "React and TypeScript.");
        job.Skills.Add(new JobSkill
        {
            Id = Guid.NewGuid(),
            JobId = job.Id,
            SkillName = "Python",
            SkillType = "ProgrammingLanguage"
        });
        dbContext.Jobs.Add(job);
        await dbContext.SaveChangesAsync();

        await new SkillBackfillService(dbContext, SkillExtractor.Default).RunAsync(CancellationToken.None);

        var stored = dbContext.Jobs.Single().Skills.Select(skill => skill.SkillName).ToList();
        stored.Should().Contain(new[] { "Python", "React", "TypeScript" });
    }

    [Fact]
    public async Task Backfill_upgrades_a_mistyped_skill_to_the_canonical_type()
    {
        await using var dbContext = CreateDbContext();
        var job = CreateJob("https://example.com/jobs/type", summary: "Kubernetes platform.");
        job.Skills.Add(new JobSkill
        {
            Id = Guid.NewGuid(),
            JobId = job.Id,
            SkillName = "Kubernetes",
            SkillType = "Keyword"
        });
        dbContext.Jobs.Add(job);
        dbContext.RawJobPages.Add(CreatePage(job.Url, "Kubernetes platform engineering."));
        await dbContext.SaveChangesAsync();

        await new SkillBackfillService(dbContext, SkillExtractor.Default).RunAsync(CancellationToken.None);

        dbContext.Jobs.Single().Skills.Single(skill => skill.SkillName == "Kubernetes")
            .SkillType.Should().Be("Cloud");
    }

    private static Job CreateJob(string url, string? summary) => new()
    {
        Id = Guid.NewGuid(),
        Source = "Test",
        Title = "Data Engineer",
        Url = url,
        ContentHash = Guid.NewGuid().ToString(),
        DescriptionSummary = summary,
        IsActive = true,
        FirstSeenAt = DateTime.UtcNow,
        LastSeenAt = DateTime.UtcNow,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    private static RawJobPage CreatePage(string url, string markdown) => new()
    {
        Id = Guid.NewGuid(),
        Source = "Test",
        Url = url,
        Markdown = markdown,
        CrawledAt = DateTime.UtcNow
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
