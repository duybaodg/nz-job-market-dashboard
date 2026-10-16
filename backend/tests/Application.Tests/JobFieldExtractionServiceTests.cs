using Application.Jobs;
using Domain.Entities;
using FluentAssertions;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Application.Tests;

public sealed class JobFieldExtractionServiceTests
{
    [Fact]
    public async Task Fills_missing_fields_from_the_description()
    {
        await using var dbContext = CreateDbContext();
        var job = CreateJob(
            title: "Software Engineer",
            location: "Auckland CBD",
            region: "Auckland",
            description: "This is a fully remote role. Salary $120,000 - $150,000 per annum. "
                + "Employment type: Contract. You will have 6 years of experience.");
        dbContext.Jobs.Add(job);
        await dbContext.SaveChangesAsync();

        var result = await new JobFieldExtractionService(dbContext).BackfillAsync(CancellationToken.None);

        result.SalaryFilled.Should().Be(1);
        result.WorkModeFilled.Should().Be(1);
        result.EmploymentTypeFilled.Should().Be(1);
        result.SeniorityFilled.Should().Be(1);

        var stored = dbContext.Jobs.Single();
        stored.WorkMode.Should().Be("Remote");
        stored.WorkModeSource.Should().Be("statement");
        stored.SalaryMin.Should().Be(120_000m);
        stored.SalaryMax.Should().Be(150_000m);
        stored.SalarySource.Should().Be("description");
        stored.EmploymentType.Should().Be("Contract");
        stored.Seniority.Should().Be("Senior");
    }

    [Fact]
    public async Task Never_overwrites_a_source_provided_value()
    {
        await using var dbContext = CreateDbContext();
        var job = CreateJob(
            title: "Software Engineer",
            location: "Auckland",
            region: "Auckland",
            description: "Fully remote role. Salary $120,000 - $150,000 per annum. Employment type: Contract.");
        job.WorkMode = "Hybrid";
        job.WorkModeSource = "statement";
        job.EmploymentType = "Full-time";
        job.EmploymentTypeSource = "statement";
        job.Seniority = "Lead";
        job.SenioritySource = "title";
        job.SalaryMin = 130_000m;
        job.SalaryMax = 140_000m;
        job.SalaryText = "$130,000 - $140,000";
        job.SalarySource = "source";
        dbContext.Jobs.Add(job);
        await dbContext.SaveChangesAsync();

        var change = new JobFieldExtractionService(dbContext).ApplyDeterministic(job);

        change.WorkMode.Should().BeFalse();
        change.EmploymentType.Should().BeFalse();
        change.Seniority.Should().BeFalse();
        change.Salary.Should().BeFalse();
        job.WorkMode.Should().Be("Hybrid");
        job.EmploymentType.Should().Be("Full-time");
        job.Seniority.Should().Be("Lead");
        job.SalaryMin.Should().Be(130_000m);
    }

    [Fact]
    public async Task Does_not_re_derive_a_salary_the_audit_marked_as_ignored()
    {
        // Round 11: the audit clears the bogus bounds and marks the retained
        // "64k" text as ignored; the extractor must not resurrect them.
        await using var dbContext = CreateDbContext();
        var job = CreateJob(
            title: "Senior Software Engineer - iOS",
            location: "Auckland",
            region: "Auckland",
            description: "Contract iOS role. Contact +64kayla@potentia.co.nz.");
        job.SalaryMin = null;
        job.SalaryMax = null;
        job.SalaryText = "64k";
        job.SalarySource = "ignored";
        job.SalaryQuestionable = true;
        job.SalaryQuestionableReason = "advertised salary '64k' ignored: it is a non-pay token";
        dbContext.Jobs.Add(job);
        await dbContext.SaveChangesAsync();

        var change = new JobFieldExtractionService(dbContext).ApplyDeterministic(job);

        change.Salary.Should().BeFalse();
        job.SalaryMin.Should().BeNull();
        job.SalaryMax.Should().BeNull();
        job.SalarySource.Should().Be("ignored");
    }

    [Fact]
    public async Task Backfill_is_idempotent()
    {
        await using var dbContext = CreateDbContext();
        dbContext.Jobs.Add(CreateJob(
            title: "Data Analyst",
            location: "Wellington",
            region: "Wellington",
            description: "Hybrid role, 3 days in our Wellington office. Salary $95,000 - $105,000 per annum."));
        await dbContext.SaveChangesAsync();

        var service = new JobFieldExtractionService(dbContext);
        var first = await service.BackfillAsync(CancellationToken.None);
        var second = await service.BackfillAsync(CancellationToken.None);

        first.JobsUpdated.Should().Be(1);
        second.JobsUpdated.Should().Be(0);
        second.SalaryFilled.Should().Be(0);
        second.WorkModeFilled.Should().Be(0);
    }

