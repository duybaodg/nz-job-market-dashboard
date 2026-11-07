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
    public async Task Backfill_applies_round24_go_and_r_guards_and_stays_idempotent()
    {
        // Round 24: re-extraction uses the shared taxonomy for every skill, so
        // the corrected Go/R guards apply without a hard-coded special case.
        await using var dbContext = CreateDbContext();
        var goJob = CreateJob("https://example.com/jobs/go", summary: "Go-to-market strategy and R&D partnership.");
        goJob.DescriptionText = "Backend development experience using technologies such as Go, TypeScript, Java, C#, or similar.";
        var rJob = CreateJob("https://example.com/jobs/r", summary: "Data scientist role.");
        rJob.DescriptionText = "Data scientist: R, Python and SQL for modelling.";
        var falseGoJob = CreateJob("https://example.com/jobs/go-to-market", summary: "Own the go-to-market strategy and R&D roadmap.");
        dbContext.Jobs.AddRange(goJob, rJob, falseGoJob);
        await dbContext.SaveChangesAsync();

        var service = new SkillBackfillService(dbContext, SkillExtractor.Default);

        var first = await service.RunAsync(CancellationToken.None);

        first.JobsUpdated.Should().Be(2);
        dbContext.Jobs.Single(job => job.Id == goJob.Id).Skills.Select(skill => skill.SkillName)
            .Should().Contain("Go");
        dbContext.Jobs.Single(job => job.Id == rJob.Id).Skills.Select(skill => skill.SkillName)
            .Should().Contain("R");
        // The summary's "Go-to-market" and "R&D" must not create those skills.
        dbContext.Jobs.Single(job => job.Id == goJob.Id).Skills.Select(skill => skill.SkillName)
            .Should().NotContain("R");
        dbContext.Jobs.Single(job => job.Id == falseGoJob.Id).Skills.Select(skill => skill.SkillName)
            .Should().NotContain(new[] { "Go", "R" });

        var second = await service.RunAsync(CancellationToken.None);

        second.JobsUpdated.Should().Be(0);
        second.SkillsAdded.Should().Be(0);
        second.SkillsRemoved.Should().Be(0);
    }

    [Fact]
    public async Task Backfill_keeps_valid_legacy_skills_and_adds_new_ones()
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
        dbContext.RawJobPages.Add(CreatePage(job.Url, "React, Java, Spring Boot and microservices on AWS."));
        await dbContext.SaveChangesAsync();

        var result = await new SkillBackfillService(dbContext, SkillExtractor.Default).RunAsync(CancellationToken.None);

        var stored = dbContext.Jobs.Single().Skills.ToList();
        stored.Select(skill => skill.SkillName).Should().Contain(new[] { "React", "Java", "Spring", "AWS" });
        // The legacy row keeps its skill but is upgraded to the canonical type.
        stored.Single(skill => skill.SkillName == "React").SkillType.Should().Be("Framework");
        result.SkillsRemoved.Should().Be(0);
    }

    [Fact]
    public async Task Backfill_unions_skills_from_every_retained_page_not_just_the_newest()
    {
        await using var dbContext = CreateDbContext();
        var job = CreateJob("https://example.com/jobs/multi-page", summary: null);
        dbContext.Jobs.Add(job);
        dbContext.RawJobPages.AddRange(
            CreatePage(job.Url, "Kubernetes platform engineering.", DateTime.UtcNow),
            CreatePage(job.Url, "C# and .NET services.", DateTime.UtcNow.AddDays(-2)));
        await dbContext.SaveChangesAsync();

        await new SkillBackfillService(dbContext, SkillExtractor.Default).RunAsync(CancellationToken.None);

        dbContext.Jobs.Single().Skills.Select(skill => skill.SkillName)
            .Should().Contain(new[] { "Kubernetes", "C#", ".NET" });
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

    [Fact]
    public async Task Backfill_removes_retired_vocabulary_even_without_full_text()
    {
        await using var dbContext = CreateDbContext();
        var job = CreateJob("https://example.com/jobs/retired", summary: "React and TypeScript.");
        job.Skills.Add(new JobSkill
        {
            Id = Guid.NewGuid(),
            JobId = job.Id,
            SkillName = "Retired Skill",
            SkillType = "Keyword"
        });
        dbContext.Jobs.Add(job);
        await dbContext.SaveChangesAsync();

        await new SkillBackfillService(dbContext, SkillExtractor.Default).RunAsync(CancellationToken.None);

        var stored = dbContext.Jobs.Single().Skills.Select(skill => skill.SkillName).ToList();
        stored.Should().NotContain("Retired Skill");
        stored.Should().Contain("React");
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

    private static RawJobPage CreatePage(string url, string markdown, DateTime? crawledAt = null) => new()
    {
        Id = Guid.NewGuid(),
        Source = "Test",
        Url = url,
        Markdown = markdown,
        CrawledAt = crawledAt ?? DateTime.UtcNow
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