    [Fact]
    public async Task Records_a_conflict_when_the_description_disagrees_with_the_source_salary()
    {
        await using var dbContext = CreateDbContext();
        var job = CreateJob(
            title: "Engineer",
            location: "Auckland",
            region: "Auckland",
            description: "The salary is $180,000 - $200,000 per annum.");
        job.SalaryMin = 100_000m;
        job.SalaryMax = 110_000m;
        job.SalaryText = "$100,000 - $110,000";
        job.SalarySource = "source";
        dbContext.Jobs.Add(job);
        await dbContext.SaveChangesAsync();

        var service = new JobFieldExtractionService(dbContext);
        var first = await service.BackfillAsync(CancellationToken.None);
        var second = await service.BackfillAsync(CancellationToken.None);

        first.SalaryConflictsNoted.Should().Be(1);
        job.SalaryMin.Should().Be(100_000m);
        job.SalaryQuestionableReason.Should().Contain("source retained");
        second.SalaryConflictsNoted.Should().Be(0);
    }

    [Fact]
    public async Task Deterministic_extraction_supersedes_an_ai_value_when_it_has_evidence()
    {
        await using var dbContext = CreateDbContext();
        var job = CreateJob(
            title: "Engineer",
            location: "Auckland",
            region: "Auckland",
            description: "This is an on-site role based in our Auckland office. "
                + "You will have 3 years of experience.");
        job.WorkMode = "Remote";
        job.WorkModeSource = "ai-enrichment";
        job.WorkModeConfidence = 0.9m;
        job.Seniority = "Lead";
        job.SenioritySource = "ai-enrichment";
        dbContext.Jobs.Add(job);
        await dbContext.SaveChangesAsync();

        new JobFieldExtractionService(dbContext).ApplyDeterministic(job);

        job.WorkMode.Should().Be("On-site");
        job.WorkModeSource.Should().Be("statement");
        job.Seniority.Should().Be("Intermediate");
        job.SenioritySource.Should().Be("description");
    }

    [Fact]
    public async Task An_ai_value_with_no_deterministic_evidence_is_kept()
    {
        await using var dbContext = CreateDbContext();
        var job = CreateJob(
            title: "Engineer",
            location: "Auckland",
            region: "Auckland",
            description: "A great team and interesting work.");
        job.Seniority = "Lead";
        job.SenioritySource = "ai-enrichment";
        dbContext.Jobs.Add(job);
        await dbContext.SaveChangesAsync();

        new JobFieldExtractionService(dbContext).ApplyDeterministic(job);

        job.Seniority.Should().Be("Lead");
        job.SenioritySource.Should().Be("ai-enrichment");
    }

    [Fact]
    public async Task An_internship_title_refines_the_intermediate_default()
    {
        await using var dbContext = CreateDbContext();
        var job = CreateJob(
            title: "Firmware Internship 2026/2027",
            location: null,
            region: "New Zealand",
            description: null);
        dbContext.Jobs.Add(job);
        await dbContext.SaveChangesAsync();

        new JobFieldExtractionService(dbContext).ApplyDeterministic(job);

        job.Seniority.Should().Be("Junior");
        job.SenioritySource.Should().Be("title");
    }

    [Fact]
    public async Task A_zeil_style_navigation_dump_does_not_invent_a_work_mode()
    {
        await using var dbContext = CreateDbContext();
        var job = CreateJob(
            title: "IT Support Engineer",
            location: "Queenstown",
            region: "Otago",
            description: "Edit Advanced Filters Location Work Type Work Style Role Level Salary "
                + "Work Style On Site Remote Hybrid Role Level Intern Entry Level Associate Mid Level Senior");
        dbContext.Jobs.Add(job);
        await dbContext.SaveChangesAsync();

        new JobFieldExtractionService(dbContext).ApplyDeterministic(job);

        // Only the low-confidence location inference is allowed.
        job.WorkMode.Should().Be("On-site");
        job.WorkModeSource.Should().Be("location");
    }

    private static Job CreateJob(string title, string? location, string? region, string? description) => new()
    {
        Id = Guid.NewGuid(),
        Source = "Test",
        Title = title,
        Location = location,
        Region = region,
        DescriptionSummary = description,
        DescriptionText = description,
        Url = $"https://example.com/jobs/{Guid.NewGuid()}",
        ContentHash = Guid.NewGuid().ToString(),
        WorkMode = "Unknown",
        Seniority = "Intermediate",
        EmploymentType = "Unknown",
        SalaryPeriod = "Unknown",
        IsActive = true,
        FirstSeenAt = DateTime.UtcNow,
        LastSeenAt = DateTime.UtcNow,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
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
